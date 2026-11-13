using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>
/// Read-only reporting on how stale the imported imported data is — surfaced at the top of
/// the dashboard so projections built on stale numbers don't silently mislead. Split out from
/// BudgetService since it shares nothing with budgets / projects / cashflow beyond the
/// DbContext and a ownerId.
/// </summary>
public class FreshnessService
{
    private readonly IDbContextFactory<BudgetDbContext> _dbFactory;

    public FreshnessService(IDbContextFactory<BudgetDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    /// <summary>
    /// Returns, per owner of an account the user sees, when their MoneyMoney data was last imported (LastUploadAt)
    /// plus the account-refresh snapshot. The dashboard uses this to flag stale data; projection numbers built on
    /// stale data silently lie, so a visible indicator matters.
    /// </summary>
    public async Task<List<DataFreshness>> GetDataFreshnessAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var accountsBySource = (await db.Accounts
            .Select(a => new { a.OwnerUserId, a.Id, a.Name, a.RefreshTimestamp, a.Error })
            .ToListAsync())
            .GroupBy(a => a.OwnerUserId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(a => new AccountFreshness
            {
                AccountId = a.Id,
                Name = a.Name,
                RefreshTimestamp = a.RefreshTimestamp,
                Error = a.Error
            }).ToList(), StringComparer.Ordinal);

        var owners = accountsBySource.Keys.Order(StringComparer.Ordinal).ToList();
        var uploads = await db.UserDataStates.IgnoreQueryFilters().AsNoTracking().Where(s => owners.Contains(s.UserId))
            .ToDictionaryAsync(s => s.UserId, s => s.LastUploadAt, StringComparer.Ordinal);
        return owners.Select(o => new DataFreshness
        {
            OwnerUserId = o,
            DbUploadedUtc = uploads.GetValueOrDefault(o),
            Accounts = accountsBySource[o]
        }).ToList();
    }

    /// <summary>
    /// Counts imported transactions that aren't assigned to a real category and returns the
    /// summed absolute amount. Surfaced on the dashboard so users see when budget rollups are
    /// silently undercounting because transactions slipped past categorization in the source app —
    /// the absolute sum is the decision signal (€5,000 missing rent vs €50 of coffee).
    ///
    /// The source app represents "no category" by pinning the transaction to a sentinel category
    /// with an empty name (typically rowid=1) rather than leaving category_key NULL, so both
    /// shapes count. Split-parent rows (Splitted=1) carry that same sentinel as a placeholder
    /// — their real categories live on the child rows — so they're excluded to match
    /// the source's own "Uncategorized" view.
    /// </summary>
    public async Task<Dictionary<string, UncategorizedSummary>> GetUncategorizedTransactionSummaryAsync()
    {
        // Grouped per owner of the visible transactions so the dashboard can render a banner per owner. Two queries
        // (count and sum) per owner.
        await using var db = await _dbFactory.CreateDbContextAsync();
        var ownerIds = await db.Transactions.Select(t => t.OwnerUserId).Distinct().ToListAsync();
        var result = new Dictionary<string, UncategorizedSummary>(StringComparer.Ordinal);
        foreach (var ownerId in ownerIds)
        {
            var query = db.Transactions
                .Where(t => t.OwnerUserId == ownerId)
                .CountedAsFlow()
                .WhereUncategorized(db, ownerId);

            var count = await query.CountAsync();
            var absSumCents = count == 0 ? 0L : await query.SumAsync(t => Math.Abs(t.Amount));
            result[ownerId] = new UncategorizedSummary { OwnerUserId = ownerId, Count = count, AbsoluteAmount = absSumCents / 100m };
        }
        return result;
    }
}
