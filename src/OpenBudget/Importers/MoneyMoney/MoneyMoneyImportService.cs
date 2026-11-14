using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using OpenBudget.Connectors.Sync;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.Importers.MoneyMoney;

/// <summary>
/// Imports a MoneyMoney SQLite file as a user's data. Reads the MoneyMoney DB read-only via raw
/// <see cref="SqliteDataReader"/> for speed (EF AddRange is much slower at hundreds-of-thousands of
/// transactions), numbers every rowid in the user's id space (<see cref="RowIds.Namespace"/>), and
/// copies the data inside one EF transaction so partial failures roll back cleanly. The import is a one-time copy: its
/// accounts are manual accounts (<see cref="AccountProviders.Manual"/>) that bank connections can take over, and a
/// further import for a user who already imported is refused (<see cref="MoneyMoneyAlreadyImportedException"/>).
///
/// Registered as a singleton so the per-user serialization semaphore is shared across concurrent imports.
/// </summary>
public class MoneyMoneyImportService
{
    private static readonly string[] ExpectedAccountColumns =
    {
        "name", "iban", "currency", "type", "portfolio", "balances", "sort", "refresh_tsp", "error", "group_key"
    };
    private static readonly string[] ExpectedAccountGroupColumns =
    {
        "name", "group_key", "sort"
    };
    private static readonly string[] ExpectedCategoryColumns =
    {
        "name", "type", "color", "rules", "group_key", "sort"
    };
    private static readonly string[] ExpectedCategoryGroupColumns =
    {
        "name", "group_key", "sort", "color"
    };
    private static readonly string[] ExpectedTransactionColumns =
    {
        "local_account_key", "amount", "currency", "timestamp", "name", "comment",
        "unformatted_purpose", "category_key", "checkmark", "splitted"
    };
    private static readonly string[] OptionalTransactionColumns = { "booked", "split_key" };
    private static readonly string[] ExpectedSecurityColumns =
    {
        "account_key", "ident", "isin", "wpn", "symbol", "quantity", "amount", "amount_cur",
        "price", "price_cur", "purchase", "asset_class", "deleted"
    };
    private static readonly string[] ExpectedPortfolioChartColumns =
    {
        "account_key", "currency", "timestamp", "balance", "securities_key"
    };

    // Bound parameter names for each table's INSERT, hoisted out of the per-table copy calls so
    // the arrays aren't reallocated on every import (CA1861); they mirror the VALUES lists below.
    private static readonly string[] AccountInsertParameters =
    {
        "$id", "$sid", "$name", "$iban", "$currency", "$type", "$portfolio", "$balances", "$sort", "$refresh_tsp", "$error", "$group_key", "$provider", "$closed", "$icon", "$color"
    };
    private static readonly string[] AccountGroupInsertParameters =
    {
        "$id", "$sid", "$name", "$group_key", "$sort", "$icon", "$color"
    };
    private static readonly string[] CategoryGroupInsertParameters =
    {
        "$id", "$sid", "$name", "$group_key", "$sort", "$color"
    };
    private static readonly string[] CategoryInsertParameters =
    {
        "$id", "$sid", "$name", "$type", "$color", "$rules", "$group_key", "$sort"
    };
    private static readonly string[] TransactionInsertParameters =
    {
        "$id", "$sid", "$local_account_key", "$amount", "$currency", "$timestamp", "$name", "$comment", "$purpose", "$category_key", "$checkmark", "$splitted", "$booked", "$split_parent_id", "$split_original_amount", "$note", "$checked",
        "$value_timestamp", "$counterparty_iban", "$counterparty_bic", "$booking_text", "$bank_reference", "$end_to_end_reference", "$mandate_reference", "$creditor_id", "$transaction_code", "$primanota", "$return_reason"
    };

    /// <summary>
    /// MoneyMoney's detail fields of a booking, by our column, with the MoneyMoney column names that may hold it (first
    /// one present wins). MoneyMoney's database schema is not documented: the names follow its AppleScript / export
    /// field names (accountNumber, bankCode, valueDate, bookingText, endToEndReference, mandateReference, creditorId,
    /// transactionCode, primaNota, returnReason) in the snake_case its known columns use; the import probes them at
    /// runtime and copies whatever exists, so an unknown name just stays empty.
    /// </summary>
    internal static readonly (string Column, bool IsDate, string[] Candidates)[] TransactionDetailColumns =
    {
        ("value_timestamp", true, new[] { "value_date", "value_timestamp", "valuedate", "value_day" }),
        ("counterparty_iban", false, new[] { "account_number", "remote_account_number", "remote_account", "counterparty_account_number", "iban", "account" }),
        ("counterparty_bic", false, new[] { "bank_code", "remote_bank_code", "counterparty_bank_code", "bic", "bankcode" }),
        ("booking_text", false, new[] { "booking_text", "bookingtext", "transaction_type", "unformatted_type" }),
        ("bank_reference", false, new[] { "bank_reference", "transaction_reference", "reference", "kref" }),
        ("end_to_end_reference", false, new[] { "end_to_end_reference", "endtoend_reference", "end_to_end_id", "eref" }),
        ("mandate_reference", false, new[] { "mandate_reference", "mandate_id", "mref" }),
        ("creditor_id", false, new[] { "creditor_id", "creditor_identifier", "cred" }),
        ("transaction_code", false, new[] { "transaction_code", "business_transaction_code", "gvc" }),
        ("primanota", false, new[] { "primanota", "prima_nota", "pn" }),
        ("return_reason", false, new[] { "return_reason", "rtrn", "rtcd" }),
    };
    private static readonly string[] SecurityInsertParameters =
    {
        "$id", "$sid", "$account_key", "$name", "$isin", "$wkn", "$symbol", "$quantity", "$amount", "$amount_cur", "$price", "$price_cur", "$purchase", "$asset_class"
    };
    private static readonly string[] PortfolioChartInsertParameters =
    {
        "$id", "$sid", "$account_key", "$currency", "$timestamp", "$balance", "$securities_key"
    };

    private readonly IDbContextFactory<BudgetDbContext> _dbFactory;
    private readonly ChangeNotificationService _notifier;
    private readonly ILogger<MoneyMoneyImportService> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _perOwnerLocks = new(StringComparer.Ordinal);

    public MoneyMoneyImportService(
        IDbContextFactory<BudgetDbContext> dbFactory,
        ChangeNotificationService notifier,
        ILogger<MoneyMoneyImportService> logger)
    {
        _dbFactory = dbFactory;
        _notifier = notifier;
        _logger = logger;
    }

