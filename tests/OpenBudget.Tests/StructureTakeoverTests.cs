using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OpenBudget.Importers.MoneyMoney;
using OpenBudget.Models;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests;

/// <summary>
/// The structure takeover at the end of a browser MoneyMoney import: it marks the user and freezes the order of their
/// accounts and categories.
/// </summary>
public sealed class StructureTakeoverTests : IDisposable
{
    private const string SourceName = AccessWorld.Alice;
    private readonly List<string> _files = [];

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in _files)
        {
            try { File.Delete(f); } catch (IOException) { /* best-effort */ }
        }
    }

    private sealed record Acct(string Name, long Group, long Sort, double Balance = 10);
    private sealed record Cat(string Name, long Group, long? Sort);
    private sealed record Tx(long Account, long Category, double Amount, string? Comment = null, long Checkmark = 0);

    /// <summary>
    /// Builds a MoneyMoney-shaped snapshot. Account groups: rowid 1 "Banks". Category groups: 1 = hidden root,
    /// 2 = "Household". Categories: rowid 1 = sentinel, then the given ones in order (rowid 2, 3, ...).
    /// </summary>
    private string Snapshot(Acct[] accounts, Cat[] categories, Tx[] transactions, string groupName = "Banks", string householdName = "Household")
    {
        var path = Path.Combine(Path.GetTempPath(), $"ob-take-{Guid.NewGuid():N}.sqlite");
        _files.Add(path);
        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        Exec(conn, """
            CREATE TABLE accounts (name TEXT, iban TEXT, currency TEXT, type INTEGER, portfolio INTEGER, balances TEXT, sort INTEGER, refresh_tsp INTEGER, error TEXT, group_key INTEGER);
            CREATE TABLE account_groups (name TEXT, group_key INTEGER, sort INTEGER);
            CREATE TABLE category_groups (name TEXT, group_key INTEGER, sort INTEGER, color TEXT);
            CREATE TABLE categories (name TEXT, type INTEGER, color TEXT, rules TEXT, group_key INTEGER, sort INTEGER);
            CREATE TABLE transactions (local_account_key INTEGER, amount REAL, currency TEXT, timestamp INTEGER, name TEXT, comment TEXT, unformatted_purpose TEXT, category_key INTEGER, checkmark INTEGER, splitted INTEGER, booked INTEGER, split_key INTEGER);
            INSERT INTO category_groups (name, group_key, sort) VALUES ('', 0, 0);
            INSERT INTO category_groups (name, group_key, sort, color) VALUES ($household, 1, 5, '1,2,3');
            INSERT INTO categories (name, type, group_key) VALUES ('', 1, 1);
            """, ("$household", householdName));
        Exec(conn, "INSERT INTO account_groups (name, group_key, sort) VALUES ($n, 0, 0)", ("$n", groupName));
        foreach (var a in accounts)
        {
            Exec(conn, "INSERT INTO accounts (name, iban, currency, type, balances, sort, group_key) VALUES ($n, 'DE00', 'EUR', 1, $b, $s, $g)",
                ("$n", a.Name), ("$b", $"11:{a.Balance:F6},3:EUR,"), ("$s", a.Sort), ("$g", a.Group));
        }
        foreach (var c in categories)
        {
            Exec(conn, "INSERT INTO categories (name, type, color, rules, group_key, sort) VALUES ($n, 1, '9,9,9', 'payee contains x', $g, $s)",
                ("$n", c.Name), ("$g", c.Group), ("$s", c.Sort is { } s ? s : DBNull.Value));
        }
        foreach (var x in transactions)
        {
            Exec(conn, """
                INSERT INTO transactions (local_account_key, amount, currency, timestamp, name, comment, category_key, checkmark, splitted, booked)
                VALUES ($a, $amt, 'EUR', 1780000000, 'Example Shop', $c, $cat, $ck, 0, 1)
                """, ("$a", x.Account), ("$amt", x.Amount), ("$c", (object?)x.Comment ?? DBNull.Value), ("$cat", x.Category), ("$ck", x.Checkmark));
        }
        return path;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Test-only helper; every statement is a literal in this file and values are bound as parameters.")]
    private static void Exec(SqliteConnection conn, string sql, params (string Name, object Value)[] args)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }

    private static async Task<TestDb> WorldAsync()
    {
        var t = new TestDb();
        t.AddUser(AccessWorld.Alice, "alice", admin: true);
        t.AddUser(AccessWorld.Bob, "bob");
        t.AddIdSpace(AccessWorld.Alice, AccessWorld.SourceAlice);
        await t.Db.SaveChangesAsync();
        return t;
    }

    private static MoneyMoneyImportService Importer(TestDb t) =>
        new(t.Factory, t.Notifier, NullLogger<MoneyMoneyImportService>.Instance);

    private static Task<ImportResult> ImportAsync(TestDb t, string path) => Importer(t).ImportFromFileAsync(SourceName, path, CancellationToken.None);

    private static StructureTakeoverService Takeover(TestDb t) =>
        new(t.System, t.Notifier, new ChangeOrigin(), Importer(t));

    private static long Id(long rowid) => RowIds.Namespace(AccessWorld.SourceAlice, rowid);

    private static readonly Acct[] TwoAccounts = [new("Giro", 1, 7), new("Savings", 1, 3)];
    private static readonly Cat[] TwoCategories = [new("Groceries", 2, null), new("Rent", 2, 1)];

    [Fact]
    public async Task Takeover_marks_the_source_and_freezes_the_order()
    {
        await using var t = await WorldAsync();
        await ImportAsync(t, Snapshot(TwoAccounts, TwoCategories, [new Tx(1, 2, -5)]));

        var result = await Takeover(t).TakeOverAsync(AccessWorld.Alice);

        Assert.NotNull(result);
        Assert.Equal(2, result.Accounts);
        await using var db = await t.Factory.CreateDbContextAsync();
        Assert.NotNull((await db.UserDataStates.SingleAsync(s => s.UserId == AccessWorld.Alice)).StructureTakenOverAt);
        // Savings (sort 3) precedes Giro (sort 7); explicit consecutive positions now.
        Assert.Equal(0, (await db.Accounts.SingleAsync(a => a.Name == "Savings")).Sort);
        Assert.Equal(1, (await db.Accounts.SingleAsync(a => a.Name == "Giro")).Sort);
        // Rent (sort 1) precedes Groceries (sort null = last).
        Assert.Equal(0, (await db.Categories.SingleAsync(c => c.Name == "Rent")).Sort);
        Assert.Equal(1, (await db.Categories.SingleAsync(c => c.Name == "Groceries")).Sort);
    }

    [Fact]
    public async Task Category_colors_are_editable_by_the_owner_after_the_takeover()
    {
        await using var t = await WorldAsync();
        await ImportAsync(t, Snapshot(TwoAccounts, TwoCategories, []));
        var rent = Id(3);   // sentinel = rowid 1, Groceries = 2, Rent = 3
        var colors = new CategoryColorService(t.ForUser(AccessWorld.Alice).Factory, t.Notifier, new ChangeOrigin());

        await Takeover(t).TakeOverAsync(AccessWorld.Alice);
        Assert.True(await colors.CanEditAsync(AccessWorld.Alice));
        Assert.True(await colors.SetColorAsync(rent, "#2B5797"));
        await Assert.ThrowsAsync<ArgumentException>(() => colors.SetColorAsync(rent, "blue"));

        var bob = new CategoryColorService(t.ForUser(AccessWorld.Bob).Factory, t.Notifier, new ChangeOrigin());
        Assert.False(await bob.CanEditAsync(AccessWorld.Alice));

        await using (var db = await t.Factory.CreateDbContextAsync())
            Assert.Equal("#2b5797", (await db.Categories.SingleAsync(c => c.Id == rent)).Color);
    }

    [Fact]
    public async Task A_second_takeover_is_a_no_op()
    {
        await using var t = await WorldAsync();
        await ImportAsync(t, Snapshot(TwoAccounts, TwoCategories, []));
        var service = Takeover(t);
        Assert.NotNull(await service.TakeOverAsync(AccessWorld.Alice));
        Assert.Null(await service.TakeOverAsync(AccessWorld.Alice));
    }
}
