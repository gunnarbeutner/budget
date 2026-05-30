using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>
/// Reads the inputs the Cashflow page needs to draw the year-balance line: per-day account
/// deltas anchored to the real starting balance, and per-item day-of-month distributions
/// learned from historical transactions for projecting future months realistically.
/// </summary>
public class CashflowProjectionService
{
    private readonly IDbContextFactory<BudgetDbContext> _dbFactory;
    private readonly CategoryHierarchyService _hierarchy;
    private readonly ProjectService _projects;

    public CashflowProjectionService(
        IDbContextFactory<BudgetDbContext> dbFactory,
        CategoryHierarchyService hierarchy,
        ProjectService projects)
    {
        _dbFactory = dbFactory;
        _hierarchy = hierarchy;
        _projects = projects;
    }

    /// <summary>
    /// Returns the project's real per-day balance delta for the calendar year, starting from the
    /// project-accounts' actual combined balance on Jan 1. Deltas reflect transactions on assigned
    /// accounts filtered by scope: Exclusive includes every transaction in the account; Shared
    /// includes only transactions whose category is in the project's tracked-categories set.
    ///
    /// Starting balance is computed by reversing ALL transactions on the assigned accounts from
    /// year start through "now" out of the live account balance — so the line is anchored to a
    /// real-world number regardless of scope. Days beyond today have zero delta; the caller can
    /// layer projection on top.
    /// </summary>
    public async Task<ProjectDailyDeltaResult> GetProjectActualDailyDeltaAsync(int projectId, int year)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var project = await db.Projects
            .Where(p => p.Id == projectId)
            .Select(p => new { p.Id, p.SourceId })
            .FirstOrDefaultAsync();
        if (project == null) return new ProjectDailyDeltaResult { DailyDelta = Array.Empty<decimal>() };
        var sourceId = project.SourceId;

        // Informational accounts (e.g. securities depots) are linked for Browse + balance totals
        // only; they must not move the projected cash trajectory, so drop them here. The filter
        // cascades to accountIds / currentBalance / the YTD + per-day transaction sums below.
        var assignments = (await db.ProjectAccountAssignments
            .Where(paa => paa.ProjectId == projectId)
            .ToListAsync())
            .Where(a => a.Scope != AccountScope.Informational)
            .ToList();

        int daysInYear = DateTime.IsLeapYear(year) ? 366 : 365;
        var result = new ProjectDailyDeltaResult
        {
            DailyDelta = new decimal[daysInYear],
            HasAccounts = assignments.Count > 0
        };
        if (!result.HasAccounts) return result;

        var accountIds = assignments.Select(a => a.AccountId).ToHashSet();
        var exclusiveIds = assignments.Where(a => a.Scope == AccountScope.Exclusive).Select(a => a.AccountId).ToHashSet();
        var sharedIds = assignments.Where(a => a.Scope == AccountScope.Shared).Select(a => a.AccountId).ToHashSet();

        var accounts = await db.Accounts
            .Where(a => a.SourceId == sourceId && accountIds.Contains(a.Id))
            .ToListAsync();
        decimal currentBalance = accounts.Sum(a => a.CurrentBalance ?? 0m);

        // Resolve project's leaf category IDs once — only needed when at least one account is Shared.
        HashSet<long> projectLeafCategoryIds = new();
        if (sharedIds.Count > 0)
        {
            var trackingItems = await _projects.GetProjectTrackingItemsAsync(projectId);
            var items = trackingItems.ToTrackedKeys().ToList();
            var allCategoriesWithCounts = await _hierarchy.GetAllCategoriesWithChildCountsAsync(sourceId);
            var itemToLeaf = CategoryHierarchyService.BuildItemToLeafCategoryIds(items, allCategoriesWithCounts);
            projectLeafCategoryIds = itemToLeaf.Values.SelectMany(s => s).ToHashSet();
        }

        var yearRange = TimeRange.Year(year);
        var nowTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Anchor the line on Jan 1 by reversing all booked transactions through "now" out of the
        // live balance. Both this sum and the per-day delta walk below MUST use the same upper
        // bound — MoneyMoney stores future-dated entries (scheduled payments) in the same table,
        // and including them in one but not the other lets currentBalance fall out of sync with
        // sum(DailyDelta), making balanceAtProjStart drift away from the real account balance.
        var clampedEnd = Math.Max(yearRange.StartTs, Math.Min(nowTs, yearRange.EndTsExclusive));
        var ytdRange = new TimeRange(yearRange.StartTs, clampedEnd);

        decimal ytdSumAllScopes = 0m;
        if (clampedEnd > yearRange.StartTs)
        {
            ytdSumAllScopes = (await db.Transactions
                .Where(t => t.SourceId == sourceId)
                .Where(t => t.AccountId.HasValue && accountIds.Contains(t.AccountId.Value))
                .WithinUtc(ytdRange)
                .SumAsync(t => t.Amount)) / 100m;
        }
        // selectedYear in the future: clampedEnd == yearRange.StartTs, ytdSumAllScopes stays 0,
        // and StartingBalance defaults to currentBalance — a known approximation flagged in the UI.
        result.StartingBalance = currentBalance - ytdSumAllScopes;

