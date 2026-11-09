using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace OpenBudget.Services;

/// <summary>Importer-independent helpers for named data sources.</summary>
public static class SourceData
{
    // URL-safe slug; same shape we accept in the upload routes.
    public static readonly Regex SlugRegex = new("^[a-z0-9][a-z0-9-]{0,99}$", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    public static bool IsValidSlug(string name) => !string.IsNullOrEmpty(name) && SlugRegex.IsMatch(name);

    /// <summary>
    /// Deletes every imported row for a source — transactions, categories, category groups,
    /// accounts, account groups — in one transaction. FK enforcement is disabled on the connection
    /// first because account_groups and category_groups carry self-referential group_key FKs
    /// (ON DELETE RESTRICT): a bulk delete would otherwise fail with "FOREIGN KEY constraint failed"
    /// the moment a parent group is removed while a child still points at it. The PRAGMA must be
    /// toggled outside any transaction or SQLite silently ignores it, and it's restored in the
    /// finally so EF's later SaveChanges behave normally. Does not remove the import_sources
    /// row itself — the caller owns that.
    /// </summary>
    public static async Task DeleteSourceDataAsync(BudgetDbContext db, int sourceId, CancellationToken ct = default)
    {
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
            await conn.OpenAsync(ct);
        await using (var pragmaOff = conn.CreateCommand())
        {
            pragmaOff.CommandText = "PRAGMA foreign_keys = OFF";
            await pragmaOff.ExecuteNonQueryAsync(ct);
        }
        try
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            // Order mirrors FK direction for clarity even with enforcement off: transactions and
            // securities reference accounts/categories; accounts reference account_groups.
            await db.Database.ExecuteSqlRawAsync("DELETE FROM transactions WHERE SourceId = {0}", new object[] { sourceId }, ct);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM portfolio_chart WHERE SourceId = {0}", new object[] { sourceId }, ct);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM holdings WHERE SourceId = {0}", new object[] { sourceId }, ct);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM categories WHERE SourceId = {0}", new object[] { sourceId }, ct);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM category_groups WHERE SourceId = {0}", new object[] { sourceId }, ct);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM accounts WHERE SourceId = {0}", new object[] { sourceId }, ct);
            await db.Database.ExecuteSqlRawAsync("DELETE FROM account_groups WHERE SourceId = {0}", new object[] { sourceId }, ct);
            // App-native PortfolioPerformance taxonomy is scoped to the same source; drop it too so
            // removing a source leaves nothing behind.
            await db.Database.ExecuteSqlRawAsync("DELETE FROM pp_security_taxonomy WHERE SourceId = {0}", new object[] { sourceId }, ct);
            await tx.CommitAsync(ct);
        }
        finally
        {
            await using var pragmaOn = conn.CreateCommand();
            pragmaOn.CommandText = "PRAGMA foreign_keys = ON";
            await pragmaOn.ExecuteNonQueryAsync(ct);
        }
    }
}