    /// <summary>Imports the MoneyMoney database at <paramref name="sqliteFilePath"/> as data of <paramref name="ownerUserId"/>.</summary>
    public async Task<ImportResult> ImportFromFileAsync(string ownerUserId, string sqliteFilePath, CancellationToken ct)
    {
        var sem = _perOwnerLocks.GetOrAdd(ownerUserId, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct);
        try
        {
            return await ImportInternalAsync(ownerUserId, sqliteFilePath, ct);
        }
        finally
        {
            sem.Release();
        }
    }

    /// <summary>The explanation a refused import gets once the owner imported from MoneyMoney.</summary>
    public const string AlreadyImportedMessage = "The MoneyMoney data was already imported; the import runs only once.";

    /// <summary>Runs <paramref name="action"/> while no import for the user is running (and blocks imports meanwhile).</summary>
    public async Task<T> WithOwnerLockAsync<T>(string ownerUserId, Func<Task<T>> action, CancellationToken ct)
    {
        var sem = _perOwnerLocks.GetOrAdd(ownerUserId, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct);
        try
        {
            return await action();
        }
        finally
        {
            sem.Release();
        }
    }

    private async Task<ImportResult> ImportInternalAsync(string ownerUserId, string sqliteFilePath, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        if (!await db.Users.AnyAsync(u => u.Id == ownerUserId, ct)) throw new ArgumentException("Unknown user.", nameof(ownerUserId));
        var source = await RowIds.StateAsync(db, ownerUserId, ct);
        string ownerId = source.UserId;
        int idSpace = source.IdSpace;
        if (source.LastUploadAt != null) throw new MoneyMoneyAlreadyImportedException();

        var connStr = new SqliteConnectionStringBuilder
        {
            DataSource = sqliteFilePath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString();

        await using var src = new SqliteConnection(connStr);
        await src.OpenAsync(ct);

        WarnOnSchemaDrift(src, ownerId);

        // MoneyMoney's source DB has dangling rowid refs (e.g. category_groups with group_key=0).
        // Our imported tables declare FKs that EF generated from the model, so we silence enforcement
        // on this connection for the duration of the import. PRAGMA must be set outside any
        // transaction or it's silently ignored.
        var destConn = (SqliteConnection)db.Database.GetDbConnection();
        if (destConn.State != System.Data.ConnectionState.Open)
            await destConn.OpenAsync(ct);
        await using (var pragmaOff = destConn.CreateCommand())
        {
            pragmaOff.CommandText = "PRAGMA foreign_keys = OFF";
            await pragmaOff.ExecuteNonQueryAsync(ct);
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            // Clear leftovers in MoneyMoney's half of the owner's id space before the copy. Order matters: transactions
            // and securities reference accounts/categories via FKs (Restrict), so they go first. Accounts reference
            // account_groups, so accounts go before account_groups. Rows OpenBudget or a connector created live in the
            // connector half and stay, with their transactions, positions and value history.
            long lo = RowIds.Namespace(idSpace, 0), hi = RowIds.Namespace(idSpace, BankTransactionStore.ConnectorRowBase);
            var existingAccountIds = (await db.Accounts
                .Where(a => a.OwnerUserId == ownerId && (a.Id < lo || a.Id >= hi))
                .Select(a => a.Id)
                .ToListAsync(ct)).ToHashSet();
            await db.Database.ExecuteSqlRawAsync(
                """
                DELETE FROM transactions
                WHERE OwnerUserId = {0}
                  AND (local_account_key IS NULL
                       OR local_account_key NOT IN (SELECT Id FROM accounts WHERE OwnerUserId = {0} AND (Id < {1} OR Id >= {2})))
                """,
                new object[] { ownerId, lo, hi }, ct);
            await db.Database.ExecuteSqlRawAsync(
                """
                DELETE FROM portfolio_chart
                WHERE OwnerUserId = {0}
                  AND account_key NOT IN (SELECT Id FROM accounts WHERE OwnerUserId = {0} AND (Id < {1} OR Id >= {2}))
                """,
                new object[] { ownerId, lo, hi }, ct);
            await db.Database.ExecuteSqlRawAsync(
                """
                DELETE FROM holdings
                WHERE OwnerUserId = {0}
                  AND account_key NOT IN (SELECT Id FROM accounts WHERE OwnerUserId = {0} AND (Id < {1} OR Id >= {2}))
                """,
                new object[] { ownerId, lo, hi }, ct);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM categories WHERE OwnerUserId = {0} AND Id >= {1} AND Id < {2}", new object[] { ownerId, lo, hi }, ct);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM category_groups WHERE OwnerUserId = {0} AND Id >= {1} AND Id < {2}", new object[] { ownerId, lo, hi }, ct);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM accounts WHERE OwnerUserId = {0} AND Id >= {1} AND Id < {2}", new object[] { ownerId, lo, hi }, ct);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM account_groups WHERE OwnerUserId = {0} AND Id >= {1} AND Id < {2}", new object[] { ownerId, lo, hi }, ct);

            int accountGroupCount = await CopyAccountGroupsAsync(src, db, ownerId, idSpace, ct);
            int accountCount = await CopyAccountsAsync(src, db, ownerId, idSpace, ct);
            int groupCount = await CopyCategoryGroupsAsync(src, db, ownerId, idSpace, ct);
            int categoryCount = await CopyCategoriesAsync(src, db, ownerId, idSpace, ct);
            var (restrictedCount, unknownRestrictions) = await CopyCategoryRestrictionsAsync(src, db, ownerId, idSpace, ct);
            // Column names only (no data): which MoneyMoney fields this upload offers for the detail mapping.
            _logger.LogInformation("MoneyMoney transactions columns: {Columns}",
                string.Join(", ", (await ColumnsAsync(src, "transactions", ct)).Order(StringComparer.OrdinalIgnoreCase)));
            _logger.LogInformation("MoneyMoney accounts columns: {Columns}",
                string.Join(", ", (await ColumnsAsync(src, "accounts", ct)).Order(StringComparer.OrdinalIgnoreCase)));
            int txCount = await CopyTransactionsAsync(src, db, ownerId, idSpace, existingAccountIds, ct);
            await CopyInitialBalancesAsync(src, db, ownerId, idSpace, ct);
            int securityCount = await CopySecuritiesAsync(src, db, ownerId, idSpace, existingAccountIds, ct);
            int portfolioChartCount = await CopyPortfolioChartAsync(src, db, ownerId, idSpace, existingAccountIds, ct);

            long fileSize = new FileInfo(sqliteFilePath).Length;
            source.LastUploadAt = DateTime.UtcNow;
            source.LastUploadSizeBytes = fileSize;
            await db.SaveChangesAsync(ct);

            // Accounts whose id was not among the owner's before are new: put them in the owner's
            // default project for new accounts (if any). Existing accounts and assignments are untouched.
            var newAccountIds = await db.Accounts
                .Where(a => a.OwnerUserId == ownerId)
                .Select(a => a.Id)
                .ToListAsync(ct);
            newAccountIds.RemoveAll(existingAccountIds.Contains);
            int autoAssigned = await DefaultProjectAssigner.AssignNewAccountsAsync(db, ownerId, newAccountIds, ct);

            // MoneyMoney's category rules become (or update) the projects' categorisation rules.
            int rulesChanged = await MoneyMoneyRuleImport.SyncAsync(db, ownerId, ct);

            // FKs were enforced-off for the copy; validate the final state before committing so a
            // dangling reference (i.e. an import bug) is surfaced rather than silently persisted.
            await LogForeignKeyViolationsAsync(destConn, db.Database.CurrentTransaction?.GetDbTransaction() as SqliteTransaction, ownerId, ct);

            await tx.CommitAsync(ct);

            _logger.LogInformation(
                "Imported MoneyMoney data of user {OwnerUserId}: {Accounts} accounts, {AccountGroups} account groups, {Groups} groups, {Categories} categories, {Transactions} transactions, {Securities} securities, {PortfolioChart} portfolio-chart points ({Bytes} bytes)",
                ownerId, accountCount, accountGroupCount, groupCount, categoryCount, txCount, securityCount, portfolioChartCount, fileSize);

            // Tell every open browser session their imported data changed so pages reload —
            // a fresh import moves balances and spending on every project of this user.
            _notifier.Publish(new DataChange(DataArea.ImportedData, OwnerUserId: ownerId));
            _notifier.Publish(new DataChange(DataArea.ImportState, OwnerUserId: ownerId));
            if (autoAssigned > 0 || rulesChanged > 0) _notifier.Publish(new DataChange(DataArea.Projects));

            return new ImportResult
            {
                OwnerUserId = ownerId,
                AccountsImported = accountCount,
                AccountGroupsImported = accountGroupCount,
                CategoryGroupsImported = groupCount,
                CategoriesImported = categoryCount,
                TransactionsImported = txCount,
                SecuritiesImported = securityCount,
                PortfolioChartPointsImported = portfolioChartCount,
                CategoriesRestricted = restrictedCount,
                CategoryRestrictionsUnknown = unknownRestrictions,
                FileSizeBytes = fileSize
            };
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
        finally
        {
            // Re-enable FK enforcement so EF's subsequent SaveChanges operations behave normally.
            await using var pragmaOn = destConn.CreateCommand();
            pragmaOn.CommandText = "PRAGMA foreign_keys = ON";
            await pragmaOn.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>
    /// Runs PRAGMA foreign_key_check over the destination and logs any dangling references left
    /// after the copy. foreign_key_check works regardless of the foreign_keys enforcement pragma,
    /// so it catches integrity bugs even though enforcement was off during the import.
    /// </summary>
    private async Task LogForeignKeyViolationsAsync(
        SqliteConnection destConn, SqliteTransaction? tx, string ownerId, CancellationToken ct)
    {
        await using var cmd = destConn.CreateCommand();
        if (tx != null) cmd.Transaction = tx;
        cmd.CommandText = "PRAGMA foreign_key_check";
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        int violations = 0;
        while (await reader.ReadAsync(ct))
        {
            violations++;
            var table = await reader.IsDBNullAsync(0, ct) ? "?" : reader.GetString(0);
            var rowid = await reader.IsDBNullAsync(1, ct) ? "?" : reader.GetValue(1).ToString();
            var parent = await reader.IsDBNullAsync(2, ct) ? "?" : reader.GetString(2);
            _logger.LogWarning(
                "FK violation after importing for user {Owner}: {Table} row {RowId} references missing {Parent}.",
                ownerId, table, rowid, parent);
        }
        if (violations > 0)
            _logger.LogWarning(
                "Import for user {Owner} left {Count} dangling foreign-key reference(s); data was committed but may render incorrectly.",
                ownerId, violations);
    }

    private void WarnOnSchemaDrift(SqliteConnection src, string ownerId)
    {
        WarnIfMissing(src, "accounts", ExpectedAccountColumns, ownerId);
        WarnIfMissing(src, "categories", ExpectedCategoryColumns, ownerId);
        WarnIfMissing(src, "category_groups", ExpectedCategoryGroupColumns, ownerId);
        WarnIfMissing(src, "transactions", ExpectedTransactionColumns, ownerId);
        // booked / split_key only exist in newer MoneyMoney versions or exporter builds; without
        // them rows import as booked and split children can't be linked to their parents.
        WarnIfMissing(src, "transactions", OptionalTransactionColumns, ownerId);
        // account_groups is only present in newer exporter builds; if the table is missing we
        // skip the check rather than warn — CopyAccountGroupsAsync handles the absence below.
        if (TableExists(src, "account_groups"))
        {
            WarnIfMissing(src, "account_groups", ExpectedAccountGroupColumns, ownerId);
        }
        // securities / portfolio_chart likewise only appear in exporter builds that copy them; the
        // copy methods tolerate their absence, so only check columns when the table is present.
        if (TableExists(src, "securities"))
        {
            WarnIfMissing(src, "securities", ExpectedSecurityColumns, ownerId);
        }
        if (TableExists(src, "portfolio_chart"))
        {
            WarnIfMissing(src, "portfolio_chart", ExpectedPortfolioChartColumns, ownerId);
        }
    }

    private static bool TableExists(SqliteConnection src, string table)
    {
        using var cmd = src.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name";
        cmd.Parameters.AddWithValue("$name", table);
        var result = cmd.ExecuteScalar();
        return result != null && result != DBNull.Value;
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Table name is a compile-time-constant identifier from our own import code, never user input; PRAGMA cannot be parameterized.")]
    private void WarnIfMissing(SqliteConnection src, string table, string[] expected, string ownerId)
    {
        using var cmd = src.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var reader = cmd.ExecuteReader();
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            present.Add(reader.GetString(reader.GetOrdinal("name")));
        }
        var missing = expected.Where(c => !present.Contains(c)).ToList();
        if (missing.Count > 0)
        {
            _logger.LogWarning(
                "MoneyMoney schema drift for user {Owner}, table '{Table}': missing columns {Missing}. Affected rows will load with null/default values.",
                ownerId, table, string.Join(",", missing));
        }
    }

    /// <summary>
    /// True when the named column exists on the named table in the source DB. Used to make
    /// optional columns (e.g. SEPA <c>purpose</c>) backward-compatible with older MoneyMoney
    /// snapshots that may not carry them.
    /// </summary>
    /// <summary>
    /// MoneyMoney's initial balance of each account (also of accounts another provider owns by now: it belongs to the
    /// account's history, which a balance computed from bookings needs). Accounts whose balance an extension reports have
    /// none stored; they get the one that makes their bookings here add up to MoneyMoney's balance (depots, valued by their
    /// holdings, get none). Runs after the bookings were copied.
    /// </summary>
    private static async Task CopyInitialBalancesAsync(SqliteConnection src, BudgetDbContext db, string ownerId, int idSpace, CancellationToken ct)
    {
        if (!await ColumnExistsAsync(src, "accounts", "initial_balance", ct)) return;
        var stored = new Dictionary<long, long>();
        var reported = new Dictionary<long, long>();
        await using (var cmd = src.CreateCommand())
        {
            cmd.CommandText = "SELECT rowid, CAST(ROUND(initial_balance * 100) AS INTEGER), CASE WHEN COALESCE(portfolio, 0) = 0 THEN balances END FROM accounts";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var id = RowIds.Namespace(idSpace, r.GetInt64(0));
                if (!await r.IsDBNullAsync(1, ct)) stored[id] = r.GetInt64(1);
                else if (!await r.IsDBNullAsync(2, ct) && new Account { Balances = r.GetString(2) }.CurrentBalance is { } balance)
                    reported[id] = (long)Math.Round(balance * 100m, MidpointRounding.AwayFromZero);
            }
        }
        var sums = await db.Transactions.Where(t => t.OwnerUserId == ownerId && t.AccountId != null && t.Splitted != SplitState.Parent)
            .GroupBy(t => t.AccountId!.Value).Select(g => new { g.Key, Sum = g.Sum(t => t.Amount) })
            .ToDictionaryAsync(x => x.Key, x => x.Sum, ct);
        foreach (var account in await db.Accounts.Where(a => a.OwnerUserId == ownerId).ToListAsync(ct))
            account.InitialBalance = stored.TryGetValue(account.Id, out var cents) ? cents
                : reported.TryGetValue(account.Id, out var balance) ? balance - sums.GetValueOrDefault(account.Id)
                : 0;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The bit of MoneyMoney's accounts.refresh that marks a closed account.</summary>
    internal const int ClosedRefreshBit = 16;

    private static async Task<bool> ColumnExistsAsync(SqliteConnection src, string table, string column, CancellationToken ct) =>
        (await ColumnsAsync(src, table, ct)).Contains(column);

    /// <summary>The column names of <paramref name="table"/> (case-insensitive set).</summary>
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Table name is a compile-time-constant identifier from our own import code, never user input; PRAGMA cannot be parameterized.")]
    private static async Task<HashSet<string>> ColumnsAsync(SqliteConnection src, string table, CancellationToken ct)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = src.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            columns.Add(reader.GetString(reader.GetOrdinal("name")));
        }
        return columns;
    }

