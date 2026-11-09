using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.Importers.MoneyMoney;

/// <summary>
/// Imports an uploaded MoneyMoney SQLite file into our store under a named source. Reads the
/// source DB read-only via raw <see cref="SqliteDataReader"/> for speed (EF AddRange is much
/// slower at hundreds-of-thousands of transactions), namespaces every rowid via
/// <see cref="SourceScope.Namespace"/>, and replaces the source's existing rows inside one
/// EF transaction so partial failures roll back cleanly.
///
/// Registered as a singleton so the per-source serialization semaphore is shared across
/// concurrent upload requests.
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
        "$id", "$sid", "$name", "$iban", "$currency", "$type", "$portfolio", "$balances", "$sort", "$refresh_tsp", "$error", "$group_key", "$provider"
    };
    private static readonly string[] AccountGroupInsertParameters =
    {
        "$id", "$sid", "$name", "$group_key", "$sort"
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
        "$id", "$sid", "$local_account_key", "$amount", "$currency", "$timestamp", "$name", "$comment", "$purpose", "$category_key", "$checkmark", "$splitted", "$booked", "$split_parent_id", "$split_original_amount"
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
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _perSourceLocks = new(StringComparer.Ordinal);

    public MoneyMoneyImportService(
        IDbContextFactory<BudgetDbContext> dbFactory,
        ChangeNotificationService notifier,
        ILogger<MoneyMoneyImportService> logger)
    {
        _dbFactory = dbFactory;
        _notifier = notifier;
        _logger = logger;
    }

    public async Task<ImportResult> ImportFromFileAsync(string sourceName, string sqliteFilePath, CancellationToken ct)
    {
        if (!SourceData.IsValidSlug(sourceName))
            throw new ArgumentException($"Source name '{sourceName}' is not a valid slug.", nameof(sourceName));

        var sem = _perSourceLocks.GetOrAdd(sourceName, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct);
        try
        {
            return await ImportInternalAsync(sourceName, sqliteFilePath, ct);
        }
        finally
        {
            sem.Release();
        }
    }

    private async Task<ImportResult> ImportInternalAsync(string sourceName, string sqliteFilePath, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // Find-or-create the source row before we open the import transaction, so its identity
        // is visible immediately and a parallel call for the same name (different file) finds it.
        var source = await db.ImportSources.FirstOrDefaultAsync(s => s.Name == sourceName, ct);
        if (source == null)
        {
            source = new ImportSource
            {
                Name = sourceName,
                CreatedAt = DateTime.UtcNow
            };
            db.ImportSources.Add(source);
            await db.SaveChangesAsync(ct);
        }
        int sourceId = source.Id;

        var connStr = new SqliteConnectionStringBuilder
        {
            DataSource = sqliteFilePath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString();

        await using var src = new SqliteConnection(connStr);
        await src.OpenAsync(ct);

        WarnOnSchemaDrift(src, sourceName);

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
            // Wipe previous rows for this source. Order matters: transactions and securities
            // reference accounts/categories via FKs (Restrict), so they go first. Accounts
            // reference account_groups, so accounts go before account_groups.
            // Remember each account's provider (keyed by Id, which MoneyMoney keeps stable) before
            // the accounts are replaced, so accounts handed over to another connector stay that way.
            var providers = await db.Accounts
                .Where(a => a.SourceId == sourceId)
                .ToDictionaryAsync(a => a.Id, a => a.Provider, ct);
            var foreignAccountIds = providers
                .Where(p => p.Value != AccountProviders.MoneyMoney)
                .Select(p => p.Key)
                .ToHashSet();

            // Balance / refresh state of accounts handed to a connector belongs to that connector.
            var foreignState = foreignAccountIds.Count == 0
                ? new Dictionary<long, ForeignAccountState>()
                : await db.Accounts
                    .Where(a => a.SourceId == sourceId && foreignAccountIds.Contains(a.Id))
                    .ToDictionaryAsync(a => a.Id, a => new ForeignAccountState(a.Balances, a.RefreshTimestamp, a.Error), ct);

            // Only MoneyMoney-owned transactions are replaced; accounts owned by another provider
            // keep theirs untouched.
            await db.Database.ExecuteSqlRawAsync(
                """
                DELETE FROM transactions
                WHERE SourceId = {0}
                  AND (local_account_key IS NULL
                       OR local_account_key NOT IN (SELECT Id FROM accounts WHERE SourceId = {0} AND provider <> {1}))
                """,
                new object[] { sourceId, AccountProviders.MoneyMoney }, ct);
            // Likewise positions / portfolio history of connector-owned accounts stay as the connector wrote them.
            await db.Database.ExecuteSqlRawAsync(
                """
                DELETE FROM portfolio_chart
                WHERE SourceId = {0}
                  AND account_key NOT IN (SELECT Id FROM accounts WHERE SourceId = {0} AND provider <> {1})
                """,
                new object[] { sourceId, AccountProviders.MoneyMoney }, ct);
            await db.Database.ExecuteSqlRawAsync(
                """
                DELETE FROM holdings
                WHERE SourceId = {0}
                  AND account_key NOT IN (SELECT Id FROM accounts WHERE SourceId = {0} AND provider <> {1})
                """,
                new object[] { sourceId, AccountProviders.MoneyMoney }, ct);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM categories WHERE SourceId = {0}", new object[] { sourceId }, ct);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM category_groups WHERE SourceId = {0}", new object[] { sourceId }, ct);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM accounts WHERE SourceId = {0}", new object[] { sourceId }, ct);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM account_groups WHERE SourceId = {0}", new object[] { sourceId }, ct);

            int accountGroupCount = await CopyAccountGroupsAsync(src, db, sourceId, ct);
            int accountCount = await CopyAccountsAsync(src, db, sourceId, providers, foreignState, ct);
            int groupCount = await CopyCategoryGroupsAsync(src, db, sourceId, ct);
            int categoryCount = await CopyCategoriesAsync(src, db, sourceId, ct);
            int txCount = await CopyTransactionsAsync(src, db, sourceId, foreignAccountIds, ct);
            int securityCount = await CopySecuritiesAsync(src, db, sourceId, foreignAccountIds, ct);
            int portfolioChartCount = await CopyPortfolioChartAsync(src, db, sourceId, foreignAccountIds, ct);

            long fileSize = new FileInfo(sqliteFilePath).Length;
            source.LastUploadAt = DateTime.UtcNow;
            source.LastUploadSizeBytes = fileSize;
            await db.SaveChangesAsync(ct);

            // FKs were enforced-off for the copy; validate the final state before committing so a
            // dangling reference (i.e. an import bug) is surfaced rather than silently persisted.
            await LogForeignKeyViolationsAsync(destConn, db.Database.CurrentTransaction?.GetDbTransaction() as SqliteTransaction, sourceName, ct);

            await tx.CommitAsync(ct);

            _logger.LogInformation(
                "Imported MoneyMoney source '{Source}' (id={SourceId}): {Accounts} accounts, {AccountGroups} account groups, {Groups} groups, {Categories} categories, {Transactions} transactions, {Securities} securities, {PortfolioChart} portfolio-chart points ({Bytes} bytes)",
                sourceName, sourceId, accountCount, accountGroupCount, groupCount, categoryCount, txCount, securityCount, portfolioChartCount, fileSize);

            // Tell every open browser session their imported data changed so pages reload —
            // a fresh upload moves balances and spending on every project bound to this source.
            _notifier.Publish(new DataChange(DataArea.ImportedData, SourceId: sourceId));
            _notifier.Publish(new DataChange(DataArea.Sources, SourceId: sourceId));

            return new ImportResult
            {
                SourceId = sourceId,
                SourceName = sourceName,
                AccountsImported = accountCount,
                AccountGroupsImported = accountGroupCount,
                CategoryGroupsImported = groupCount,
                CategoriesImported = categoryCount,
                TransactionsImported = txCount,
                SecuritiesImported = securityCount,
                PortfolioChartPointsImported = portfolioChartCount,
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
        SqliteConnection destConn, SqliteTransaction? tx, string sourceName, CancellationToken ct)
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
                "FK violation after importing '{Source}': {Table} row {RowId} references missing {Parent}.",
                sourceName, table, rowid, parent);
        }
        if (violations > 0)
            _logger.LogWarning(
                "Import of '{Source}' left {Count} dangling foreign-key reference(s); data was committed but may render incorrectly.",
                sourceName, violations);
    }

    private void WarnOnSchemaDrift(SqliteConnection src, string sourceName)
    {
        WarnIfMissing(src, "accounts", ExpectedAccountColumns, sourceName);
        WarnIfMissing(src, "categories", ExpectedCategoryColumns, sourceName);
        WarnIfMissing(src, "category_groups", ExpectedCategoryGroupColumns, sourceName);
        WarnIfMissing(src, "transactions", ExpectedTransactionColumns, sourceName);
        // booked / split_key only exist in newer MoneyMoney versions or exporter builds; without
        // them rows import as booked and split children can't be linked to their parents.
        WarnIfMissing(src, "transactions", OptionalTransactionColumns, sourceName);
        // account_groups is only present in newer exporter builds; if the table is missing we
        // skip the check rather than warn — CopyAccountGroupsAsync handles the absence below.
        if (TableExists(src, "account_groups"))
        {
            WarnIfMissing(src, "account_groups", ExpectedAccountGroupColumns, sourceName);
        }
        // securities / portfolio_chart likewise only appear in exporter builds that copy them; the
        // copy methods tolerate their absence, so only check columns when the table is present.
        if (TableExists(src, "securities"))
        {
            WarnIfMissing(src, "securities", ExpectedSecurityColumns, sourceName);
        }
        if (TableExists(src, "portfolio_chart"))
        {
            WarnIfMissing(src, "portfolio_chart", ExpectedPortfolioChartColumns, sourceName);
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
    private void WarnIfMissing(SqliteConnection src, string table, string[] expected, string sourceName)
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
                "MoneyMoney schema drift on source '{Source}' table '{Table}': missing columns {Missing}. Affected rows will load with null/default values.",
                sourceName, table, string.Join(",", missing));
        }
    }

    /// <summary>
    /// True when the named column exists on the named table in the source DB. Used to make
    /// optional columns (e.g. SEPA <c>purpose</c>) backward-compatible with older MoneyMoney
    /// snapshots that may not carry them.
    /// </summary>
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Table name is a compile-time-constant identifier from our own import code, never user input; PRAGMA cannot be parameterized.")]
    private static async Task<bool> ColumnExistsAsync(SqliteConnection src, string table, string column, CancellationToken ct)
    {
        await using var cmd = src.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var name = reader.GetString(reader.GetOrdinal("name"));
            if (string.Equals(name, column, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static async Task<int> CopyAccountsAsync(
        SqliteConnection src, BudgetDbContext db, int sourceId, IReadOnlyDictionary<long, string> providers,
        Dictionary<long, ForeignAccountState> foreignState, CancellationToken ct)
    {
        // group_key is the FK into account_groups; older exporters (pre-account-groups) won't
        // have account_groups but the column itself has always been on accounts. Probe anyway
        // so we degrade cleanly if a forked schema ever drops it.
        bool hasGroupKey = await ColumnExistsAsync(src, "accounts", "group_key", ct);
        string groupKeyExpr = hasGroupKey ? "group_key" : "NULL AS group_key";
        string select = $"""
            SELECT rowid, name, iban, currency, type, portfolio, balances, sort, refresh_tsp, error, {groupKeyExpr}
            FROM accounts
            """;
        const string insert = """
            INSERT INTO accounts (Id, SourceId, name, iban, currency, type, portfolio, balances, sort, refresh_tsp, error, group_key, provider)
            VALUES ($id, $sid, $name, $iban, $currency, $type, $portfolio, $balances, $sort, $refresh_tsp, $error, $group_key, $provider)
            """;
        return await BulkCopyAsync(src, db, select, insert, (cmd, r) =>
        {
            long rowid = r.GetInt64(0);
            long id = SourceScope.Namespace(sourceId, rowid);
            cmd.Parameters["$id"].Value = id;
            cmd.Parameters["$sid"].Value = sourceId;
            cmd.Parameters["$provider"].Value = providers.GetValueOrDefault(id, AccountProviders.MoneyMoney);
            cmd.Parameters["$name"].Value = r.IsDBNull(1) ? DBNull.Value : r.GetString(1);
            cmd.Parameters["$iban"].Value = r.IsDBNull(2) ? DBNull.Value : r.GetString(2);
            cmd.Parameters["$currency"].Value = r.IsDBNull(3) ? DBNull.Value : r.GetString(3);
            cmd.Parameters["$type"].Value = r.IsDBNull(4) ? DBNull.Value : r.GetInt64(4);
            cmd.Parameters["$portfolio"].Value = r.IsDBNull(5) ? DBNull.Value : r.GetInt64(5);
            cmd.Parameters["$balances"].Value = r.IsDBNull(6) ? DBNull.Value : r.GetString(6);
            cmd.Parameters["$sort"].Value = r.IsDBNull(7) ? DBNull.Value : r.GetInt64(7);
            cmd.Parameters["$refresh_tsp"].Value = r.IsDBNull(8) ? DBNull.Value : r.GetInt64(8);
            cmd.Parameters["$error"].Value = r.IsDBNull(9) ? DBNull.Value : r.GetString(9);
            cmd.Parameters["$group_key"].Value = r.IsDBNull(10)
                ? DBNull.Value
                : NamespaceParentKey(sourceId, r.GetInt64(10));
            if (foreignState.TryGetValue(id, out var kept))
            {
                // A connector owns this account: keep its balance and refresh state rather than MoneyMoney's.
                cmd.Parameters["$balances"].Value = (object?)kept.Balances ?? DBNull.Value;
                cmd.Parameters["$refresh_tsp"].Value = (object?)kept.RefreshTimestamp ?? DBNull.Value;
                cmd.Parameters["$error"].Value = (object?)kept.Error ?? DBNull.Value;
            }
        }, AccountInsertParameters, ct);
    }

    private static async Task<int> CopyAccountGroupsAsync(SqliteConnection src, BudgetDbContext db, int sourceId, CancellationToken ct)
    {
        // account_groups landed in the exporter after the first releases; tolerate its absence
        // so a stale exporter upload still imports (accounts simply land without a parent ref).
        if (!TableExists(src, "account_groups")) return 0;
        const string select = "SELECT rowid, name, group_key, sort FROM account_groups";
        const string insert = """
            INSERT INTO account_groups (Id, SourceId, name, group_key, sort)
            VALUES ($id, $sid, $name, $group_key, $sort)
            """;
        return await BulkCopyAsync(src, db, select, insert, (cmd, r) =>
        {
            long rowid = r.GetInt64(0);
            cmd.Parameters["$id"].Value = SourceScope.Namespace(sourceId, rowid);
            cmd.Parameters["$sid"].Value = sourceId;
            cmd.Parameters["$name"].Value = r.IsDBNull(1) ? DBNull.Value : r.GetString(1);
            cmd.Parameters["$group_key"].Value = r.IsDBNull(2)
                ? DBNull.Value
                : NamespaceParentKey(sourceId, r.GetInt64(2));
            cmd.Parameters["$sort"].Value = r.IsDBNull(3) ? DBNull.Value : r.GetInt64(3);
        }, AccountGroupInsertParameters, ct);
    }

    private static async Task<int> CopyCategoryGroupsAsync(SqliteConnection src, BudgetDbContext db, int sourceId, CancellationToken ct)
    {
        const string select = "SELECT rowid, name, group_key, sort, color FROM category_groups";
        const string insert = """
            INSERT INTO category_groups (Id, SourceId, name, group_key, sort, color)
            VALUES ($id, $sid, $name, $group_key, $sort, $color)
            """;
        return await BulkCopyAsync(src, db, select, insert, (cmd, r) =>
        {
            long rowid = r.GetInt64(0);
            cmd.Parameters["$id"].Value = SourceScope.Namespace(sourceId, rowid);
            cmd.Parameters["$sid"].Value = sourceId;
            cmd.Parameters["$name"].Value = r.IsDBNull(1) ? DBNull.Value : r.GetString(1);
            cmd.Parameters["$group_key"].Value = r.IsDBNull(2)
                ? DBNull.Value
                : NamespaceParentKey(sourceId, r.GetInt64(2));
            cmd.Parameters["$sort"].Value = r.IsDBNull(3) ? DBNull.Value : r.GetInt64(3);
            cmd.Parameters["$color"].Value = r.IsDBNull(4) ? DBNull.Value : r.GetString(4);
        }, CategoryGroupInsertParameters, ct);
    }

    private static async Task<int> CopyCategoriesAsync(SqliteConnection src, BudgetDbContext db, int sourceId, CancellationToken ct)
    {
        const string select = "SELECT rowid, name, type, color, rules, group_key, sort FROM categories";
        const string insert = """
            INSERT INTO categories (Id, SourceId, name, type, color, rules, group_key, sort)
            VALUES ($id, $sid, $name, $type, $color, $rules, $group_key, $sort)
            """;
        return await BulkCopyAsync(src, db, select, insert, (cmd, r) =>
        {
            long rowid = r.GetInt64(0);
            cmd.Parameters["$id"].Value = SourceScope.Namespace(sourceId, rowid);
            cmd.Parameters["$sid"].Value = sourceId;
            cmd.Parameters["$name"].Value = r.IsDBNull(1) ? DBNull.Value : r.GetString(1);
            cmd.Parameters["$type"].Value = r.IsDBNull(2) ? DBNull.Value : r.GetInt64(2);
            cmd.Parameters["$color"].Value = r.IsDBNull(3) ? DBNull.Value : r.GetString(3);
            cmd.Parameters["$rules"].Value = r.IsDBNull(4) ? DBNull.Value : r.GetString(4);
            cmd.Parameters["$group_key"].Value = r.IsDBNull(5)
                ? DBNull.Value
                : NamespaceParentKey(sourceId, r.GetInt64(5));
            cmd.Parameters["$sort"].Value = r.IsDBNull(6) ? DBNull.Value : r.GetInt64(6);
        }, CategoryInsertParameters, ct);
    }

    private static async Task<int> CopyTransactionsAsync(
        SqliteConnection src, BudgetDbContext db, int sourceId, HashSet<long> foreignAccountIds, CancellationToken ct)
    {
        // MoneyMoney's SEPA "Verwendungszweck" lives in unformatted_purpose (verified against
        // a live MoneyMoney 2.5-era SQLCipher db: rows there carry the BILLCOLL/EREF/MREF/CRED
        // raw text users see in the transaction detail pane). Older / forked schemas may not
        // have it — probe at runtime and substitute NULL so the import still succeeds.
        bool hasPurpose = await ColumnExistsAsync(src, "transactions", "unformatted_purpose", ct);
        string purposeExpr = hasPurpose ? "unformatted_purpose" : "NULL AS unformatted_purpose";
        // booked: 0 = pending ("vorgemerkt"). Missing column => everything counts as booked.
        bool hasBooked = await ColumnExistsAsync(src, "transactions", "booked", ct);
        string bookedExpr = hasBooked ? "booked" : "1 AS booked";
        // split_key links a child (splitted=2) to its parent's rowid. Without it we cannot tell
        // how much of a parent is allocated, so parents keep the legacy "excluded" behavior.
        bool hasSplitKey = await ColumnExistsAsync(src, "transactions", "split_key", ct);
        string splitKeyExpr = hasSplitKey ? "split_key" : "NULL AS split_key";
        var childSumsCents = hasSplitKey ? await ReadSplitChildSumsAsync(src, ct) : new Dictionary<long, long>();

        string select = $"""
            SELECT rowid, local_account_key, amount, currency, timestamp, name, comment, {purposeExpr}, category_key, checkmark, splitted, {bookedExpr}, {splitKeyExpr}
            FROM transactions
            """;
        const string insert = """
            INSERT INTO transactions (Id, SourceId, local_account_key, amount, currency, timestamp, name, comment, purpose, category_key, checkmark, splitted, booked, split_parent_id, split_original_amount)
            VALUES ($id, $sid, $local_account_key, $amount, $currency, $timestamp, $name, $comment, $purpose, $category_key, $checkmark, $splitted, $booked, $split_parent_id, $split_original_amount)
            """;
        return await BulkCopyAsync(src, db, select, insert, (cmd, r) =>
        {
            long rowid = r.GetInt64(0);
            cmd.Parameters["$id"].Value = SourceScope.Namespace(sourceId, rowid);
            cmd.Parameters["$sid"].Value = sourceId;
            cmd.Parameters["$local_account_key"].Value = r.IsDBNull(1)
                ? DBNull.Value
                : SourceScope.Namespace(sourceId, r.GetInt64(1));
            // MoneyMoney stores amounts as REAL euros (or other currency); we keep them as
            // integer cents downstream so DB-side SUM is exact.
            long? amountCents = r.IsDBNull(2) ? null : MoneyMath.EurosToCents(r.GetDouble(2));
            cmd.Parameters["$amount"].Value = (object?)amountCents ?? DBNull.Value;
            cmd.Parameters["$currency"].Value = r.IsDBNull(3) ? DBNull.Value : r.GetString(3);
            cmd.Parameters["$timestamp"].Value = r.IsDBNull(4) ? DBNull.Value : r.GetInt64(4);
            cmd.Parameters["$name"].Value = r.IsDBNull(5) ? DBNull.Value : r.GetString(5);
            cmd.Parameters["$comment"].Value = r.IsDBNull(6) ? DBNull.Value : r.GetString(6);
            cmd.Parameters["$purpose"].Value = r.IsDBNull(7) ? DBNull.Value : r.GetString(7);
            cmd.Parameters["$category_key"].Value = r.IsDBNull(8)
                ? DBNull.Value
                : SourceScope.Namespace(sourceId, r.GetInt64(8));
            cmd.Parameters["$checkmark"].Value = r.IsDBNull(9) ? DBNull.Value : r.GetInt64(9);
            long? splitted = r.IsDBNull(10) ? null : r.GetInt64(10);
            cmd.Parameters["$splitted"].Value = (object?)splitted ?? DBNull.Value;
            cmd.Parameters["$booked"].Value = r.IsDBNull(11) || r.GetInt64(11) != 0 ? 1 : 0;
            cmd.Parameters["$split_parent_id"].Value = DBNull.Value;
            cmd.Parameters["$split_original_amount"].Value = DBNull.Value;

            if (splitted == SplitState.Child && hasSplitKey && !r.IsDBNull(12) && r.GetInt64(12) != 0)
            {
                cmd.Parameters["$split_parent_id"].Value = SourceScope.Namespace(sourceId, r.GetInt64(12));
            }
            else if (splitted == SplitState.Parent && hasSplitKey)
            {
                long original = amountCents ?? 0;
                cmd.Parameters["$split_original_amount"].Value = original;
                long remainder = SplitMath.Remainder(original, childSumsCents.GetValueOrDefault(rowid));
                if (remainder != 0)
                {
                    // Children don't cover the parent: turn it into an uncategorized normal row
                    // for the unallocated part so every total picks it up without special cases.
                    cmd.Parameters["$splitted"].Value = SplitState.Remainder;
                    cmd.Parameters["$amount"].Value = remainder;
                    cmd.Parameters["$category_key"].Value = DBNull.Value;
                }
            }
        }, TransactionInsertParameters, ct, skipRow: r =>
            !r.IsDBNull(1) && foreignAccountIds.Contains(SourceScope.Namespace(sourceId, r.GetInt64(1))));
    }

    /// <summary>Sum of child amounts (cents) per split parent rowid.</summary>
    private static async Task<Dictionary<long, long>> ReadSplitChildSumsAsync(SqliteConnection src, CancellationToken ct)
    {
        var sums = new Dictionary<long, long>();
        await using var cmd = src.CreateCommand();
        cmd.CommandText = "SELECT split_key, amount FROM transactions WHERE splitted = 2 AND split_key IS NOT NULL AND split_key <> 0 AND amount IS NOT NULL";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            long parent = reader.GetInt64(0);
            sums[parent] = sums.GetValueOrDefault(parent) + MoneyMath.EurosToCents(reader.GetDouble(1));
        }
        return sums;
    }

    private static async Task<int> CopySecuritiesAsync(SqliteConnection src, BudgetDbContext db, int sourceId, HashSet<long> foreignAccountIds, CancellationToken ct)
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
            INSERT INTO holdings (Id, SourceId, account_key, name, isin, wkn, symbol, quantity, amount, amount_cur, price, price_cur, purchase, asset_class)
            VALUES ($id, $sid, $account_key, $name, $isin, $wkn, $symbol, $quantity, $amount, $amount_cur, $price, $price_cur, $purchase, $asset_class)
            """;
        return await BulkCopyAsync(src, db, select, insert, (cmd, r) =>
        {
            long rowid = r.GetInt64(0);
            cmd.Parameters["$id"].Value = SourceScope.Namespace(sourceId, rowid);
            cmd.Parameters["$sid"].Value = sourceId;
            cmd.Parameters["$account_key"].Value = SourceScope.Namespace(sourceId, r.GetInt64(1));
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
        }, SecurityInsertParameters, ct, skipRow: r => foreignAccountIds.Contains(SourceScope.Namespace(sourceId, r.GetInt64(1))));
    }

    private static async Task<int> CopyPortfolioChartAsync(SqliteConnection src, BudgetDbContext db, int sourceId, HashSet<long> foreignAccountIds, CancellationToken ct)
    {
        // portfolio_chart, like securities, only ships in newer exporter builds. securities_key is
        // namespaced but carries no FK (it can reference deleted positions we don't import), so a
        // 0 sentinel maps to NULL like other parent keys.
        if (!TableExists(src, "portfolio_chart")) return 0;
        const string select = "SELECT rowid, account_key, currency, timestamp, balance, securities_key FROM portfolio_chart";
        const string insert = """
            INSERT INTO portfolio_chart (Id, SourceId, account_key, currency, timestamp, balance, securities_key)
            VALUES ($id, $sid, $account_key, $currency, $timestamp, $balance, $securities_key)
            """;
        return await BulkCopyAsync(src, db, select, insert, (cmd, r) =>
        {
            long rowid = r.GetInt64(0);
            cmd.Parameters["$id"].Value = SourceScope.Namespace(sourceId, rowid);
            cmd.Parameters["$sid"].Value = sourceId;
            cmd.Parameters["$account_key"].Value = SourceScope.Namespace(sourceId, r.GetInt64(1));
            cmd.Parameters["$currency"].Value = r.IsDBNull(2) ? DBNull.Value : r.GetString(2);
            cmd.Parameters["$timestamp"].Value = r.IsDBNull(3) ? DBNull.Value : r.GetInt64(3);
            cmd.Parameters["$balance"].Value = r.IsDBNull(4) ? DBNull.Value : r.GetDouble(4);
            cmd.Parameters["$securities_key"].Value = r.IsDBNull(5)
                ? DBNull.Value
                : NamespaceParentKey(sourceId, r.GetInt64(5));
        }, PortfolioChartInsertParameters, ct, skipRow: r => foreignAccountIds.Contains(SourceScope.Namespace(sourceId, r.GetInt64(1))));
    }

    /// <summary>
    /// Namespace a MoneyMoney parent/group rowid into our ID space. MoneyMoney uses
    /// <c>0</c> as the "no parent" / "no group" sentinel; we map that to <see cref="DBNull"/>
    /// so the FK actually resolves (and the deferred FK check at migration commit doesn't
    /// trip on dangling references).
    /// </summary>
    private static object NamespaceParentKey(int sourceId, long parentRowid)
    {
        return parentRowid == 0 ? DBNull.Value : SourceScope.Namespace(sourceId, parentRowid);
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

/// <summary>Balance and refresh state of an account owned by a bank connector, preserved across MoneyMoney imports.</summary>
internal sealed record ForeignAccountState(string? Balances, long? RefreshTimestamp, string? Error);

public class ImportResult
{
    public int SourceId { get; set; }
    public string SourceName { get; set; } = string.Empty;
    public int AccountsImported { get; set; }
    public int AccountGroupsImported { get; set; }
    public int CategoryGroupsImported { get; set; }
    public int CategoriesImported { get; set; }
    public int TransactionsImported { get; set; }
    public int SecuritiesImported { get; set; }
    public int PortfolioChartPointsImported { get; set; }
    public long FileSizeBytes { get; set; }
}
