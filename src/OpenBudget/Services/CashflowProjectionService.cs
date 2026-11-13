using OpenBudget.Connectors;
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
            .Select(p => new { p.Id, p.OwnerUserId })
            .FirstOrDefaultAsync();
        if (project == null) return new ProjectDailyDeltaResult { DailyDelta = Array.Empty<decimal>() };
        var ownerId = project.OwnerUserId;

        // Only accounts marked "In cashflow" move the projected cash trajectory. The filter
        // cascades to accountIds / currentBalance / the YTD + per-day transaction sums below.
        var assignments = await db.ProjectAccountAssignments
            .Where(paa => paa.ProjectId == projectId && paa.InCashflow)
            .ToListAsync();

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
            .Where(a => a.OwnerUserId == ownerId && accountIds.Contains(a.Id))
            .ToListAsync();
        decimal currentBalance = accounts.Sum(a => a.CurrentBalance ?? 0m);

        // Resolve project's leaf category IDs once — only needed when at least one account is Shared.
        HashSet<long> projectLeafCategoryIds = new();
        if (sharedIds.Count > 0)
        {
            var trackingItems = await _projects.GetProjectTrackingItemsAsync(projectId);
            var items = trackingItems.ToTrackedKeys().ToList();
            var allCategoriesWithCounts = await _hierarchy.GetAllCategoriesWithChildCountsAsync(ownerId);
            var itemToLeaf = CategoryHierarchyService.BuildItemToLeafCategoryIds(items, allCategoriesWithCounts);
            projectLeafCategoryIds = itemToLeaf.Values.SelectMany(s => s).ToHashSet();
        }

        var yearRange = TimeRange.Year(year);
        var nowTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Anchor the line on Jan 1 by reversing all booked transactions through "now" out of the
        // live balance. Both this sum and the per-day delta walk below MUST use the same upper
        // bound — the source app stores future-dated entries (scheduled payments) in the same table,
        // and including them in one but not the other lets currentBalance fall out of sync with
        // sum(DailyDelta), making balanceAtProjStart drift away from the real account balance.
        var clampedEnd = Math.Max(yearRange.StartTs, Math.Min(nowTs, yearRange.EndTsExclusive));
        var ytdRange = new TimeRange(yearRange.StartTs, clampedEnd);

        decimal ytdSumAllScopes = 0m;
        if (clampedEnd > yearRange.StartTs)
        {
            ytdSumAllScopes = (await db.Transactions
                .Where(t => t.OwnerUserId == ownerId)
                .Where(t => t.Splitted != SplitState.Parent)
                .Where(t => t.AccountId.HasValue && accountIds.Contains(t.AccountId.Value))
                .WithinUtc(ytdRange)
                .SumAsync(t => t.Amount)) / 100m;
        }
        // selectedYear in the future: clampedEnd == yearRange.StartTs, ytdSumAllScopes stays 0,
        // and StartingBalance defaults to currentBalance — a known approximation flagged in the UI.
        result.StartingBalance = currentBalance - ytdSumAllScopes;

        var yearTxns = await db.Transactions
            .Where(t => t.OwnerUserId == ownerId)
            .Where(t => t.Splitted != SplitState.Parent)
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
    /// The bank's standing orders and scheduled transfers in the projection: each execution after
    /// <paramref name="today"/> through the end of <paramref name="year"/> on the project's "In cashflow"
    /// accounts, and per cashflow row the part of its category projection those orders already cover.
    ///
    /// Avoiding double counting: the category projection (budget or history) already expects the rent that a
    /// standing order pays. So every standing order is given the category of the most recent booked transaction
    /// it produced — same counterparty IBAN and same amount, else same counterparty name and amount, else the
    /// same IBAN with any amount (the order's amount changed) — and the order's executions per month are
    /// recorded against the cashflow row whose categories contain it. <see cref="CashflowCalculator.ComputeBalance"/>
    /// takes that amount out of the row's projected month (never below zero) and books the order on its own
    /// execution days instead. An order whose category cannot be found is only added. Single scheduled transfers
    /// are one-offs that the category projection does not foresee, so they are only added.
    ///
    /// On a Shared account only orders whose category is one of the project's count, the same rule the
    /// account's bookings follow; on an Exclusive account all of them.
    /// </summary>
    public async Task<ScheduledCashflow> GetScheduledCashflowAsync(
        int projectId, int year, DateOnly today, IReadOnlyCollection<(long Id, CategoryItemType ItemType)> rowKeys)
    {
        var result = new ScheduledCashflow();
        var from = today.AddDays(1);
        var to = new DateOnly(year, 12, 31);
        if (from > to) return result;
        if (from.Year < year) from = new DateOnly(year, 1, 1);

        await using var db = await _dbFactory.CreateDbContextAsync();
        var project = await db.Projects.Where(p => p.Id == projectId).Select(p => new { p.OwnerUserId }).FirstOrDefaultAsync();
        if (project == null) return result;
        var assignments = await db.ProjectAccountAssignments
            .Where(a => a.ProjectId == projectId && a.InCashflow)
            .Select(a => new { a.AccountId, a.Scope })
            .ToListAsync();
        if (assignments.Count == 0) return result;
        var accountIds = assignments.Select(a => a.AccountId).ToList();
        var orders = await db.ScheduledPayments.AsNoTracking()
            .Where(p => p.OwnerUserId == project.OwnerUserId && accountIds.Contains(p.AccountId))
            .ToListAsync();
        if (orders.Count == 0) return result;

        var hierarchy = await _hierarchy.GetAllCategoriesWithChildCountsAsync(project.OwnerUserId);
        var rowByLeaf = new Dictionary<long, (long, CategoryItemType)>();
        foreach (var (row, leaves) in CategoryHierarchyService.BuildItemToLeafCategoryIds(rowKeys, hierarchy))
            foreach (var leaf in leaves) rowByLeaf.TryAdd(leaf, row);

        HashSet<long>? projectLeaves = null;
        if (assignments.Any(a => a.Scope == AccountScope.Shared))
        {
            var tracked = (await _projects.GetProjectTrackingItemsAsync(projectId)).ToTrackedKeys().ToList();
            projectLeaves = CategoryHierarchyService.BuildItemToLeafCategoryIds(tracked, hierarchy).Values.SelectMany(s => s).ToHashSet();
        }

        var categories = await InferCategoriesAsync(db, project.OwnerUserId, orders);
        var scopeByAccount = assignments.ToDictionary(a => a.AccountId, a => a.Scope);
        foreach (var order in orders)
        {
            categories.TryGetValue(order.Id, out var category);
            if (scopeByAccount[order.AccountId] == AccountScope.Shared && (category == null || projectLeaves?.Contains(category.Value) != true))
                continue;
            var covered = order.Kind == ScheduledPaymentKind.Standing && category is { } c && rowByLeaf.TryGetValue(c, out var row) ? row : ((long, CategoryItemType)?)null;
            foreach (var date in order.Occurrences(from, to))
            {
                var amount = order.Amount / 100m;
                result.Occurrences.Add(new ScheduledOccurrence(date, amount, order.CounterpartyName ?? order.Purpose ?? string.Empty, order.Kind));
                if (covered is { } key)
                {
                    if (!result.CoveredByRow.TryGetValue(key, out var months)) result.CoveredByRow[key] = months = new decimal[12];
                    months[date.Month - 1] += amount;
                }
            }
        }
        result.Occurrences.Sort((a, b) => a.Date.CompareTo(b.Date));
        return result;
    }

    /// <summary>Per order id: the category of the most recent booked transaction the order produced (see <see cref="GetScheduledCashflowAsync"/>).</summary>
    private static async Task<Dictionary<long, long?>> InferCategoriesAsync(BudgetDbContext db, string ownerId, List<ScheduledPayment> orders)
    {
        var standing = orders.Where(o => o.Kind == ScheduledPaymentKind.Standing).ToList();
        var result = new Dictionary<long, long?>();
        if (standing.Count == 0) return result;
        var ibans = standing.Where(o => o.CounterpartyIban != null)
            .SelectMany(o => new[] { o.CounterpartyIban!, o.CounterpartyIban!.ToUpperInvariant(), AccountDisplay.FormatIban(o.CounterpartyIban)! })
            .Distinct(StringComparer.Ordinal).ToList();
        var amounts = standing.Select(o => o.Amount).Distinct().ToList();
        var candidates = await db.Transactions.AsNoTracking()
            .Where(t => t.OwnerUserId == ownerId && t.Booked && t.CategoryId != null && t.Splitted != SplitState.Parent)
            .Where(t => (t.CounterpartyIban != null && ibans.Contains(t.CounterpartyIban)) || amounts.Contains(t.Amount))
            .OrderByDescending(t => t.Timestamp)
            .Select(t => new { t.CounterpartyIban, t.Name, t.Amount, t.CategoryId })
            .ToListAsync();
        foreach (var order in standing)
        {
            var iban = order.CounterpartyIban;
            bool SameIban(string? other) => iban != null && other != null && string.Equals(iban, other.Replace(" ", string.Empty, StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase);
            var match = candidates.FirstOrDefault(t => t.Amount == order.Amount && SameIban(t.CounterpartyIban))
                ?? candidates.FirstOrDefault(t => t.Amount == order.Amount && order.CounterpartyName != null
                    && string.Equals(t.Name?.Trim(), order.CounterpartyName.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? candidates.FirstOrDefault(t => SameIban(t.CounterpartyIban));
            result[order.Id] = match?.CategoryId;
        }
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
            string ownerId)
    {
        const int MinSampleSize = 3;

        var result = new Dictionary<(long, CategoryItemType), DayOfMonthDistribution>();
        if (items.Count == 0) return result;

        var allCategoriesWithCounts = await _hierarchy.GetAllCategoriesWithChildCountsAsync(ownerId);
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
            .Where(t => t.OwnerUserId == ownerId)
            .CountedAsFlow()
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
