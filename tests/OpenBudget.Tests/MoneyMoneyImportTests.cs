using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OpenBudget.Importers.MoneyMoney;
using OpenBudget.Models;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests;

/// <summary>
/// Import behavior against synthetic MoneyMoney snapshots: per-account provider ownership,
/// pending ("booked = 0") rows and the split-booking remainder rule.
/// </summary>
public sealed class MoneyMoneyImportTests : IDisposable
{
    private const string SourceName = "test";
    private readonly List<string> _files = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in _files)
        {
            try { File.Delete(f); } catch { /* best-effort */ }
        }
    }

    /// <summary>One synthetic transactions row; rowids are assigned in insertion order starting at 1.</summary>
    private sealed record Row(long Account, double Amount, long Splitted = 0, long? SplitKey = null, int Booked = 1, long Ts = 1_780_000_000);

    private string BuildSnapshot(Row[] rows, bool withBooked = true, bool withSplitKey = true, int accounts = 2)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ob-mm-{Guid.NewGuid():N}.sqlite");
        _files.Add(path);
        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        var txCols = "local_account_key INTEGER, amount REAL, currency TEXT, timestamp INTEGER, name TEXT, comment TEXT, unformatted_purpose TEXT, category_key INTEGER, checkmark INTEGER, splitted INTEGER"
            + (withBooked ? ", booked INTEGER" : "")
            + (withSplitKey ? ", split_key INTEGER" : "");
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"""
                CREATE TABLE accounts (name TEXT, iban TEXT, currency TEXT, type INTEGER, portfolio INTEGER, balances TEXT, sort INTEGER, refresh_tsp INTEGER, error TEXT, group_key INTEGER);
                CREATE TABLE account_groups (name TEXT, group_key INTEGER, sort INTEGER);
                CREATE TABLE category_groups (name TEXT, group_key INTEGER, sort INTEGER, color TEXT);
                CREATE TABLE categories (name TEXT, type INTEGER, color TEXT, rules TEXT, group_key INTEGER, sort INTEGER);
                CREATE TABLE transactions ({txCols});
                INSERT INTO categories (name, type) VALUES ('', 1);
                INSERT INTO categories (name, type) VALUES ('Food', 1);
                """;
            cmd.ExecuteNonQuery();
        }
        for (int a = 1; a <= accounts; a++)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO accounts (name, currency, type, balances) VALUES ($n, 'EUR', 1, '')";
            cmd.Parameters.AddWithValue("$n", $"Acct{a}");
            cmd.ExecuteNonQuery();
        }
        foreach (var r in rows)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"""
                INSERT INTO transactions (local_account_key, amount, currency, timestamp, name, category_key, checkmark, splitted{(withBooked ? ", booked" : "")}{(withSplitKey ? ", split_key" : "")})
                VALUES ($a, $amt, 'EUR', $ts, 'x', $cat, 0, $sp{(withBooked ? ", $b" : "")}{(withSplitKey ? ", $sk" : "")})
                """;
            cmd.Parameters.AddWithValue("$a", r.Account);
            cmd.Parameters.AddWithValue("$amt", r.Amount);
            cmd.Parameters.AddWithValue("$ts", r.Ts);
            // Parents carry category 0 in MoneyMoney; everything else is "Food" (rowid 2).
            cmd.Parameters.AddWithValue("$cat", r.Splitted == 1 ? 0 : 2);
            cmd.Parameters.AddWithValue("$sp", r.Splitted);
            if (withBooked) cmd.Parameters.AddWithValue("$b", r.Booked);
            if (withSplitKey) cmd.Parameters.Add(new SqliteParameter("$sk", r.SplitKey is { } sk ? sk : DBNull.Value));
            cmd.ExecuteNonQuery();
        }
        return path;
    }

    private static Task<ImportResult> ImportAsync(TestDb t, string path)
    {
        var svc = new MoneyMoneyImportService(t.Factory, t.Notifier, NullLogger<MoneyMoneyImportService>.Instance);
        return svc.ImportFromFileAsync(SourceName, path, CancellationToken.None);
    }

    private static async Task<List<Transaction>> TransactionsAsync(TestDb t)
    {
        await using var db = await t.Factory.CreateDbContextAsync();
        return await db.Transactions.AsNoTracking().OrderBy(x => x.Id).ToListAsync();
    }

    // ---- split bookings ----

    [Fact]
    public async Task Split_parent_without_children_counts_fully_as_uncategorized()
    {
        await using var t = new TestDb();
        var result = await ImportAsync(t, BuildSnapshot(new[] { new Row(1, -100.00, Splitted: 1) }));

        var tx = Assert.Single(await TransactionsAsync(t));
        Assert.Equal(SplitState.Remainder, tx.Splitted);
        Assert.Equal(-10000, tx.Amount);
        Assert.Equal(-10000, tx.SplitOriginalAmount);
        Assert.Null(tx.CategoryId);

        var uncategorized = await t.TransactionQueries.QueryAsync(new TransactionQuery { SourceId = result.SourceId, UncategorizedOnly = true });
        Assert.Equal(-100m, uncategorized.TotalSum);
        Assert.Equal(1, uncategorized.TotalCount);
    }

    [Fact]
    public async Task Split_parent_with_one_partial_child_leaves_remainder_uncategorized()
    {
        await using var t = new TestDb();
        var result = await ImportAsync(t, BuildSnapshot(new[]
        {
            new Row(1, -100.00, Splitted: 1),
            new Row(1, -30.00, Splitted: 2, SplitKey: 1),
        }));

        var all = await t.TransactionQueries.QueryAsync(new TransactionQuery { SourceId = result.SourceId });
        Assert.Equal(-100m, all.TotalSum); // nothing disappears: 30 child + 70 remainder
        var uncategorized = await t.TransactionQueries.QueryAsync(new TransactionQuery { SourceId = result.SourceId, UncategorizedOnly = true });
        Assert.Equal(-70m, uncategorized.TotalSum);
        var rows = await TransactionsAsync(t);
        var child = rows.Single(r => r.Splitted == SplitState.Child);
        Assert.Equal(SourceScope.Namespace(result.SourceId, 1), child.SplitParentId);
        Assert.Contains(all.Rows, r => r.IsSplitRemainder);
    }

    [Fact]
    public async Task Split_parent_fully_covered_by_children_has_no_remainder_and_stays_excluded()
    {
        await using var t = new TestDb();
        var result = await ImportAsync(t, BuildSnapshot(new[]
        {
            new Row(1, -100.00, Splitted: 1),
            new Row(1, -33.33, Splitted: 2, SplitKey: 1),
            new Row(1, -66.67, Splitted: 2, SplitKey: 1),
        }));

        var rows = await TransactionsAsync(t);
        Assert.Equal(SplitState.Parent, rows.Single(r => r.Id == SourceScope.Namespace(result.SourceId, 1)).Splitted);
        var all = await t.TransactionQueries.QueryAsync(new TransactionQuery { SourceId = result.SourceId });
        Assert.Equal(-100m, all.TotalSum);
        Assert.Equal(2, all.TotalCount);
        var uncategorized = await t.TransactionQueries.QueryAsync(new TransactionQuery { SourceId = result.SourceId, UncategorizedOnly = true });
        Assert.Equal(0, uncategorized.TotalCount);
    }

    [Fact]
    public async Task Split_children_over_covering_the_parent_produce_a_negative_remainder()
    {
        await using var t = new TestDb();
        var result = await ImportAsync(t, BuildSnapshot(new[]
        {
            new Row(1, -100.00, Splitted: 1),
            new Row(1, -60.00, Splitted: 2, SplitKey: 1),
            new Row(1, -50.00, Splitted: 2, SplitKey: 1),
        }));

        var uncategorized = await t.TransactionQueries.QueryAsync(new TransactionQuery { SourceId = result.SourceId, UncategorizedOnly = true });
        Assert.Equal(10m, uncategorized.TotalSum); // -100 - (-110)
        var all = await t.TransactionQueries.QueryAsync(new TransactionQuery { SourceId = result.SourceId });
        Assert.Equal(-100m, all.TotalSum);
    }

    [Fact]
    public async Task Snapshot_without_split_key_keeps_parents_excluded()
    {
        await using var t = new TestDb();
        var result = await ImportAsync(t, BuildSnapshot(new[]
        {
            new Row(1, -100.00, Splitted: 1),
            new Row(1, -100.00, Splitted: 2),
        }, withSplitKey: false));

        var rows = await TransactionsAsync(t);
        Assert.Equal(SplitState.Parent, rows[0].Splitted);
        var all = await t.TransactionQueries.QueryAsync(new TransactionQuery { SourceId = result.SourceId });
        Assert.Equal(-100m, all.TotalSum);
    }

    // ---- pending bookings ----

    [Fact]
    public async Task Booked_flag_is_imported_and_defaults_to_booked_when_the_column_is_missing()
    {
        await using var t = new TestDb();
        var result = await ImportAsync(t, BuildSnapshot(new[] { new Row(1, -1, Booked: 1), new Row(1, -2, Booked: 0) }));
        var rows = await TransactionsAsync(t);
        Assert.True(rows[0].Booked);
        Assert.False(rows[1].Booked);

        var page = await t.TransactionQueries.QueryAsync(new TransactionQuery { SourceId = result.SourceId });
        Assert.Equal(-3m, page.TotalSum); // pending rows count toward totals
        Assert.Single(page.Rows, r => r.IsPending);

        await ImportAsync(t, BuildSnapshot(new[] { new Row(1, -1), new Row(1, -2) }, withBooked: false));
        Assert.All(await TransactionsAsync(t), r => Assert.True(r.Booked));
    }

    [Fact]
    public async Task Newest_booked_date_ignores_pending_rows()
    {
        await using var t = new TestDb();
        var result = await ImportAsync(t, BuildSnapshot(new[]
        {
            new Row(1, -1, Ts: 1_000_000),
            new Row(1, -2, Ts: 2_000_000),
            new Row(1, -3, Ts: 3_000_000, Booked: 0),
        }));
        var accountId = SourceScope.Namespace(result.SourceId, 1);

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(2_000_000).UtcDateTime, await t.Accounts.GetNewestBookedDateAsync(accountId));
        Assert.Null(await t.Accounts.GetNewestBookedDateAsync(SourceScope.Namespace(result.SourceId, 2)));
    }

    // ---- per-account provider ----

    [Fact]
    public async Task Reimport_keeps_other_providers_transactions_and_provider_values()
    {
        await using var t = new TestDb();
        var first = await ImportAsync(t, BuildSnapshot(new[] { new Row(1, -1), new Row(2, -2) }));
        var acct1 = SourceScope.Namespace(first.SourceId, 1);
        var acct2 = SourceScope.Namespace(first.SourceId, 2);

        // Hand account 2 over to a native connector and give it a transaction of its own.
        Assert.True(await t.Accounts.SetProviderAsync(acct2, "native"));
        await using (var db = await t.Factory.CreateDbContextAsync())
        {
            db.Transactions.Add(new Transaction { Id = 9_999, SourceId = first.SourceId, AccountId = acct2, Amount = -777, Timestamp = 1 });
            await db.SaveChangesAsync();
        }

        // New snapshot: account 1 gets a different transaction, account 2 still has MoneyMoney rows.
        await ImportAsync(t, BuildSnapshot(new[] { new Row(1, -5), new Row(2, -6), new Row(2, -7) }));

        var rows = await TransactionsAsync(t);
        // Account 1 was replaced (-1 -> -5); account 2 keeps everything it had (the earlier
        // MoneyMoney row -2 and the native one) and ignores the snapshot's new rows -6 / -7.
        Assert.Equal(new long[] { -777, -500, -200 }, rows.Select(r => r.Amount).ToArray());
        Assert.Single(rows, r => r.AccountId == acct2 && r.Id == 9_999);

        await using var check = await t.Factory.CreateDbContextAsync();
        var providers = await check.Accounts.ToDictionaryAsync(a => a.Id, a => a.Provider);
        Assert.Equal(AccountProviders.MoneyMoney, providers[acct1]);
        Assert.Equal("native", providers[acct2]);
    }

    [Fact]
    public async Task Moneymoney_accounts_default_to_the_moneymoney_provider_and_have_their_transactions_replaced()
    {
        await using var t = new TestDb();
        await ImportAsync(t, BuildSnapshot(new[] { new Row(1, -1) }));
        await ImportAsync(t, BuildSnapshot(new[] { new Row(1, -2), new Row(1, -3) }));

        Assert.Equal(2, (await TransactionsAsync(t)).Count);
        await using var db = await t.Factory.CreateDbContextAsync();
        Assert.All(await db.Accounts.ToListAsync(), a => Assert.Equal(AccountProviders.MoneyMoney, a.Provider));
    }
}