    private static async Task<int> CopyAccountsAsync(
        SqliteConnection src, BudgetDbContext db, string ownerId, int idSpace, CancellationToken ct)
    {
        // group_key is the FK into account_groups; older exporters (pre-account-groups) won't
        // have account_groups but the column itself has always been on accounts. Probe anyway
        // so we degrade cleanly if a forked schema ever drops it.
        bool hasGroupKey = await ColumnExistsAsync(src, "accounts", "group_key", ct);
        string groupKeyExpr = hasGroupKey ? "group_key" : "NULL AS group_key";
        // MoneyMoney's refresh bit mask: 16 marks an account the user closed there.
        string closedExpr = await ColumnExistsAsync(src, "accounts", "refresh", ct) ? $"(COALESCE(refresh, 0) & {ClosedRefreshBit}) <> 0" : "0";
        string iconExpr = await ColumnExistsAsync(src, "accounts", "icon", ct) ? "icon" : "NULL AS icon";
        string select = $"""
            SELECT rowid, name, iban, currency, type, portfolio, balances, sort, refresh_tsp, error, {groupKeyExpr}, {closedExpr}, {iconExpr}
            FROM accounts
            """;
        const string insert = """
            INSERT INTO accounts (Id, OwnerUserId, name, iban, currency, type, portfolio, balances, sort, refresh_tsp, error, group_key, provider, closed, icon, color)
            VALUES ($id, $sid, $name, $iban, $currency, $type, $portfolio, $balances, $sort, $refresh_tsp, $error, $group_key, $provider, $closed, $icon, $color)
            """;
        return await BulkCopyAsync(src, db, select, insert, (cmd, r) =>
        {
            long rowid = r.GetInt64(0);
            long id = RowIds.Namespace(idSpace, rowid);
            cmd.Parameters["$id"].Value = id;
            cmd.Parameters["$sid"].Value = ownerId;
            cmd.Parameters["$provider"].Value = AccountProviders.Manual;
            cmd.Parameters["$name"].Value = r.IsDBNull(1) ? DBNull.Value : r.GetString(1);
            cmd.Parameters["$iban"].Value = r.IsDBNull(2) ? DBNull.Value : r.GetString(2);
            cmd.Parameters["$currency"].Value = r.IsDBNull(3) ? DBNull.Value : r.GetString(3);
            cmd.Parameters["$type"].Value = r.IsDBNull(4) ? DBNull.Value : r.GetInt64(4);
            cmd.Parameters["$portfolio"].Value = r.IsDBNull(5) ? DBNull.Value : r.GetInt64(5);
            cmd.Parameters["$balances"].Value = r.IsDBNull(6) ? DBNull.Value : r.GetString(6);
            cmd.Parameters["$sort"].Value = r.IsDBNull(7) ? DBNull.Value : r.GetInt64(7);
            cmd.Parameters["$refresh_tsp"].Value = r.IsDBNull(8) ? DBNull.Value : r.GetInt64(8);
            cmd.Parameters["$error"].Value = r.IsDBNull(9) ? DBNull.Value : r.GetString(9);
            cmd.Parameters["$closed"].Value = r.GetInt64(11) != 0;
            cmd.Parameters["$group_key"].Value = r.IsDBNull(10)
                ? DBNull.Value
                : NamespaceParentKey(idSpace, r.GetInt64(10));
            var look = MoneyMoneyIcons.Parse(r.IsDBNull(12) ? null : r.GetString(12));
            cmd.Parameters["$icon"].Value = (object?)look?.Icon ?? DBNull.Value;
            cmd.Parameters["$color"].Value = (object?)look?.Color ?? DBNull.Value;
        }, AccountInsertParameters, ct);
    }

