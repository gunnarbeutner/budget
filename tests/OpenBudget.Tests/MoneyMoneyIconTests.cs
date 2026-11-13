using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using OpenBudget.Cli;
using OpenBudget.Importers.MoneyMoney;
using OpenBudget.Models;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests;

/// <summary>MoneyMoney's account icons: the mapping onto OpenBudget's, and the backfill of already imported data.</summary>
public sealed class MoneyMoneyIconTests : IAsyncDisposable
{
    private const string Owner = TestDb.DefaultOwner;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"ob-mm-icons-{Guid.NewGuid():N}");
    private readonly TestDb _t = new();

    public async ValueTask DisposeAsync()
    {
        await _t.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { /* best effort */ }
    }

    [Theory]
    [InlineData("AccountIcon5-7", "bi-cash-coin", "#1d4ed8")]
    [InlineData("AccountIcon4-2", "bi-credit-card", "#2a7330")]
    [InlineData("AccountIcon5-8", "bi-coin", "#1d4ed8")]
    [InlineData("AccountIcon5-9", "bi-cash", "#1d4ed8")]
    [InlineData("AccountIcon7-5", "bi-bank", "#4b5563")]
    [InlineData("AccountIcon1-4", "bi-piggy-bank", "#b91c1c")]
    [InlineData("AccountIcon2-6", "bi-wallet2", "#a3480a")]
    [InlineData("AccountIcon3-3", "bi-envelope", "#856404")]
    [InlineData("AccountIcon6", null, "#7e22ce")]
    [InlineData("AccountIcon6-1", null, "#7e22ce")]
    public void Maps_shape_and_colour(string value, string? icon, string color)
    {
        var look = MoneyMoneyIcons.Parse(value)!;
        Assert.Equal(icon, look.Icon);
        Assert.Equal(color, look.Color);
        Assert.True(icon == null || AccountIcons.IsValid(icon));
        Assert.Contains(CategoryColors.Palette, p => p.Key == color);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("AccountIcon8-1")]
    [InlineData("AccountIcon0")]
    [InlineData("AccountIcon5-0")]
    [InlineData("AccountIcon")]
    [InlineData("SomethingElse")]
    public void Ignores_unknown_values(string? value) => Assert.Null(MoneyMoneyIcons.Parse(value));

    private string WriteSource()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "MoneyMoney.sqlite");
        using var conn = new SqliteConnection($"Data Source={path};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE accounts (name TEXT, icon TEXT);
            CREATE TABLE account_groups (name TEXT, icon TEXT);
            INSERT INTO accounts (name, icon) VALUES ('Example Checking', 'AccountIcon4-2');
            INSERT INTO accounts (name, icon) VALUES ('Example Savings', 'AccountIcon5-4');
            INSERT INTO accounts (name, icon) VALUES ('Renamed In MoneyMoney', 'AccountIcon5-9');
            INSERT INTO accounts (name, icon) VALUES ('Example Cash', NULL);
            INSERT INTO accounts (name, icon) VALUES ('Never Imported', 'AccountIcon1');
            INSERT INTO account_groups (name, icon) VALUES ('Example Group', 'AccountIcon7-5');
            """;
        cmd.ExecuteNonQuery();
        return path;
    }

    private async Task<int> SeedAsync()
    {
        await using var db = await _t.System.CreateDbContextAsync();
        int space = (await RowIds.StateAsync(db, Owner)).IdSpace;
        long Id(long rowid) => RowIds.Namespace(space, rowid);
        db.Accounts.AddRange(
            new Account { Id = Id(1), OwnerUserId = Owner, Name = "Example Checking", Type = 1, Currency = "EUR" },
            new Account { Id = Id(2), OwnerUserId = Owner, Name = "Example Savings", Type = 2, Currency = "EUR", Icon = "bi-safe" },
            new Account { Id = Id(3), OwnerUserId = Owner, Name = "Example Other", Type = 1, Currency = "EUR" },
            new Account { Id = Id(4), OwnerUserId = Owner, Name = "Example Cash", Type = 1, Currency = "EUR" });
        db.AccountGroups.Add(new AccountGroup { Id = Id(1), OwnerUserId = Owner, Name = "Example Group", Sort = 1 });
        await db.SaveChangesAsync();
        return space;
    }

    private async Task<(MoneyMoneyIconBackfillResult Result, List<string> Report)> BackfillAsync(string path, bool apply)
    {
        await using var db = await _t.System.CreateDbContextAsync();
        var report = new List<string>();
        return (await MoneyMoneyIconBackfill.RunAsync(db, Owner, path, apply, report), report);
    }

    [Fact]
    public async Task Dry_run_reports_without_changing()
    {
        var path = WriteSource();
        int space = await SeedAsync();

        var (result, report) = await BackfillAsync(path, apply: false);

        Assert.Equal(new MoneyMoneyIconBackfillResult(1, 1, 1, 1, 1), result);
        Assert.Contains(report, l => l.Contains("Renamed In MoneyMoney", StringComparison.Ordinal));
        Assert.Contains("account bi-credit-card #2a7330: 1", report);
        await using var db = await _t.System.CreateDbContextAsync();
        Assert.Null(await db.Accounts.Where(a => a.Id == RowIds.Namespace(space, 1)).Select(a => a.Color).SingleAsync());
    }

    [Fact]
    public async Task Apply_sets_only_unset_matching_rows()
    {
        var path = WriteSource();
        int space = await SeedAsync();

        await BackfillAsync(path, apply: true);

        await using var db = await _t.System.CreateDbContextAsync();
        var accounts = await db.Accounts.Where(a => a.OwnerUserId == Owner).ToDictionaryAsync(a => a.Id);
        Assert.Equal(("bi-credit-card", "#2a7330"), (accounts[RowIds.Namespace(space, 1)].Icon, accounts[RowIds.Namespace(space, 1)].Color));
        Assert.Equal("bi-safe", accounts[RowIds.Namespace(space, 2)].Icon);
        Assert.Null(accounts[RowIds.Namespace(space, 2)].Color);
        Assert.Null(accounts[RowIds.Namespace(space, 3)].Icon);
        Assert.Null(accounts[RowIds.Namespace(space, 4)].Color);
        var group = await db.AccountGroups.SingleAsync(g => g.Id == RowIds.Namespace(space, 1));
        Assert.Equal(("bi-bank", "#4b5563"), (group.Icon, group.Color));
    }

    [Fact]
    public void Tells_plain_from_encrypted_databases()
    {
        var plain = WriteSource();
        var encrypted = Path.Combine(_dir, "encrypted.sqlite");
        MoneyMoneyCipher.Encrypt(plain, encrypted, MoneyMoneyCipher.DefaultKey);
        Assert.True(MoneyMoneyIconsCli.IsPlainSqlite(plain));
        Assert.False(MoneyMoneyIconsCli.IsPlainSqlite(encrypted));
    }
}
