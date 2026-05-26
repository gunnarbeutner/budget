using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>
/// Forecasting and monthly-history reads — feeds the budget wizard's auto-prediction and the
/// Budgets-page trend chart. Pure history: doesn't write anything, doesn't know about
/// projects or tracked items, just turns (category, year) into per-month sums.
/// </summary>
public class BudgetPredictionService
{
    private readonly BudgetDbContext _db;
    private readonly CategoryHierarchyService _hierarchy;

    public BudgetPredictionService(BudgetDbContext db, CategoryHierarchyService hierarchy)
    {
        _db = db;
        _hierarchy = hierarchy;
    }

    /// <summary>Monthly spending (or income) for an individual leaf category over the last N months.</summary>
    public async Task<List<MonthlySpending>> GetMonthlySpendingForCategoryAsync(long categoryId, int sourceId, int months = 12, bool isIncome = false)
    {
        var now = DateTimeOffset.UtcNow;
        var startDate = new DateTimeOffset(new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc)).AddMonths(-(months - 1));

        var results = new List<MonthlySpending>();

        for (int i = 0; i < months; i++)
        {
            var monthStart = startDate.AddMonths(i);
            var range = TimeRange.Between(monthStart, monthStart.AddMonths(1));

            var spentCents = isIncome
                ? await _db.Transactions
                    .Where(t => t.SourceId == sourceId)
                    .Where(t => t.CategoryId == categoryId)
                    .WithinUtc(range)
                    .Where(t => t.Amount > 0)
                    .SumAsync(t => t.Amount)
                : await _db.Transactions
                    .Where(t => t.SourceId == sourceId)
                    .Where(t => t.CategoryId == categoryId)
                    .WithinUtc(range)
                    .Where(t => t.Amount < 0)
                    .SumAsync(t => -t.Amount);

            results.Add(new MonthlySpending
            {
                Year = monthStart.Year,
                Month = monthStart.Month,
                Amount = spentCents / 100m
            });
        }