    private static async Task<int> CopyAccountGroupsAsync(SqliteConnection src, BudgetDbContext db, string ownerId, int idSpace, CancellationToken ct)
    {
        // account_groups landed in the exporter after the first releases; tolerate its absence
        // so a stale exporter upload still imports (accounts simply land without a parent ref).
        if (!TableExists(src, "account_groups")) return 0;
        string iconExpr = await ColumnExistsAsync(src, "account_groups", "icon", ct) ? "icon" : "NULL AS icon";
        string select = $"SELECT rowid, name, group_key, sort, {iconExpr} FROM account_groups";
        const string insert = """
            INSERT INTO account_groups (Id, OwnerUserId, name, group_key, sort, icon, color)
            VALUES ($id, $sid, $name, $group_key, $sort, $icon, $color)
            """;
        return await BulkCopyAsync(src, db, select, insert, (cmd, r) =>
        {
            long rowid = r.GetInt64(0);
            cmd.Parameters["$id"].Value = RowIds.Namespace(idSpace, rowid);
            cmd.Parameters["$sid"].Value = ownerId;
            cmd.Parameters["$name"].Value = r.IsDBNull(1) ? DBNull.Value : r.GetString(1);
            cmd.Parameters["$group_key"].Value = r.IsDBNull(2)
                ? DBNull.Value
                : NamespaceParentKey(idSpace, r.GetInt64(2));
            cmd.Parameters["$sort"].Value = r.IsDBNull(3) ? DBNull.Value : r.GetInt64(3);
            var look = MoneyMoneyIcons.Parse(r.IsDBNull(4) ? null : r.GetString(4));
            cmd.Parameters["$icon"].Value = (object?)look?.Icon ?? DBNull.Value;
            cmd.Parameters["$color"].Value = (object?)look?.Color ?? DBNull.Value;
        }, AccountGroupInsertParameters, ct);
    }

