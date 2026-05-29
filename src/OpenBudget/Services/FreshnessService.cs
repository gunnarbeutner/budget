using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>
/// Read-only reporting on how stale the imported MoneyMoney data is — surfaced at the top of
/// the dashboard so projections built on stale numbers don't silently mislead. Split out from
/// BudgetService since it shares nothing with budgets / projects / cashflow beyond the
/// DbContext and a sourceId.
/// </summary>
public class FreshnessService
{
    private readonly IDbContextFactory<BudgetDbContext> _dbFactory;

    public FreshnessService(IDbContextFactory<BudgetDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    /// <summary>
    /// Returns when each registered MoneyMoney source was last imported (LastUploadAt) plus the
    /// account-refresh snapshot for each source. The dashboard uses this to flag stale data;
    /// projection numbers built on stale data silently lie, so a visible indicator matters.
    /// </summary>
    public async Task<List<DataFreshness>> GetDataFreshnessAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var sources = await db.ImportSources
            .OrderBy(s => s.Name)
            .ToListAsync();

        var accountsBySource = (await db.Accounts
            .Select(a => new { a.SourceId, a.Id, a.Name, a.RefreshTimestamp, a.Error })
            .ToListAsync())
            .GroupBy(a => a.SourceId)
            .ToDictionary(g => g.Key, g => g.Select(a => new AccountFreshness
            {
                AccountId = a.Id,
                Name = a.Name,
                RefreshTimestamp = a.RefreshTimestamp,
                Error = a.Error
            }).ToList());

        return sources.Select(s => new DataFreshness
        {
            SourceId = s.Id,
            SourceName = s.Name,
            DbUploadedUtc = s.LastUploadAt,
            Accounts = accountsBySource.TryGetValue(s.Id, out var list) ? list : new List<AccountFreshness>()
        }).ToList();
    }

    /// <summary>
    /// Counts MoneyMoney transactions that aren't assigned to a real category and returns the
    /// summed absolute amount. Surfaced on the dashboard so users see when budget rollups are
    /// silently undercounting because transactions slipped past categorization in MoneyMoney —
    /// the absolute sum is the decision signal (€5,000 missing rent vs €50 of coffee).
    ///
    /// MoneyMoney represents "no category" by pinning the transaction to a sentinel category
    /// with an empty name (typically rowid=1) rather than leaving category_key NULL, so both
    /// shapes count. Split-parent rows (Splitted=1) carry that same sentinel as a placeholder
    /// — their real categories live on the child rows — so they're excluded to match
    /// MoneyMoney's own "Uncategorized" view.
    /// </summary>
    public async Task<Dictionary<int, UncategorizedSummary>> GetUncategorizedTransactionSummaryAsync()
    {
        // Group per source so the dashboard can render a banner per source. Two queries (count
        // and sum) per source — fine even at a dozen sources.
        await using var db = await _dbFactory.CreateDbContextAsync();
        var sourceIds = await db.ImportSources.Select(s => s.Id).ToListAsync();
        var result = new Dictionary<int, UncategorizedSummary>();
        foreach (var sourceId in sourceIds)
        {
            var query = db.Transactions
                .Where(t => t.SourceId == sourceId)
                .Where(t => t.Splitted != 1)
                .Where(t => t.CategoryId == null
                    || !db.Categories.Any(c => c.SourceId == sourceId && c.Id == t.CategoryId && !string.IsNullOrEmpty(c.Name)));

            var count = await query.CountAsync();
            var absSumCents = count == 0 ? 0L : await query.SumAsync(t => Math.Abs(t.Amount));
            result[sourceId] = new UncategorizedSummary { SourceId = sourceId, Count = count, AbsoluteAmount = absSumCents / 100m };
        }
        return result;
    }
}