        return results;
    }

    /// <summary>
    /// Monthly spending (or income) for a category group over the last N months, summing over
    /// every descendant leaf category.
    /// </summary>
    public async Task<List<MonthlySpending>> GetMonthlySpendingAsync(long categoryGroupId, int sourceId, int months = 12, bool isIncome = false)
    {
        var categoryIds = await _hierarchy.GetAllCategoryIdsForGroupAsync(categoryGroupId, sourceId);

        if (!categoryIds.Any())
            return new List<MonthlySpending>();

        var now = DateTimeOffset.UtcNow;
        var startDate = new DateTimeOffset(new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc)).AddMonths(-(months - 1));

        var results = new List<MonthlySpending>();

        for (int i = 0; i < months; i++)
        {
            var monthStart = startDate.AddMonths(i);
            var range = TimeRange.Between(monthStart, monthStart.AddMonths(1));

            var spentCents = isIncome
                ? await _db.Transactions
                    .Where(t => t.SourceId == sourceId)
                    .Where(t => t.CategoryId.HasValue && categoryIds.Contains(t.CategoryId.Value))
                    .WithinUtc(range)
                    .Where(t => t.Amount > 0)
                    .SumAsync(t => t.Amount)
                : await _db.Transactions
                    .Where(t => t.SourceId == sourceId)
                    .Where(t => t.CategoryId.HasValue && categoryIds.Contains(t.CategoryId.Value))
                    .WithinUtc(range)
                    .Where(t => t.Amount < 0)
                    .SumAsync(t => -t.Amount);

            results.Add(new MonthlySpending
            {
                Year = monthStart.Year,
                Month = monthStart.Month,
                Amount = spentCents / 100m
            });
        }

        return results;
    }

    /// <summary>
    /// Batched monthly outflow (negative transactions, negated to positive — or positive for
    /// income) for a calendar year. Items with no leaf categories get an empty list.
    /// </summary>
    public async Task<Dictionary<(long Id, CategoryItemType ItemType), List<MonthlySpending>>>
        GetMonthlySpendingBatchAsync(
            IReadOnlyCollection<(long Id, CategoryItemType ItemType)> items,
            int year,
            int sourceId,
            bool isIncome = false)
    {
        const int months = 12;
        var result = new Dictionary<(long, CategoryItemType), List<MonthlySpending>>();
        foreach (var item in items)
            result[item] = new List<MonthlySpending>();

        if (!items.Any()) return result;

        var startDate = new DateTimeOffset(new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var allCategoriesWithCounts = await _hierarchy.GetAllCategoriesWithChildCountsAsync(sourceId);
        var itemToLeafIds = CategoryHierarchyService.BuildItemToLeafCategoryIds(items, allCategoriesWithCounts);

        foreach (var item in items)
        {
            if (!itemToLeafIds[item].Any()) continue;
            var list = result[item];
            for (int m = 0; m < months; m++)
            {
                var d = startDate.AddMonths(m);
                list.Add(new MonthlySpending { Year = d.Year, Month = d.Month, Amount = 0 });
            }
        }

        var allLeafIds = itemToLeafIds.Values.SelectMany(s => s).Distinct().ToList();
        if (!allLeafIds.Any()) return result;

        var rawQuery = _db.Transactions
            .Where(t => t.SourceId == sourceId)
            .Where(t => t.CategoryId.HasValue && allLeafIds.Contains(t.CategoryId.Value))
            .WithinUtc(TimeRange.Between(startDate, startDate.AddMonths(months)));
        rawQuery = isIncome
            ? rawQuery.Where(t => t.Amount > 0)
            : rawQuery.Where(t => t.Amount < 0);
        var raw = await rawQuery
            .Select(t => new { CategoryId = t.CategoryId!.Value, t.Amount, t.Timestamp })
            .ToListAsync();

        var perCategoryMonthly = new Dictionary<long, decimal[]>();
        foreach (var t in raw)
        {
            var ts = DateTimeOffset.FromUnixTimeSeconds(t.Timestamp).UtcDateTime;
            var monthIndex = ((ts.Year - startDate.Year) * 12) + (ts.Month - startDate.Month);
            if (monthIndex < 0 || monthIndex >= months) continue;
            if (!perCategoryMonthly.TryGetValue(t.CategoryId, out var arr))
            {
                arr = new decimal[months];
                perCategoryMonthly[t.CategoryId] = arr;
            }
            arr[monthIndex] += (isIncome ? t.Amount : -t.Amount) / 100m;
        }

        foreach (var item in items)
        {
            var list = result[item];
            if (!list.Any()) continue;
            foreach (var leafId in itemToLeafIds[item])
            {
                if (!perCategoryMonthly.TryGetValue(leafId, out var monthly)) continue;
                for (int m = 0; m < months; m++)
                    list[m].Amount += monthly[m];
            }
        }

        return result;
    }

    /// <summary>
    /// Reads the 12 actual monthly spending amounts for a single tracked item in a specific
    /// calendar year. Used as the prediction for already-elapsed target years where seasonal
    /// scaling against today's level would be nonsensical.
    /// </summary>
    private async Task<decimal[]> GetYearActualsAsync((long Id, CategoryItemType ItemType) key, int year, int sourceId, bool isIncome = false)
    {
        var result = new decimal[12];
        var batch = await GetMonthlySpendingBatchAsync(new[] { key }, year, sourceId, isIncome);
        if (batch.TryGetValue(key, out var trend))
        {
            for (int i = 0; i < 12 && i < trend.Count; i++)
                result[i] = trend[i].Amount;
        }
        return result;
    }

    /// <summary>
    /// Predict monthly spending for a leaf (MoneyMoney Category) using seasonal adjustment —
    /// same algorithm as <see cref="PredictMonthlySpendingForCategoryAsync"/> but driven by
    /// per-leaf history instead of summing a group's descendants.
    /// </summary>
    public async Task<decimal[]> PredictMonthlySpendingForLeafCategoryAsync(long categoryId, int targetYear, int sourceId, bool isIncome = false)
    {
        var predictions = new decimal[12];

        // For a completely elapsed year, just use that year's own actuals — no scaling needed.
        if (targetYear < DateTime.UtcNow.Year)
            return await GetYearActualsAsync((categoryId, CategoryItemType.Category), targetYear, sourceId, isIncome);

        var monthlyData = await GetMonthlySpendingForCategoryAsync(categoryId, sourceId, 24, isIncome);

        if (!monthlyData.Any())
            return predictions;

        var previousYearData = monthlyData
            .Where(m => m.Year == targetYear - 1)
            .OrderBy(m => m.Month)
            .ToList();

        var now = DateTime.UtcNow;
        var recentData = monthlyData
            .Where(m => m.Year < now.Year || (m.Year == now.Year && m.Month < now.Month))
            .OrderByDescending(m => m.Year)
            .ThenByDescending(m => m.Month)
            .Take(12)
            .ToList();

        if (!recentData.Any())
        {
            var avg = monthlyData.Average(m => m.Amount);
            for (int i = 0; i < 12; i++) predictions[i] = avg;
            return predictions;
        }

        var recentTotal = recentData.Sum(m => m.Amount);

        if (previousYearData.Count == 12)
        {
            var previousYearTotal = previousYearData.Sum(m => m.Amount);
            if (previousYearTotal > 0)
            {
                for (int month = 1; month <= 12; month++)
                {
                    var previousMonth = previousYearData.FirstOrDefault(m => m.Month == month);
                    predictions[month - 1] = previousMonth != null
                        ? recentTotal * (previousMonth.Amount / previousYearTotal)
                        : recentTotal / 12;
                }
                return predictions;
            }
        }

        var monthlyAvg = recentTotal / 12;
        for (int i = 0; i < 12; i++) predictions[i] = monthlyAvg;
        return predictions;
    }

    /// <summary>
    /// Predict monthly spending for a category group using seasonal adjustment: applies the
    /// previous calendar year's monthly distribution to the most-recent-12-months total. Falls
    /// back to a flat distribution when the previous year is incomplete or zero.
    /// </summary>
    public async Task<decimal[]> PredictMonthlySpendingForCategoryAsync(long categoryGroupId, int targetYear, int sourceId, bool isIncome = false)
    {
        var predictions = new decimal[12];

        if (targetYear < DateTime.UtcNow.Year)
            return await GetYearActualsAsync((categoryGroupId, CategoryItemType.Group), targetYear, sourceId, isIncome);

        var monthlyData = await GetMonthlySpendingAsync(categoryGroupId, sourceId, 24, isIncome);

        if (!monthlyData.Any())
            return predictions;

        var previousYearData = monthlyData
            .Where(m => m.Year == targetYear - 1)
            .OrderBy(m => m.Month)
            .ToList();

        var now = DateTime.UtcNow;
        var recentData = monthlyData
            .Where(m => m.Year < now.Year || (m.Year == now.Year && m.Month < now.Month))
            .OrderByDescending(m => m.Year)
            .ThenByDescending(m => m.Month)
            .Take(12)
            .ToList();

        if (!recentData.Any())
        {
            var avgAmount = monthlyData.Average(m => m.Amount);
            for (int i = 0; i < 12; i++)
                predictions[i] = avgAmount;
            return predictions;
        }

        var recentTotal = recentData.Sum(m => m.Amount);

        if (previousYearData.Any() && previousYearData.Count == 12)
        {
            var previousYearTotal = previousYearData.Sum(m => m.Amount);

            if (previousYearTotal > 0)
            {
                for (int month = 1; month <= 12; month++)
                {
                    var previousYearMonth = previousYearData.FirstOrDefault(m => m.Month == month);
                    if (previousYearMonth != null)
                    {
                        var distribution = previousYearMonth.Amount / previousYearTotal;
                        predictions[month - 1] = recentTotal * distribution;
                    }
                    else
                    {
                        predictions[month - 1] = recentTotal / 12;
                    }
                }
            }
            else
            {
                var monthlyAvg = recentTotal / 12;
                for (int i = 0; i < 12; i++)
                    predictions[i] = monthlyAvg;
            }
        }
        else
        {
            var monthlyAvg = recentTotal / 12;
            for (int i = 0; i < 12; i++)
                predictions[i] = monthlyAvg;
        }

        return predictions;
    }
}