    private static async Task<int> CopyCategoryGroupsAsync(SqliteConnection src, BudgetDbContext db, string ownerId, int idSpace, CancellationToken ct)
    {
        const string select = "SELECT rowid, name, group_key, sort, color FROM category_groups";
        const string insert = """
            INSERT INTO category_groups (Id, OwnerUserId, name, group_key, sort, color)
            VALUES ($id, $sid, $name, $group_key, $sort, $color)
            """;
        return await BulkCopyAsync(src, db, select, insert, (cmd, r) =>
        {
            long rowid = r.GetInt64(0);
            cmd.Parameters["$id"].Value = RowIds.Namespace(idSpace, rowid);
            cmd.Parameters["$sid"].Value = ownerId;
            cmd.Parameters["$name"].Value = r.IsDBNull(1) ? DBNull.Value : r.GetString(1);
            cmd.Parameters["$group_key"].Value = r.IsDBNull(2)
                ? DBNull.Value
                : NamespaceParentKey(idSpace, r.GetInt64(2));
            cmd.Parameters["$sort"].Value = r.IsDBNull(3) ? DBNull.Value : r.GetInt64(3);
            cmd.Parameters["$color"].Value = r.IsDBNull(4) ? DBNull.Value : r.GetString(4);
        }, CategoryGroupInsertParameters, ct);
    }

    private static async Task<int> CopyCategoriesAsync(SqliteConnection src, BudgetDbContext db, string ownerId, int idSpace, CancellationToken ct)
    {
        const string select = "SELECT rowid, name, type, color, rules, group_key, sort FROM categories";
        const string insert = """
            INSERT INTO categories (Id, OwnerUserId, name, type, color, rules, group_key, sort)
            VALUES ($id, $sid, $name, $type, $color, $rules, $group_key, $sort)
            """;
        return await BulkCopyAsync(src, db, select, insert, (cmd, r) =>
        {
            long rowid = r.GetInt64(0);
            cmd.Parameters["$id"].Value = RowIds.Namespace(idSpace, rowid);
            cmd.Parameters["$sid"].Value = ownerId;
            cmd.Parameters["$name"].Value = r.IsDBNull(1) ? DBNull.Value : r.GetString(1);
            cmd.Parameters["$type"].Value = r.IsDBNull(2) ? DBNull.Value : r.GetInt64(2);
            cmd.Parameters["$color"].Value = r.IsDBNull(3) ? DBNull.Value : r.GetString(3);
            cmd.Parameters["$rules"].Value = r.IsDBNull(4) ? DBNull.Value : r.GetString(4);
            cmd.Parameters["$group_key"].Value = r.IsDBNull(5)
                ? DBNull.Value
                : NamespaceParentKey(idSpace, r.GetInt64(5));
            cmd.Parameters["$sort"].Value = r.IsDBNull(6) ? DBNull.Value : r.GetInt64(6);
        }, CategoryInsertParameters, ct);
    }

    /// <summary>
    /// MoneyMoney's "Restrict to" of a category (<c>categories.apply_to_account</c>: <c>A&lt;accounts.rowid&gt;</c> or
    /// <c>G&lt;account_groups.rowid&gt;</c>, empty = all accounts) onto the imported category. A target the import does
    /// not have leaves the category unrestricted. Returns (restricted, unknown).
    /// </summary>
    private static async Task<(int Restricted, int Unknown)> CopyCategoryRestrictionsAsync(
        SqliteConnection src, BudgetDbContext db, string ownerId, int idSpace, CancellationToken ct)
    {
        if (!await ColumnExistsAsync(src, "categories", "apply_to_account", ct)) return (0, 0);
        var wanted = new List<(long CategoryId, string Value)>();
        await using (var cmd = src.CreateCommand())
        {
            cmd.CommandText = "SELECT rowid, apply_to_account FROM categories WHERE apply_to_account IS NOT NULL AND TRIM(apply_to_account) <> ''";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                wanted.Add((RowIds.Namespace(idSpace, r.GetInt64(0)), Convert.ToString(r.GetValue(1), CultureInfo.InvariantCulture)!.Trim()));
        }
        if (wanted.Count == 0) return (0, 0);
        var accounts = (await db.Accounts.Where(a => a.OwnerUserId == ownerId).Select(a => a.Id).ToListAsync(ct)).ToHashSet();
        var groups = (await db.AccountGroups.Where(g => g.OwnerUserId == ownerId).Select(g => g.Id).ToListAsync(ct)).ToHashSet();
        int restricted = 0, unknown = 0;
        foreach (var (categoryId, value) in wanted)
        {
            var (kind, row) = ParseRestriction(value);
            var target = row > 0 ? RowIds.Namespace(idSpace, row) : 0;
            long? accountId = kind == 'A' && accounts.Contains(target) ? target : null;
            long? groupId = kind == 'G' && groups.Contains(target) ? target : null;
            if (accountId == null && groupId == null)
            {
                unknown++;
                continue;
            }
            restricted += await db.Categories.Where(c => c.Id == categoryId && c.OwnerUserId == ownerId).ExecuteUpdateAsync(
                s => s.SetProperty(c => c.RestrictAccountId, accountId).SetProperty(c => c.RestrictAccountGroupId, groupId), ct);
        }
        return (restricted, unknown);
    }

