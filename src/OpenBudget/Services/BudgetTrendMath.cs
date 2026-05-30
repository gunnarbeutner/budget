using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>
/// Pure assembly helpers for the Budgets page's trend/variance view, lifted out of
/// Budgets.razor's LoadCategoryTrends so the budgeted-descendant resolution, MoneyMoney sort
/// keying, and variance-row building can be unit-tested. The DB loads and the
/// <see cref="BudgetEntryService.ComputeEffectiveAmounts"/> call stay in the page.
/// </summary>
public static class BudgetTrendMath
{
    /// <summary>
    /// For each tracked group, the descendants (leaves or sub-groups) that carry their own budget
    /// entry. Groups with no budgeted descendant are omitted. Used both to auto-expand those
    /// groups and to decide which rows are budget "carriers".
    /// </summary>
    public static Dictionary<(long Id, CategoryItemType ItemType), List<(long Id, CategoryItemType ItemType)>>
        ResolveBudgetedDescendants(
            IReadOnlySet<(long Id, CategoryItemType ItemType)> budgetKeys,
            IEnumerable<long> selectedGroupIds,
            List<CategoryGroupWithChildCount> allCategories)
    {
        var result = new Dictionary<(long, CategoryItemType), List<(long, CategoryItemType)>>();
        foreach (var groupId in selectedGroupIds)
        {
            var groupKey = (groupId, CategoryItemType.Group);
            var descendants = CategoryGroupWithChildCount.CollectDescendants(
                groupId, allCategories, includeLeafCategories: true);
            var budgeted = descendants
                .Select(d => (d.Id, d.ItemType))
                .Where(k => budgetKeys.Contains(k))
                .ToList();
            if (budgeted.Count > 0)
                result[groupKey] = budgeted;
        }
        return result;
    }

    /// <summary>
    /// Build a sort key reflecting an item's position in MoneyMoney's tree (ancestor sort values,
    /// then own sort, then id for stability). Lexicographic compare on the resulting string yields
    /// the same order MoneyMoney itself displays.
    /// </summary>
    public static string ComputeSortKey(
        (long Id, CategoryItemType ItemType) key,
        IReadOnlyList<Category>? allActualCategories,
        IReadOnlyList<CategoryGroupWithChildCount>? allCategories)
    {
        var parts = new List<string>();
        long? walkUp;

        if (key.ItemType == CategoryItemType.Category)
        {
            var cat = allActualCategories?.FirstOrDefault(c => c.Id == key.Id);
            parts.Insert(0, (cat?.Sort ?? long.MaxValue).ToString("D19", System.Globalization.CultureInfo.InvariantCulture));
            walkUp = cat?.GroupId;
        }
        else
        {
            var groupItem = allCategories?.FirstOrDefault(c =>
                c.ItemType == CategoryItemType.Group && c.Id == key.Id);
            parts.Insert(0, (groupItem?.CategoryGroup.Sort ?? long.MaxValue).ToString("D19", System.Globalization.CultureInfo.InvariantCulture));
            walkUp = groupItem?.ParentCategoryGroupId;
        }

        while (walkUp.HasValue)
        {
            var parent = allCategories?.FirstOrDefault(c =>
                c.ItemType == CategoryItemType.Group && c.Id == walkUp.Value);
            if (parent == null) break;
            parts.Insert(0, (parent.CategoryGroup.Sort ?? long.MaxValue).ToString("D19", System.Globalization.CultureInfo.InvariantCulture));
            walkUp = parent.ParentCategoryGroupId;
        }

        // Append the item's id as a stable tiebreaker.
        parts.Add(key.Id.ToString("D19", System.Globalization.CultureInfo.InvariantCulture));
        return string.Join(".", parts);
    }

    /// <summary>
    /// Build the per-category budget trend series + the variance summary rows from already-loaded
    /// data: the effective budget amounts, the actual trends (keyed by display name), and which
    /// display names are aggregate (charted) rows. Rows with neither a budget allocation nor any
    /// actuals are dropped. Returns budget trends keyed by display name and one VarianceSummary
    /// per surviving batch key.
    /// </summary>
    public static (Dictionary<string, List<MonthlySpending>> BudgetTrends, List<VarianceSummary> Variance)
        BuildVarianceRows(
            IReadOnlyList<(long Id, CategoryItemType ItemType)> batchKeys,
            IReadOnlyDictionary<(long Id, CategoryItemType ItemType), string> categoryDisplayNames,
            BudgetEffectiveAmounts budgetEffective,
            IReadOnlyDictionary<string, List<MonthlySpending>> categoryTrends,
            IReadOnlySet<string> aggregateRowNames,
            int budgetYear)
    {
        // Collect all months being displayed across all categories.
        var allDisplayedMonths = categoryTrends.Values
            .SelectMany(trend => trend.Select(m => (m.Year, m.Month)))
            .Distinct()
            .ToHashSet();

        // aggregateRowNames is populated by the trend-loading pass using the same chart tree
        // projection; reconstruct the key set here for filtering.
        var aggregateKeySetForVariance = batchKeys
            .Where(k => categoryDisplayNames.TryGetValue(k, out var n) && aggregateRowNames.Contains(n))
            .ToHashSet();

        var budgetTrends = new Dictionary<string, List<MonthlySpending>>(StringComparer.Ordinal);
        var varianceSummary = new List<VarianceSummary>();

        foreach (var key in batchKeys)
        {
            var categoryName = categoryDisplayNames.GetValueOrDefault(key);
            if (categoryName == null) continue;

            // Shadow sub-cats (expanded under a group) have no MonthlyBudgetEntry so
            // budgetEffective doesn't list them — fall back to zero budget so they still
            // get a row showing their actuals. HasBudget=false drives the "—" rendering.
            var hasBudget = budgetEffective.Effective.TryGetValue(key, out var monthlyArr);
            var stored = monthlyArr ?? new decimal[12];

            var trend = Enumerable.Range(1, 12)
                .Select(m => new MonthlySpending { Year = budgetYear, Month = m, Amount = stored[m - 1] })
                .Where(m => allDisplayedMonths.Contains((m.Year, m.Month)))
                .ToList();
            var actualTrend = categoryTrends.GetValueOrDefault(categoryName);

            // Drop only the truly empty rows: no budget allocation AND no actuals.
            if (!trend.Any(m => m.Amount != 0m) && (actualTrend == null || actualTrend.All(m => m.Amount == 0m)))
                continue;

            if (hasBudget && trend.Any(m => m.Amount != 0m))
                budgetTrends[categoryName] = trend;
            var budgetTotal = trend.Sum(m => m.Amount);
            var actualTotal = actualTrend?.Sum(m => m.Amount) ?? 0m;
            varianceSummary.Add(new VarianceSummary
            {
                CategoryId = key.Id,
                ItemType = key.ItemType,
                CategoryName = categoryName,
                BudgetAmount = budgetTotal,
                ActualAmount = actualTotal,
                Variance = actualTotal - budgetTotal,
                IsAggregateRow = aggregateKeySetForVariance.Contains(key),
                HasBudget = hasBudget,
                IsAutoSummed = budgetEffective.AutoSummed.Contains(key)
            });
        }

        return (budgetTrends, varianceSummary);
    }
}
