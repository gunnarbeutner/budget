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
    private const string SourceName = TestDb.DefaultOwner;
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
    private sealed record Row(long Account, double Amount, long Splitted = 0, long? SplitKey = null, int Booked = 1, long Ts = 1_780_000_000, long? Category = null);

    private string BuildSnapshot(Row[] rows, bool withBooked = true, bool withSplitKey = true, int accounts = 2, string? foodRules = null)
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
        if (foodRules != null)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE categories SET rules = $r WHERE name = 'Food'";
            cmd.Parameters.AddWithValue("$r", foodRules);
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
            cmd.Parameters.AddWithValue("$cat", r.Category ?? (r.Splitted == 1 ? 0 : 2));
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

        var uncategorized = await t.TransactionQueries.QueryAsync(new TransactionQuery { OwnerUserId = result.OwnerUserId, UncategorizedOnly = true });
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

        var all = await t.TransactionQueries.QueryAsync(new TransactionQuery { OwnerUserId = result.OwnerUserId });
        Assert.Equal(-100m, all.TotalSum); // nothing disappears: 30 child + 70 remainder
        var uncategorized = await t.TransactionQueries.QueryAsync(new TransactionQuery { OwnerUserId = result.OwnerUserId, UncategorizedOnly = true });
        Assert.Equal(-70m, uncategorized.TotalSum);
        var rows = await TransactionsAsync(t);
        var child = rows.Single(r => r.Splitted == SplitState.Child);
        Assert.Equal(RowIds.Namespace(1, 1), child.SplitParentId);
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
        Assert.Equal(SplitState.Parent, rows.Single(r => r.Id == RowIds.Namespace(1, 1)).Splitted);
        var all = await t.TransactionQueries.QueryAsync(new TransactionQuery { OwnerUserId = result.OwnerUserId });
        Assert.Equal(-100m, all.TotalSum);
        Assert.Equal(2, all.TotalCount);
        var uncategorized = await t.TransactionQueries.QueryAsync(new TransactionQuery { OwnerUserId = result.OwnerUserId, UncategorizedOnly = true });
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

        var uncategorized = await t.TransactionQueries.QueryAsync(new TransactionQuery { OwnerUserId = result.OwnerUserId, UncategorizedOnly = true });
        Assert.Equal(10m, uncategorized.TotalSum); // -100 - (-110)
        var all = await t.TransactionQueries.QueryAsync(new TransactionQuery { OwnerUserId = result.OwnerUserId });
        Assert.Equal(-100m, all.TotalSum);
    }

    [Fact]
    public async Task Split_children_with_splitted_zero_are_linked_by_split_key_and_not_double_counted()
    {
        await using var t = new TestDb();
        var result = await ImportAsync(t, BuildSnapshot(new[]
        {
            new Row(1, -715.00, Splitted: 1),
            new Row(1, -500.00, Splitted: 0, SplitKey: 1),
            new Row(1, -215.00, Splitted: 0, SplitKey: 1),
            new Row(1, -20.00, Splitted: 0, SplitKey: 99), // dangling key: stays a normal row
        }));

        var rows = await TransactionsAsync(t);
        Assert.Equal(SplitState.Parent, rows.Single(r => r.Id == RowIds.Namespace(1, 1)).Splitted);
        var children = rows.Where(r => r.Splitted == SplitState.Child).ToList();
        Assert.Equal(2, children.Count);
        Assert.All(children, c => Assert.Equal(RowIds.Namespace(1, 1), c.SplitParentId));
        var dangling = rows.Single(r => r.Id == RowIds.Namespace(1, 4));
        Assert.Equal(SplitState.Normal, dangling.Splitted);
        Assert.Null(dangling.SplitParentId);

        var all = await t.TransactionQueries.QueryAsync(new TransactionQuery { OwnerUserId = result.OwnerUserId });
        Assert.Equal(-735m, all.TotalSum);
        var uncategorized = await t.TransactionQueries.QueryAsync(new TransactionQuery { OwnerUserId = result.OwnerUserId, UncategorizedOnly = true });
        Assert.Equal(0, uncategorized.TotalCount);
    }

    [Theory]
    [InlineData(-70.00, -30.00)]  // children under-cover the parent
    [InlineData(-110.00, 10.00)]  // children over-cover the parent
    public async Task Split_remainder_keeps_the_parents_category(double childSum, double expectedRemainder)
    {
        await using var t = new TestDb();
        var result = await ImportAsync(t, BuildSnapshot(new[]
        {
            new Row(1, -100.00, Splitted: 1, Category: 2),
            new Row(1, childSum, Splitted: 2, SplitKey: 1),
        }));

        var remainder = (await TransactionsAsync(t)).Single(r => r.Splitted == SplitState.Remainder);
        Assert.Equal(MoneyMath.EurosToCents(expectedRemainder), remainder.Amount);
        Assert.Equal(RowIds.Namespace(1, 2), remainder.CategoryId);
        var uncategorized = await t.TransactionQueries.QueryAsync(new TransactionQuery { OwnerUserId = result.OwnerUserId, UncategorizedOnly = true });
        Assert.Equal(0, uncategorized.TotalCount);
        var all = await t.TransactionQueries.QueryAsync(new TransactionQuery { OwnerUserId = result.OwnerUserId });
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
        var all = await t.TransactionQueries.QueryAsync(new TransactionQuery { OwnerUserId = result.OwnerUserId });
        Assert.Equal(-100m, all.TotalSum);
    }

    // ---- pending bookings ----

    [Fact]
    public async Task Booked_flag_is_imported()
    {
        await using var t = new TestDb();
        var result = await ImportAsync(t, BuildSnapshot(new[] { new Row(1, -1, Booked: 1), new Row(1, -2, Booked: 0) }));
        var rows = await TransactionsAsync(t);
        Assert.True(rows[0].Booked);
        Assert.False(rows[1].Booked);

        var page = await t.TransactionQueries.QueryAsync(new TransactionQuery { OwnerUserId = result.OwnerUserId });
        Assert.Equal(-3m, page.TotalSum); // pending rows count toward totals
        Assert.Single(page.Rows, r => r.IsPending);

    }

    [Fact]
    public async Task Rows_count_as_booked_when_the_column_is_missing()
    {
        await using var t = new TestDb();
        await ImportAsync(t, BuildSnapshot(new[] { new Row(1, -1), new Row(1, -2) }, withBooked: false));
        Assert.All(await TransactionsAsync(t), r => Assert.True(r.Booked));
    }

    [Fact]
    public async Task Newest_booked_date_ignores_pending_rows()
    {
        await using var t = new TestDb();
        await ImportAsync(t, BuildSnapshot(new[]
        {
            new Row(1, -1, Ts: 1_000_000),
            new Row(1, -2, Ts: 2_000_000),
            new Row(1, -3, Ts: 3_000_000, Booked: 0),
        }));
        var accountId = RowIds.Namespace(1, 1);

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(2_000_000).UtcDateTime, await t.Accounts.GetNewestBookedDateAsync(accountId));
        Assert.Null(await t.Accounts.GetNewestBookedDateAsync(RowIds.Namespace(1, 2)));
    }

    // ---- per-account provider ----

    [Fact]
    public async Task Imported_accounts_are_manual_and_a_second_import_is_refused()
    {
        await using var t = new TestDb();
        await ImportAsync(t, BuildSnapshot(new[] { new Row(1, -1), new Row(2, -2) }));

        await Assert.ThrowsAsync<MoneyMoneyAlreadyImportedException>(() => ImportAsync(t, BuildSnapshot(new[] { new Row(1, -2), new Row(1, -3) })));

        Assert.Equal(new long[] { -200, -100 }, (await TransactionsAsync(t)).Select(r => r.Amount).OrderBy(a => a).ToArray());
        await using var db = await t.Factory.CreateDbContextAsync();
        var accounts = await db.Accounts.ToListAsync();
        Assert.Equal(2, accounts.Count);
        Assert.All(accounts, a => Assert.Equal(AccountProviders.Manual, a.Provider));
        Assert.All(accounts, a => Assert.True(RowIds.IsImported(a.Id)));
    }

    // ---- category rules ----

    private static async Task<List<CategoryRule>> RulesAsync(TestDb t)
    {
        await using var db = await t.Factory.CreateDbContextAsync();
        return await db.CategoryRules.AsNoTracking().OrderBy(r => r.Id).ToListAsync();
    }

    /// <summary>
    /// Gives the imported food category <paramref name="foodRules"/> as MoneyMoney's rule text and runs the rule sync
    /// (as the import and the startup backfill do).
    /// </summary>
    private static async Task SyncRulesAsync(TestDb t, string ownerId, string? foodRules)
    {
        await using var db = await t.Factory.CreateDbContextAsync();
        await db.Categories.Where(c => c.Id == RowIds.Namespace(1, 2)).ExecuteUpdateAsync(u => u.SetProperty(c => c.Rules, foodRules));
        await MoneyMoneyRuleImport.SyncAsync(db, ownerId, CancellationToken.None);
    }

    [Fact]
    public async Task Category_rules_become_project_rules_idempotently_and_respect_user_edits()
    {
        await using var t = new TestDb();
        const string first = "name:\"Example Market\" OR bakery";
        var result = await ImportAsync(t, BuildSnapshot([new Row(1, -1)], foodRules: first));
        Assert.Empty(await RulesAsync(t)); // no project yet
        await using (var db = await t.Factory.CreateDbContextAsync())
        {
            db.Projects.Add(new Project { Name = "Household", OwnerUserId = result.OwnerUserId });
            await db.SaveChangesAsync();
        }

        await SyncRulesAsync(t, result.OwnerUserId, first);
        var rule = Assert.Single(await RulesAsync(t));
        Assert.Equal(first, rule.Expression);
        Assert.Equal(MoneyMoneyRuleImport.Origin, rule.ImportOrigin);
        Assert.Equal(RowIds.Namespace(1, 2), rule.CategoryId);

        // Same again: no duplicate. Changed upstream: updated in place.
        await SyncRulesAsync(t, result.OwnerUserId, first);
        Assert.Single(await RulesAsync(t));
        const string second = "name:\"Example Market\"";
        await SyncRulesAsync(t, result.OwnerUserId, second);
        Assert.Equal(second, Assert.Single(await RulesAsync(t)).Expression);

        // Edited here: later syncs leave it alone.
        await using (var db = await t.Factory.CreateDbContextAsync())
        {
            (await db.CategoryRules.SingleAsync()).Expression = "sample";
            await db.SaveChangesAsync();
        }
        await SyncRulesAsync(t, result.OwnerUserId, first);
        Assert.Equal("sample", Assert.Single(await RulesAsync(t)).Expression);
    }

    [Fact]
    public async Task A_dismissed_imported_rule_is_not_recreated_and_an_untouched_one_follows_an_emptied_rule()
    {
        await using var t = new TestDb();
        var result = await ImportAsync(t, BuildSnapshot([new Row(1, -1)]));
        await using (var db = await t.Factory.CreateDbContextAsync())
        {
            db.Projects.Add(new Project { Name = "Household", OwnerUserId = result.OwnerUserId });
            await db.SaveChangesAsync();
        }
        await SyncRulesAsync(t, result.OwnerUserId, "bakery");
        Assert.Single(await RulesAsync(t));
        await SyncRulesAsync(t, result.OwnerUserId, "");
        Assert.Empty(await RulesAsync(t));

        await SyncRulesAsync(t, result.OwnerUserId, "bakery");
        await using (var db = await t.Factory.CreateDbContextAsync())
        {
            var r = await db.CategoryRules.SingleAsync();
            r.Dismissed = true;
            await db.SaveChangesAsync();
        }
        await SyncRulesAsync(t, result.OwnerUserId, "bakery OR market");
        var only = Assert.Single(await RulesAsync(t));
        Assert.True(only.Dismissed);
        Assert.Equal("bakery", only.Expression);
    }

    // ---- detail fields ----

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100", Justification = "Test-only constant SQL.")]
    private static void Exec(string path, string sql)
    {
        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public async Task Detail_columns_of_an_extended_schema_are_copied()
    {
        await using var t = new TestDb();
        var path = BuildSnapshot(new[] { new Row(1, -10), new Row(1, -20) });
        Exec(path, """
            ALTER TABLE transactions ADD COLUMN value_date INTEGER;
            ALTER TABLE transactions ADD COLUMN account_number TEXT;
            ALTER TABLE transactions ADD COLUMN bank_code TEXT;
            ALTER TABLE transactions ADD COLUMN booking_text TEXT;
            ALTER TABLE transactions ADD COLUMN end_to_end_reference TEXT;
            ALTER TABLE transactions ADD COLUMN mandate_reference TEXT;
            ALTER TABLE transactions ADD COLUMN creditor_id TEXT;
            ALTER TABLE transactions ADD COLUMN transaction_code INTEGER;
            ALTER TABLE transactions ADD COLUMN primanota TEXT;
            ALTER TABLE transactions ADD COLUMN return_reason TEXT;
            ALTER TABLE transactions ADD COLUMN some_future_column TEXT;
            UPDATE transactions SET value_date = 1780086400, account_number = 'DE00123456780000000001', bank_code = 'EXAMPLEXXXX',
                booking_text = 'Lastschrift', end_to_end_reference = 'E2E-1', mandate_reference = 'M-42', creditor_id = 'DE00ZZZ0000001',
                transaction_code = 105, primanota = '9310', return_reason = 'MD06', some_future_column = 'ignored'
            WHERE rowid = 1;
            UPDATE transactions SET unformatted_purpose = 'EREF+FROMPURPOSE MREF+M-7 CRED+DE11ZZZ0000002 SVWZ+Example' WHERE rowid = 2;
            """);
        await ImportAsync(t, path);
        var rows = await TransactionsAsync(t);

        var full = rows[0];
        Assert.Equal(1780086400, full.ValueTimestamp);
        Assert.Equal("DE00123456780000000001", full.CounterpartyIban);
        Assert.Equal("EXAMPLEXXXX", full.CounterpartyBic);
        Assert.Equal("Lastschrift", full.BookingText);
        Assert.Equal("E2E-1", full.EndToEndReference);
        Assert.Equal("M-42", full.MandateReference);
        Assert.Equal("DE00ZZZ0000001", full.CreditorId);
        Assert.Equal("105", full.TransactionCode);
        Assert.Equal("9310", full.Primanota);
        Assert.Equal("MD06", full.ReturnReason);

        // Empty columns: the SEPA references come from the purpose's tags.
        var tagged = rows[1];
        Assert.Equal("FROMPURPOSE", tagged.EndToEndReference);
        Assert.Equal("M-7", tagged.MandateReference);
        Assert.Equal("DE11ZZZ0000002", tagged.CreditorId);
        Assert.Null(tagged.ValueTimestamp);
        Assert.Null(tagged.CounterpartyIban);

        var page = await t.TransactionQueries.QueryAsync(new TransactionQuery { OwnerUserId = rows[0].OwnerUserId });
        Assert.Contains(page.Rows, r => r.MandateReference == "M-42" && r.TransactionCode == "105" && r.ReturnReason == "MD06");
    }

    [Fact]
    public async Task A_minimal_old_schema_still_imports_without_detail_fields()
    {
        await using var t = new TestDb();
        var path = Path.Combine(Path.GetTempPath(), $"ob-mm-{Guid.NewGuid():N}.sqlite");
        _files.Add(path);
        Exec(path, """
            CREATE TABLE accounts (name TEXT, iban TEXT, currency TEXT, type INTEGER, portfolio INTEGER, balances TEXT, sort INTEGER, refresh_tsp INTEGER, error TEXT, group_key INTEGER);
            CREATE TABLE category_groups (name TEXT, group_key INTEGER, sort INTEGER, color TEXT);
            CREATE TABLE categories (name TEXT, type INTEGER, color TEXT, rules TEXT, group_key INTEGER, sort INTEGER);
            CREATE TABLE transactions (local_account_key INTEGER, amount REAL, currency TEXT, timestamp INTEGER, name TEXT, comment TEXT, category_key INTEGER, checkmark INTEGER, splitted INTEGER);
            INSERT INTO accounts (name, currency, type, balances) VALUES ('Acct1', 'EUR', 1, '');
            INSERT INTO transactions (local_account_key, amount, currency, timestamp, name, checkmark, splitted) VALUES (1, -5, 'EUR', 1780000000, 'Example Shop', 0, 0);
            """);
        var result = await ImportAsync(t, path);
        Assert.Equal(1, result.TransactionsImported);
        var row = Assert.Single(await TransactionsAsync(t));
        Assert.Equal(-500, row.Amount);
        Assert.Null(row.Purpose);
        Assert.Null(row.MandateReference);
        Assert.Null(row.ValueTimestamp);
    }

    [Fact]
    public async Task An_account_closed_in_MoneyMoney_is_imported_closed()
    {
        await using var t = new TestDb();
        var path = Path.Combine(Path.GetTempPath(), $"ob-mm-{Guid.NewGuid():N}.sqlite");
        _files.Add(path);
        Exec(path, """
            CREATE TABLE accounts (name TEXT, iban TEXT, currency TEXT, type INTEGER, portfolio INTEGER, balances TEXT, sort INTEGER, refresh_tsp INTEGER, error TEXT, group_key INTEGER, refresh INTEGER);
            CREATE TABLE category_groups (name TEXT, group_key INTEGER, sort INTEGER, color TEXT);
            CREATE TABLE categories (name TEXT, type INTEGER, color TEXT, rules TEXT, group_key INTEGER, sort INTEGER);
            CREATE TABLE transactions (local_account_key INTEGER, amount REAL, currency TEXT, timestamp INTEGER, name TEXT, comment TEXT, category_key INTEGER, checkmark INTEGER, splitted INTEGER);
            INSERT INTO accounts (name, currency, type, balances, refresh) VALUES ('Open account', 'EUR', 1, '', 3);
            INSERT INTO accounts (name, currency, type, balances, refresh) VALUES ('Closed account', 'EUR', 1, '', 19);
            INSERT INTO accounts (name, currency, type, balances, refresh) VALUES ('Offline account', 'EUR', 6, '', 0);
            """);
        var project = t.AddProject(1, "Default");
        project.IsDefaultForNewAccounts = true;
        await t.Db.SaveChangesAsync();
        await ImportAsync(t, path);
        await using var db = await t.Factory.CreateDbContextAsync();
        var closed = await db.Accounts.IgnoreQueryFilters().OrderBy(a => a.Id).Select(a => new { a.Name, a.Closed }).ToListAsync();
        Assert.Equal(["Open account:False", "Closed account:True", "Offline account:False"], closed.Select(a => $"{a.Name}:{a.Closed}"));
        // All of them land in the default project, but a closed account never counts for the cashflow.
        var assigned = await db.ProjectAccountAssignments.OrderBy(a => a.AccountId).Select(a => a.InCashflow).ToListAsync();
        Assert.Equal([true, false, true], assigned);
    }

    [Fact]
    public async Task MoneyMoney_initial_balances_are_imported_in_cents_or_derived_from_the_reported_balance()
    {
        await using var t = new TestDb();
        var path = Path.Combine(Path.GetTempPath(), $"ob-mm-{Guid.NewGuid():N}.sqlite");
        _files.Add(path);
        Exec(path, """
            CREATE TABLE accounts (name TEXT, iban TEXT, currency TEXT, type INTEGER, portfolio INTEGER, balances TEXT, sort INTEGER, refresh_tsp INTEGER, error TEXT, group_key INTEGER, initial_balance REAL);
            CREATE TABLE category_groups (name TEXT, group_key INTEGER, sort INTEGER, color TEXT);
            CREATE TABLE categories (name TEXT, type INTEGER, color TEXT, rules TEXT, group_key INTEGER, sort INTEGER);
            CREATE TABLE transactions (local_account_key INTEGER, amount REAL, currency TEXT, timestamp INTEGER, name TEXT, comment TEXT, category_key INTEGER, checkmark INTEGER, splitted INTEGER);
            INSERT INTO accounts (name, currency, type, balances, initial_balance) VALUES ('Ledger', 'EUR', 3, '', 1234.56);
            INSERT INTO accounts (name, currency, type, balances, initial_balance) VALUES ('Giro', 'EUR', 1, '', NULL);
            INSERT INTO accounts (name, currency, type, balances, initial_balance) VALUES ('Extension ledger', 'EUR', 3, '8:0.000000,3:EUR,', NULL);
            INSERT INTO transactions (local_account_key, amount, currency, timestamp, name, checkmark, splitted) VALUES (3, -270.5, 'EUR', 1780000000, 'Purchase', 0, 0);
            """);
        await ImportAsync(t, path);
        await using var db = await t.Factory.CreateDbContextAsync();
        var initial = await db.Accounts.IgnoreQueryFilters().OrderBy(a => a.Id).Select(a => a.InitialBalance).ToListAsync();
        Assert.Equal([123456L, 0L, 27050L], initial);
    }
}