    /// <summary>Splits <c>A12</c> / <c>G3</c> into kind and rowid; ('?', 0) when it has another shape.</summary>
    internal static (char Kind, long RowId) ParseRestriction(string value) =>
        value.Length >= 2 && value[0] is 'A' or 'G' or 'a' or 'g'
        && long.TryParse(value.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var row) && row > 0
            ? (char.ToUpperInvariant(value[0]), row)
            : ('?', 0);

    private static async Task<int> CopyTransactionsAsync(
        SqliteConnection src, BudgetDbContext db, string ownerId, int idSpace, HashSet<long> existingAccountIds, CancellationToken ct)
    {
        // MoneyMoney's SEPA "Verwendungszweck" lives in unformatted_purpose (verified against
        // a live MoneyMoney 2.5-era SQLCipher db: rows there carry the BILLCOLL/EREF/MREF/CRED
        // raw text users see in the transaction detail pane). Older / forked schemas may not
        // have it — probe at runtime and substitute NULL so the import still succeeds.
        var columns = await ColumnsAsync(src, "transactions", ct);
        // Without the unformatted text the formatted purpose is the next best thing.
        string purposeExpr = columns.Contains("unformatted_purpose") ? "unformatted_purpose"
            : columns.Contains("purpose") ? "purpose AS unformatted_purpose" : "NULL AS unformatted_purpose";
        var detailExprs = TransactionDetailColumns
            .Select(d => d.Candidates.FirstOrDefault(columns.Contains) is { } c ? "\"" + c + "\"" : "NULL")
            .ToList();
        // booked: 0 = pending ("vorgemerkt"). Missing column => everything counts as booked.
        bool hasBooked = await ColumnExistsAsync(src, "transactions", "booked", ct);
        string bookedExpr = hasBooked ? "booked" : "1 AS booked";
        // split_key links a child (splitted=2) to its parent's rowid. Without it we cannot tell
        // how much of a parent is allocated, so parents keep the legacy "excluded" behavior.
        bool hasSplitKey = await ColumnExistsAsync(src, "transactions", "split_key", ct);
        string splitKeyExpr = hasSplitKey ? "split_key" : "NULL AS split_key";
        var (childSumsCents, childRowids) = hasSplitKey
            ? await ReadSplitChildrenAsync(src, ct)
            : (new Dictionary<long, long>(), new HashSet<long>());

        string select = $"""
            SELECT rowid, local_account_key, amount, currency, timestamp, name, comment, {purposeExpr}, category_key, checkmark, splitted, {bookedExpr}, {splitKeyExpr}, {string.Join(", ", detailExprs)}
            FROM transactions
            """;
        const string insert = """
            INSERT INTO transactions (Id, OwnerUserId, local_account_key, amount, currency, timestamp, name, comment, purpose, category_key, checkmark, splitted, booked, split_parent_id, split_original_amount, note, "checked",
                value_timestamp, counterparty_iban, counterparty_bic, booking_text, bank_reference, end_to_end_reference, mandate_reference, creditor_id, transaction_code, primanota, return_reason)
            VALUES ($id, $sid, $local_account_key, $amount, $currency, $timestamp, $name, $comment, $purpose, $category_key, $checkmark, $splitted, $booked, $split_parent_id, $split_original_amount, $note, $checked,
                $value_timestamp, $counterparty_iban, $counterparty_bic, $booking_text, $bank_reference, $end_to_end_reference, $mandate_reference, $creditor_id, $transaction_code, $primanota, $return_reason)
            """;
        return await BulkCopyAsync(src, db, select, insert, (cmd, r) =>
        {
            long rowid = r.GetInt64(0);
            cmd.Parameters["$id"].Value = RowIds.Namespace(idSpace, rowid);
            cmd.Parameters["$sid"].Value = ownerId;
            cmd.Parameters["$local_account_key"].Value = r.IsDBNull(1)
                ? DBNull.Value
                : RowIds.Namespace(idSpace, r.GetInt64(1));
            // MoneyMoney stores amounts as REAL euros (or other currency); we keep them as
            // integer cents downstream so DB-side SUM is exact.
            long? amountCents = r.IsDBNull(2) ? null : MoneyMath.EurosToCents(r.GetDouble(2));
            cmd.Parameters["$amount"].Value = (object?)amountCents ?? DBNull.Value;
            cmd.Parameters["$currency"].Value = r.IsDBNull(3) ? DBNull.Value : r.GetString(3);
            cmd.Parameters["$timestamp"].Value = r.IsDBNull(4) ? DBNull.Value : r.GetInt64(4);
            cmd.Parameters["$name"].Value = r.IsDBNull(5) ? DBNull.Value : r.GetString(5);
            cmd.Parameters["$comment"].Value = r.IsDBNull(6) ? DBNull.Value : r.GetString(6);
            // OpenBudget's note and check mark start as MoneyMoney's.
            cmd.Parameters["$note"].Value = r.IsDBNull(6) ? DBNull.Value : r.GetString(6);
            cmd.Parameters["$purpose"].Value = r.IsDBNull(7) ? DBNull.Value : r.GetString(7);
            cmd.Parameters["$category_key"].Value = r.IsDBNull(8)
                ? DBNull.Value
                : RowIds.Namespace(idSpace, r.GetInt64(8));
            cmd.Parameters["$checkmark"].Value = r.IsDBNull(9) ? DBNull.Value : r.GetInt64(9);
            cmd.Parameters["$checked"].Value = !r.IsDBNull(9) && r.GetInt64(9) > 0 ? 1 : 0;
            long? splitted = r.IsDBNull(10) ? null : r.GetInt64(10);
            cmd.Parameters["$splitted"].Value = (object?)splitted ?? DBNull.Value;
            cmd.Parameters["$booked"].Value = r.IsDBNull(11) || r.GetInt64(11) != 0 ? 1 : 0;
            cmd.Parameters["$split_parent_id"].Value = DBNull.Value;
            cmd.Parameters["$split_original_amount"].Value = DBNull.Value;
            SetDetails(cmd, r, 13, r.IsDBNull(7) ? null : r.GetString(7));

            // Any row whose split_key points at an existing split parent is a child, whatever its
            // own splitted value: MoneyMoney also stores children with splitted=0, and counting
            // those as normal rows beside a full-amount remainder double counts the booking.
            if (childRowids.Contains(rowid))
            {
                cmd.Parameters["$splitted"].Value = SplitState.Child;
                cmd.Parameters["$split_parent_id"].Value = RowIds.Namespace(idSpace, r.GetInt64(12));
            }
            else if (splitted == SplitState.Parent && hasSplitKey)
            {
                long original = amountCents ?? 0;
                cmd.Parameters["$split_original_amount"].Value = original;
                long remainder = SplitMath.Remainder(original, childSumsCents.GetValueOrDefault(rowid));
                if (remainder != 0)
                {
                    // Children don't cover the parent: turn it into a normal row for the
                    // unallocated part so every total picks it up without special cases. It keeps
                    // the parent's own MoneyMoney category (MoneyMoney shows it categorized);
                    // only a parent without one leaves the remainder uncategorized.
                    cmd.Parameters["$splitted"].Value = SplitState.Remainder;
                    cmd.Parameters["$amount"].Value = remainder;
                    cmd.Parameters["$category_key"].Value = r.IsDBNull(8) || r.GetInt64(8) == 0
                        ? DBNull.Value
                        : RowIds.Namespace(idSpace, r.GetInt64(8));
                }
            }
        }, TransactionInsertParameters, ct, skipRow: r =>
            !r.IsDBNull(1) && existingAccountIds.Contains(RowIds.Namespace(idSpace, r.GetInt64(1))));
    }

