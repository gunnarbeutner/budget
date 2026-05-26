using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using OpenBudget.Models;

namespace OpenBudget.Services;

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
    // URL-safe slug; same shape we accept in the upload route.
    public static readonly Regex SlugRegex = new("^[a-z0-9][a-z0-9-]{0,99}$", RegexOptions.Compiled);

    private static readonly string[] ExpectedAccountColumns =
    {
        "name", "iban", "currency", "type", "portfolio", "balances", "sort", "refresh_tsp", "error"
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
        "category_key", "checkmark", "splitted"
    };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MoneyMoneyImportService> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _perSourceLocks = new();

    public MoneyMoneyImportService(IServiceScopeFactory scopeFactory, ILogger<MoneyMoneyImportService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public static bool IsValidSlug(string name) => !string.IsNullOrEmpty(name) && SlugRegex.IsMatch(name);

    public async Task<ImportResult> ImportFromFileAsync(string sourceName, string sqliteFilePath, CancellationToken ct)
    {
        if (!IsValidSlug(sourceName))
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
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BudgetDbContext>();

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
        // Our mm_* tables declare FKs that EF generated from the model, so we silence enforcement
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
            // Wipe previous rows for this source. Order matters: transactions reference
            // accounts/categories via FKs (Restrict), so transactions go first.
            await db.Database.ExecuteSqlRawAsync("DELETE FROM mm_transactions WHERE SourceId = {0}", new object[] { sourceId }, ct);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM mm_categories WHERE SourceId = {0}", new object[] { sourceId }, ct);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM mm_category_groups WHERE SourceId = {0}", new object[] { sourceId }, ct);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM mm_accounts WHERE SourceId = {0}", new object[] { sourceId }, ct);

            int accountCount = await CopyAccountsAsync(src, db, sourceId, ct);
            int groupCount = await CopyCategoryGroupsAsync(src, db, sourceId, ct);
            int categoryCount = await CopyCategoriesAsync(src, db, sourceId, ct);
            int txCount = await CopyTransactionsAsync(src, db, sourceId, ct);

            long fileSize = new FileInfo(sqliteFilePath).Length;
            source.LastUploadAt = DateTime.UtcNow;
            source.LastUploadSizeBytes = fileSize;
            await db.SaveChangesAsync(ct);

            await tx.CommitAsync(ct);

            _logger.LogInformation(
                "Imported MoneyMoney source '{Source}' (id={SourceId}): {Accounts} accounts, {Groups} groups, {Categories} categories, {Transactions} transactions ({Bytes} bytes)",
                sourceName, sourceId, accountCount, groupCount, categoryCount, txCount, fileSize);

            return new ImportResult
            {
                SourceId = sourceId,
                SourceName = sourceName,
                AccountsImported = accountCount,
                CategoryGroupsImported = groupCount,
                CategoriesImported = categoryCount,
                TransactionsImported = txCount,
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

    private void WarnOnSchemaDrift(SqliteConnection src, string sourceName)
    {
        WarnIfMissing(src, "accounts", ExpectedAccountColumns, sourceName);
        WarnIfMissing(src, "categories", ExpectedCategoryColumns, sourceName);
        WarnIfMissing(src, "category_groups", ExpectedCategoryGroupColumns, sourceName);
        WarnIfMissing(src, "transactions", ExpectedTransactionColumns, sourceName);
    }

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

    private static async Task<int> CopyAccountsAsync(SqliteConnection src, BudgetDbContext db, int sourceId, CancellationToken ct)
    {
        const string select = "SELECT rowid, name, iban, currency, type, portfolio, balances, sort, refresh_tsp, error FROM accounts";
        const string insert = """
            INSERT INTO mm_accounts (Id, SourceId, name, iban, currency, type, portfolio, balances, sort, refresh_tsp, error)
            VALUES ($id, $sid, $name, $iban, $currency, $type, $portfolio, $balances, $sort, $refresh_tsp, $error)
            """;
        return await BulkCopyAsync(src, db, select, insert, sourceId, (cmd, r) =>
        {
            long rowid = r.GetInt64(0);
            cmd.Parameters["$id"].Value = SourceScope.Namespace(sourceId, rowid);
            cmd.Parameters["$sid"].Value = sourceId;
            cmd.Parameters["$name"].Value = r.IsDBNull(1) ? DBNull.Value : r.GetString(1);
            cmd.Parameters["$iban"].Value = r.IsDBNull(2) ? DBNull.Value : r.GetString(2);
            cmd.Parameters["$currency"].Value = r.IsDBNull(3) ? DBNull.Value : r.GetString(3);
            cmd.Parameters["$type"].Value = r.IsDBNull(4) ? DBNull.Value : r.GetInt64(4);
            cmd.Parameters["$portfolio"].Value = r.IsDBNull(5) ? DBNull.Value : r.GetInt64(5);
            cmd.Parameters["$balances"].Value = r.IsDBNull(6) ? DBNull.Value : r.GetString(6);
            cmd.Parameters["$sort"].Value = r.IsDBNull(7) ? DBNull.Value : r.GetInt64(7);
            cmd.Parameters["$refresh_tsp"].Value = r.IsDBNull(8) ? DBNull.Value : r.GetInt64(8);
            cmd.Parameters["$error"].Value = r.IsDBNull(9) ? DBNull.Value : r.GetString(9);
        }, new[] { "$id", "$sid", "$name", "$iban", "$currency", "$type", "$portfolio", "$balances", "$sort", "$refresh_tsp", "$error" }, ct);
    }

    private static async Task<int> CopyCategoryGroupsAsync(SqliteConnection src, BudgetDbContext db, int sourceId, CancellationToken ct)
    {
        const string select = "SELECT rowid, name, group_key, sort, color FROM category_groups";
        const string insert = """
            INSERT INTO mm_category_groups (Id, SourceId, name, group_key, sort, color)
            VALUES ($id, $sid, $name, $group_key, $sort, $color)
            """;
        return await BulkCopyAsync(src, db, select, insert, sourceId, (cmd, r) =>
        {
            long rowid = r.GetInt64(0);
            cmd.Parameters["$id"].Value = SourceScope.Namespace(sourceId, rowid);
            cmd.Parameters["$sid"].Value = sourceId;
            cmd.Parameters["$name"].Value = r.IsDBNull(1) ? DBNull.Value : r.GetString(1);
            cmd.Parameters["$group_key"].Value = r.IsDBNull(2)
                ? DBNull.Value
                : NamespaceParentGroup(sourceId, r.GetInt64(2));
            cmd.Parameters["$sort"].Value = r.IsDBNull(3) ? DBNull.Value : r.GetInt64(3);
            cmd.Parameters["$color"].Value = r.IsDBNull(4) ? DBNull.Value : r.GetString(4);
        }, new[] { "$id", "$sid", "$name", "$group_key", "$sort", "$color" }, ct);
    }

    private static async Task<int> CopyCategoriesAsync(SqliteConnection src, BudgetDbContext db, int sourceId, CancellationToken ct)
    {
        const string select = "SELECT rowid, name, type, color, rules, group_key, sort FROM categories";
        const string insert = """
            INSERT INTO mm_categories (Id, SourceId, name, type, color, rules, group_key, sort)
            VALUES ($id, $sid, $name, $type, $color, $rules, $group_key, $sort)
            """;
        return await BulkCopyAsync(src, db, select, insert, sourceId, (cmd, r) =>
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
                : (object)SourceScope.Namespace(sourceId, r.GetInt64(5));
            cmd.Parameters["$sort"].Value = r.IsDBNull(6) ? DBNull.Value : r.GetInt64(6);
        }, new[] { "$id", "$sid", "$name", "$type", "$color", "$rules", "$group_key", "$sort" }, ct);
    }

    private static async Task<int> CopyTransactionsAsync(SqliteConnection src, BudgetDbContext db, int sourceId, CancellationToken ct)
    {
        const string select = """
            SELECT rowid, local_account_key, amount, currency, timestamp, name, comment, category_key, checkmark, splitted
            FROM transactions
            """;
        const string insert = """
            INSERT INTO mm_transactions (Id, SourceId, local_account_key, amount, currency, timestamp, name, comment, category_key, checkmark, splitted)
            VALUES ($id, $sid, $local_account_key, $amount, $currency, $timestamp, $name, $comment, $category_key, $checkmark, $splitted)
            """;
        return await BulkCopyAsync(src, db, select, insert, sourceId, (cmd, r) =>
        {
            long rowid = r.GetInt64(0);
            cmd.Parameters["$id"].Value = SourceScope.Namespace(sourceId, rowid);
            cmd.Parameters["$sid"].Value = sourceId;
            cmd.Parameters["$local_account_key"].Value = r.IsDBNull(1)
                ? DBNull.Value
                : (object)SourceScope.Namespace(sourceId, r.GetInt64(1));
            // MoneyMoney stores amounts as REAL euros (or other currency); we keep them as
            // integer cents downstream so DB-side SUM is exact.
            cmd.Parameters["$amount"].Value = r.IsDBNull(2)
                ? DBNull.Value
                : (object)MoneyMath.EurosToCents(r.GetDouble(2));
            cmd.Parameters["$currency"].Value = r.IsDBNull(3) ? DBNull.Value : r.GetString(3);
            cmd.Parameters["$timestamp"].Value = r.IsDBNull(4) ? DBNull.Value : r.GetInt64(4);
            cmd.Parameters["$name"].Value = r.IsDBNull(5) ? DBNull.Value : r.GetString(5);
            cmd.Parameters["$comment"].Value = r.IsDBNull(6) ? DBNull.Value : r.GetString(6);
            cmd.Parameters["$category_key"].Value = r.IsDBNull(7)
                ? DBNull.Value
                : (object)SourceScope.Namespace(sourceId, r.GetInt64(7));
            cmd.Parameters["$checkmark"].Value = r.IsDBNull(8) ? DBNull.Value : r.GetInt64(8);
            cmd.Parameters["$splitted"].Value = r.IsDBNull(9) ? DBNull.Value : r.GetInt64(9);
        }, new[] { "$id", "$sid", "$local_account_key", "$amount", "$currency", "$timestamp", "$name", "$comment", "$category_key", "$checkmark", "$splitted" }, ct);
    }

    private static object NamespaceParentGroup(int sourceId, long parentRowid)
    {
        // ParentGroupId stays as a foreign key into mm_category_groups (Id column), so apply
        // the same namespacing. 0 is the "no parent" sentinel; namespace it too so it sits in
        // this source's space (queries treat 0/sentinel as roots).
        return (object)SourceScope.Namespace(sourceId, parentRowid);
    }

    private static async Task<int> BulkCopyAsync(
        SqliteConnection src,
        BudgetDbContext db,
        string selectSql,
        string insertSql,
        int sourceId,
        Action<SqliteCommand, SqliteDataReader> bind,
        string[] parameterNames,
        CancellationToken ct)
    {
        // Reader on the source DB; one INSERT command on the destination, executed once per row
        // inside a single Database transaction. SQLite handles ~50k inserts/sec like this when
        // SourceConn and DestConn are separate (no journal contention).
        var destConn = (SqliteConnection)db.Database.GetDbConnection();
        if (destConn.State != System.Data.ConnectionState.Open)
            await destConn.OpenAsync(ct);
        var destTx = db.Database.CurrentTransaction?.GetDbTransaction() as SqliteTransaction;

        await using var insertCmd = destConn.CreateCommand();
        insertCmd.CommandText = insertSql;
        if (destTx != null) insertCmd.Transaction = destTx;
        foreach (var p in parameterNames)
            insertCmd.Parameters.Add(new SqliteParameter(p, DBNull.Value));

        await using var selectCmd = src.CreateCommand();
        selectCmd.CommandText = selectSql;
        await using var reader = await selectCmd.ExecuteReaderAsync(ct);

        int count = 0;
        while (await reader.ReadAsync(ct))
        {
            bind(insertCmd, reader);
            await insertCmd.ExecuteNonQueryAsync(ct);
            count++;
        }
        return count;
    }
}

public class ImportResult
{
    public int SourceId { get; set; }
    public string SourceName { get; set; } = string.Empty;
    public int AccountsImported { get; set; }
    public int CategoryGroupsImported { get; set; }
    public int CategoriesImported { get; set; }
    public int TransactionsImported { get; set; }
    public long FileSizeBytes { get; set; }
}