        var yearTxns = await db.Transactions
            .Where(t => t.SourceId == sourceId)
            .Where(t => t.AccountId.HasValue && accountIds.Contains(t.AccountId.Value))
            .WithinUtc(ytdRange)
            .Select(t => new { AccountId = t.AccountId!.Value, t.CategoryId, t.Amount, t.Timestamp })
            .ToListAsync();

        foreach (var t in yearTxns)
        {
            bool include = exclusiveIds.Contains(t.AccountId)
                || (sharedIds.Contains(t.AccountId)
                    && t.CategoryId.HasValue
                    && projectLeafCategoryIds.Contains(t.CategoryId.Value));
            if (!include) continue;

            var date = DateTimeOffset.FromUnixTimeSeconds(t.Timestamp).UtcDateTime;
            int dayIndex = date.DayOfYear - 1;
            if (dayIndex < 0 || dayIndex >= daysInYear) continue;
            result.DailyDelta[dayIndex] += t.Amount / 100m;
        }

        var today = DateTime.UtcNow.Date;
        if (year < today.Year) result.LastActualDayOfYear = daysInYear;
        else if (year > today.Year) result.LastActualDayOfYear = 0;
        else result.LastActualDayOfYear = today.DayOfYear;

        return result;
    }

    /// <summary>
    /// Returns a normalized day-of-month spending distribution per item, learned from real
    /// transactions. Used by the balance chart's projection so projected months show real-looking
    /// patterns (payday lumps, mid-month groceries, end-of-month rent) instead of a flat smear.
    ///
    /// Fallback ladder per item: trailing 12 months (≥3 transactions) → all-time (≥3) → a single
    /// lump on day 1. Weights normalize to sum=1 over days 1..31; callers must snap any weight
    /// past the target month's last day onto the last day (Feb gets the day-29/30/31 weight).
    /// </summary>
    public async Task<Dictionary<(long Id, CategoryItemType ItemType), DayOfMonthDistribution>>
        GetCategoryDayOfMonthWeightsAsync(
            IReadOnlyCollection<(long Id, CategoryItemType ItemType)> items,
            int sourceId)
    {
        const int MinSampleSize = 3;

        var result = new Dictionary<(long, CategoryItemType), DayOfMonthDistribution>();
        if (items.Count == 0) return result;

        var allCategoriesWithCounts = await _hierarchy.GetAllCategoriesWithChildCountsAsync(sourceId);
        var itemToLeafIds = CategoryHierarchyService.BuildItemToLeafCategoryIds(items, allCategoriesWithCounts);

        var allLeafIds = itemToLeafIds.Values.SelectMany(s => s).Distinct().ToList();
        if (allLeafIds.Count == 0)
        {
            foreach (var item in items) result[item] = DayOfMonthDistribution.Day1Fallback();
            return result;
        }

        // Trailing-year window for the primary sample. Half-open per TimeRange convention —
        // a transaction at exactly the upper-bound second is treated as "future" and skipped.
        var nowUtc = DateTime.UtcNow;
        var trailingRange = TimeRange.Between(nowUtc.AddYears(-1), nowUtc);

        // Single transaction scan across all relevant leaves; trailing-year vs all-time are then
        // computed in-memory per item without further round trips.
        await using var db = await _dbFactory.CreateDbContextAsync();
        var allTxns = await db.Transactions
            .Where(t => t.SourceId == sourceId)
            .Where(t => t.CategoryId.HasValue && allLeafIds.Contains(t.CategoryId.Value))
            .Select(t => new { CategoryId = t.CategoryId!.Value, t.Amount, t.Timestamp })
            .ToListAsync();

        var byLeaf = allTxns
            .GroupBy(t => t.CategoryId)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var item in items)
        {
            var leafs = itemToLeafIds[item];
            var sample = new List<(decimal AbsAmount, int DayOfMonth)>();
            int trailingCount = 0;
            foreach (var leaf in leafs)
            {
                if (!byLeaf.TryGetValue(leaf, out var list)) continue;
                foreach (var t in list)
                {
                    if (t.Timestamp < trailingRange.StartTs || t.Timestamp >= trailingRange.EndTsExclusive) continue;
                    var day = DateTimeOffset.FromUnixTimeSeconds(t.Timestamp).UtcDateTime.Day;
                    sample.Add((Math.Abs(t.Amount) / 100m, day));
                    trailingCount++;
                }
            }

            if (trailingCount >= MinSampleSize)
            {
                result[item] = DayOfMonthDistribution.FromSample(sample, trailingCount, DistributionPatternSource.TrailingYear);
                continue;
            }

            // Fall back to all-time sample.
            sample.Clear();
            int allTimeCount = 0;
            foreach (var leaf in leafs)
            {
                if (!byLeaf.TryGetValue(leaf, out var list)) continue;
                foreach (var t in list)
                {
                    var day = DateTimeOffset.FromUnixTimeSeconds(t.Timestamp).UtcDateTime.Day;
                    sample.Add((Math.Abs(t.Amount) / 100m, day));
                    allTimeCount++;
                }
            }

            result[item] = allTimeCount >= MinSampleSize
                ? DayOfMonthDistribution.FromSample(sample, allTimeCount, DistributionPatternSource.AllTime)
                : DayOfMonthDistribution.Day1Fallback();
        }

        return result;
    }
}