    /// <summary>
    /// Fills the detail parameters from the probed columns starting at <paramref name="first"/>. SEPA references
    /// MoneyMoney has no column for are taken from the purpose's EREF+ / MREF+ / CRED+ tags.
    /// </summary>
    private static void SetDetails(SqliteCommand cmd, SqliteDataReader r, int first, string? purpose)
    {
        Dictionary<string, string>? tags = null;
        for (int i = 0; i < TransactionDetailColumns.Length; i++)
        {
            var (column, isDate, _) = TransactionDetailColumns[i];
            object? value = r.IsDBNull(first + i) ? null : isDate ? ToUnixSeconds(r.GetValue(first + i)) : Text(r.GetValue(first + i));
            if (value == null && column is "end_to_end_reference" or "mandate_reference" or "creditor_id")
            {
                tags ??= SepaPurpose.Parse(purpose);
                value = tags.GetValueOrDefault(column switch { "end_to_end_reference" => "EREF", "mandate_reference" => "MREF", _ => "CRED" });
            }
            cmd.Parameters["$" + column].Value = value ?? DBNull.Value;
        }
    }

    private static string? Text(object value) => Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() is { Length: > 0 } s ? s : null;

    /// <summary>A date column as unix seconds: MoneyMoney stores dates as unix seconds like <c>timestamp</c>; ISO text is accepted too.</summary>
    private static long? ToUnixSeconds(object value) => value switch
    {
        long l => l > 0 ? l : null,
        double d => d > 0 ? (long)d : null,
        string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) => l > 0 ? l : null,
        string s when DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt)
            => new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)).ToUnixTimeSeconds(),
        _ => null,
    };

    /// <summary>
    /// Split children (rows whose split_key names an existing split parent, regardless of their
    /// own splitted value) and the sum of their amounts (cents) per parent rowid.
    /// </summary>
    private static async Task<(Dictionary<long, long> Sums, HashSet<long> ChildRowids)> ReadSplitChildrenAsync(SqliteConnection src, CancellationToken ct)
    {
        var sums = new Dictionary<long, long>();
        var children = new HashSet<long>();
        await using var cmd = src.CreateCommand();
        cmd.CommandText = """
            SELECT c.rowid, c.split_key, c.amount
            FROM transactions c
            JOIN transactions p ON p.rowid = c.split_key
            WHERE c.split_key IS NOT NULL AND c.split_key <> 0 AND c.rowid <> c.split_key
              AND p.splitted = 1
            """;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            children.Add(reader.GetInt64(0));
            if (await reader.IsDBNullAsync(2, ct)) continue;
            long parent = reader.GetInt64(1);
            sums[parent] = sums.GetValueOrDefault(parent) + MoneyMath.EurosToCents(reader.GetDouble(2));
        }
        return (sums, children);
    }

    private static async Task<int> CopySecuritiesAsync(SqliteConnection src, BudgetDbContext db, string ownerId, int idSpace, HashSet<long> existingAccountIds, CancellationToken ct)
    {
        // securities only ships in exporter builds that copy it; tolerate its absence so a stale
        // upload still imports the rest. Only live positions are carried over — MoneyMoney keeps
        // sold/closed holdings around with deleted=1, which would otherwise pollute the depot view.
        if (!TableExists(src, "securities")) return 0;
        const string select = """
            SELECT rowid, account_key, ident, isin, wpn, symbol, quantity, amount, amount_cur, price, price_cur, purchase, asset_class
            FROM securities
            WHERE COALESCE(deleted, 0) = 0
            """;
        const string insert = """
            INSERT INTO holdings (Id, OwnerUserId, account_key, name, isin, wkn, symbol, quantity, amount, amount_cur, price, price_cur, purchase, asset_class)
            VALUES ($id, $sid, $account_key, $name, $isin, $wkn, $symbol, $quantity, $amount, $amount_cur, $price, $price_cur, $purchase, $asset_class)
            """;
        return await BulkCopyAsync(src, db, select, insert, (cmd, r) =>
        {
            long rowid = r.GetInt64(0);
            cmd.Parameters["$id"].Value = RowIds.Namespace(idSpace, rowid);
            cmd.Parameters["$sid"].Value = ownerId;
            cmd.Parameters["$account_key"].Value = RowIds.Namespace(idSpace, r.GetInt64(1));
            cmd.Parameters["$name"].Value = r.IsDBNull(2) ? DBNull.Value : r.GetString(2);
            cmd.Parameters["$isin"].Value = r.IsDBNull(3) ? DBNull.Value : r.GetString(3);
            cmd.Parameters["$wkn"].Value = r.IsDBNull(4) ? DBNull.Value : r.GetString(4);
            cmd.Parameters["$symbol"].Value = r.IsDBNull(5) ? DBNull.Value : r.GetString(5);
            // quantity/amount/price/purchase stay REAL — crypto carries sub-cent quantities and
            // these are market valuations, not the cents-as-long ledger used for transactions.
            cmd.Parameters["$quantity"].Value = r.IsDBNull(6) ? 0d : r.GetDouble(6);
            cmd.Parameters["$amount"].Value = r.IsDBNull(7) ? DBNull.Value : r.GetDouble(7);
            cmd.Parameters["$amount_cur"].Value = r.IsDBNull(8) ? DBNull.Value : r.GetString(8);
            cmd.Parameters["$price"].Value = r.IsDBNull(9) ? DBNull.Value : r.GetDouble(9);
            cmd.Parameters["$price_cur"].Value = r.IsDBNull(10) ? DBNull.Value : r.GetString(10);
            cmd.Parameters["$purchase"].Value = r.IsDBNull(11) ? DBNull.Value : r.GetDouble(11);
            cmd.Parameters["$asset_class"].Value = r.IsDBNull(12) ? DBNull.Value : r.GetInt64(12);
        }, SecurityInsertParameters, ct, skipRow: r => existingAccountIds.Contains(RowIds.Namespace(idSpace, r.GetInt64(1))));
    }

    private static async Task<int> CopyPortfolioChartAsync(SqliteConnection src, BudgetDbContext db, string ownerId, int idSpace, HashSet<long> existingAccountIds, CancellationToken ct)
    {
        // portfolio_chart, like securities, only ships in newer exporter builds. securities_key is
        // namespaced but carries no FK (it can reference deleted positions we don't import), so a
        // 0 sentinel maps to NULL like other parent keys.
        if (!TableExists(src, "portfolio_chart")) return 0;
        const string select = "SELECT rowid, account_key, currency, timestamp, balance, securities_key FROM portfolio_chart";
        const string insert = """
            INSERT INTO portfolio_chart (Id, OwnerUserId, account_key, currency, timestamp, balance, securities_key)
            VALUES ($id, $sid, $account_key, $currency, $timestamp, $balance, $securities_key)
            """;
        return await BulkCopyAsync(src, db, select, insert, (cmd, r) =>
        {
            long rowid = r.GetInt64(0);
            cmd.Parameters["$id"].Value = RowIds.Namespace(idSpace, rowid);
            cmd.Parameters["$sid"].Value = ownerId;
            cmd.Parameters["$account_key"].Value = RowIds.Namespace(idSpace, r.GetInt64(1));
            cmd.Parameters["$currency"].Value = r.IsDBNull(2) ? DBNull.Value : r.GetString(2);
            cmd.Parameters["$timestamp"].Value = r.IsDBNull(3) ? DBNull.Value : r.GetInt64(3);
            cmd.Parameters["$balance"].Value = r.IsDBNull(4) ? DBNull.Value : r.GetDouble(4);
            cmd.Parameters["$securities_key"].Value = r.IsDBNull(5)
                ? DBNull.Value
                : NamespaceParentKey(idSpace, r.GetInt64(5));
        }, PortfolioChartInsertParameters, ct, skipRow: r => existingAccountIds.Contains(RowIds.Namespace(idSpace, r.GetInt64(1))));
    }

    /// <summary>
    /// Namespace a MoneyMoney parent/group rowid into our ID space. MoneyMoney uses
    /// <c>0</c> as the "no parent" / "no group" sentinel; we map that to <see cref="DBNull"/>
    /// so the FK actually resolves (and the deferred FK check at migration commit doesn't
    /// trip on dangling references).
    /// </summary>
    private static object NamespaceParentKey(int idSpace, long parentRowid)
    {
        return parentRowid == 0 ? DBNull.Value : RowIds.Namespace(idSpace, parentRowid);
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "select/insert SQL is built by callers from constant column lists, not user input; row values are bound as parameters.")]
    private static async Task<int> BulkCopyAsync(
        SqliteConnection src,
        BudgetDbContext db,
        string selectSql,
        string insertSql,
        Action<SqliteCommand, SqliteDataReader> bind,
        string[] parameterNames,
        CancellationToken ct,
        Func<SqliteDataReader, bool>? skipRow = null)
    {
        // Reader on the source DB; one INSERT command on the destination, executed once per row
        // inside a single Database transaction. SQLite handles ~50k inserts/sec like this when
        // SourceConn and DestConn are separate (no journal contention).
        var destConn = (SqliteConnection)db.Database.GetDbConnection();
        if (destConn.State != System.Data.ConnectionState.Open)
            await destConn.OpenAsync(ct);

        await using var insertCmd = destConn.CreateCommand();
        insertCmd.CommandText = insertSql;
        if (db.Database.CurrentTransaction?.GetDbTransaction() is SqliteTransaction destTx) insertCmd.Transaction = destTx;
        foreach (var p in parameterNames)
            insertCmd.Parameters.Add(new SqliteParameter(p, DBNull.Value));
        // Compile the statement once; the loop below reuses it per row, so without Prepare SQLite
        // would re-parse the same INSERT tens of thousands of times.
        await insertCmd.PrepareAsync(ct);

        await using var selectCmd = src.CreateCommand();
        selectCmd.CommandText = selectSql;
        await using var reader = await selectCmd.ExecuteReaderAsync(ct);

        int count = 0;
        while (await reader.ReadAsync(ct))
        {
            if (skipRow != null && skipRow(reader)) continue;
            bind(insertCmd, reader);
            await insertCmd.ExecuteNonQueryAsync(ct);
            count++;
        }
        return count;
    }
}

/// <summary>An import arrived for a user who already imported from MoneyMoney.</summary>
public sealed class MoneyMoneyAlreadyImportedException : InvalidOperationException
{
    public MoneyMoneyAlreadyImportedException()
        : base(MoneyMoneyImportService.AlreadyImportedMessage)
    {
    }

    public MoneyMoneyAlreadyImportedException(string message)
        : base(message)
    {
    }

    public MoneyMoneyAlreadyImportedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

public class ImportResult
{
    public string OwnerUserId { get; set; } = string.Empty;
    public int AccountsImported { get; set; }
    public int AccountGroupsImported { get; set; }
    public int CategoryGroupsImported { get; set; }
    public int CategoriesImported { get; set; }
    public int TransactionsImported { get; set; }
    public int SecuritiesImported { get; set; }
    public int PortfolioChartPointsImported { get; set; }
    /// <summary>Categories whose "Restrict to" (MoneyMoney's apply_to_account) was taken over.</summary>
    public int CategoriesRestricted { get; set; }
    /// <summary>Categories restricted in MoneyMoney to an account or group the import does not have: left unrestricted.</summary>
    public int CategoryRestrictionsUnknown { get; set; }
    public long FileSizeBytes { get; set; }
}
