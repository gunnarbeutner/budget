using OpenBudget.Models;
using OpenBudget.Services;
using Xunit;
using static OpenBudget.Tests.Hierarchy;

namespace OpenBudget.Tests;

/// <summary>
/// Characterization tests for the Budgets trend/variance assembly extracted from Budgets.razor:
/// budgeted-descendant resolution, imported sort keying, and variance-row building.
/// </summary>
public class BudgetTrendMathTests
{
    [Fact]
    public void ResolveBudgetedDescendants_keeps_only_budget_carrying_descendants()
    {
        var hierarchy = new List<CategoryGroupWithChildCount>
        {
            Group(100),
            Leaf(101, parent: 100),
            Leaf(102, parent: 100),
            Group(200, parent: 100),
            Leaf(201, parent: 200),
        };
        var budgetKeys = new HashSet<(long, CategoryItemType)> { CatKey(101), GroupKey(200) };

        var result = BudgetTrendMath.ResolveBudgetedDescendants(budgetKeys, new long[] { 100 }, hierarchy);

        Assert.True(result.ContainsKey(GroupKey(100)));
        var budgeted = result[GroupKey(100)];
        Assert.Equal(2, budgeted.Count);
        Assert.Contains(CatKey(101), budgeted);
        Assert.Contains(GroupKey(200), budgeted);
        Assert.DoesNotContain(CatKey(102), budgeted); // no budget entry
    }

    [Fact]
    public void ResolveBudgetedDescendants_omits_groups_without_budgeted_descendants()
    {
        var hierarchy = new List<CategoryGroupWithChildCount> { Group(100), Leaf(101, parent: 100) };
        var budgetKeys = new HashSet<(long, CategoryItemType)>(); // nothing budgeted

        var result = BudgetTrendMath.ResolveBudgetedDescendants(budgetKeys, new long[] { 100 }, hierarchy);

        Assert.Empty(result);
    }

    [Fact]
    public void ComputeSortKey_orders_by_ancestor_sort_then_own_sort()
    {
        var groups = new List<CategoryGroupWithChildCount>
        {
            new() { CategoryGroup = new CategoryGroup { Id = 10, Sort = 2 }, ItemType = CategoryItemType.Group },
            new() { CategoryGroup = new CategoryGroup { Id = 20, Sort = 1 }, ItemType = CategoryItemType.Group },
        };
        var cats = new List<Category>
        {
            new() { Id = 101, Sort = 5, GroupId = 10 }, // under group 10 (sort 2)
            new() { Id = 201, Sort = 5, GroupId = 20 }, // under group 20 (sort 1)
        };

        // Group 20 (sort 1) sorts before group 10 (sort 2).
        Assert.True(string.CompareOrdinal(
            BudgetTrendMath.ComputeSortKey(GroupKey(20), cats, groups),
            BudgetTrendMath.ComputeSortKey(GroupKey(10), cats, groups)) < 0);

        // A leaf inherits its ancestor's sort, so leaf under group 20 sorts before leaf under group 10.
        Assert.True(string.CompareOrdinal(
            BudgetTrendMath.ComputeSortKey(CatKey(201), cats, groups),
            BudgetTrendMath.ComputeSortKey(CatKey(101), cats, groups)) < 0);
    }

    [Fact]
    public void BuildVarianceRows_computes_totals_and_flags_aggregate_rows()
    {
        var batchKeys = new List<(long, CategoryItemType)> { GroupKey(100), CatKey(101), CatKey(102) };
        var names = new Dictionary<(long, CategoryItemType), string>
        {
            [GroupKey(100)] = "GroupA",
            [CatKey(101)] = "LeafX",
            [CatKey(102)] = "Empty", // no budget, no actuals → dropped
        };
        var effective = new BudgetEffectiveAmounts();
        effective.Effective[GroupKey(100)] = Jan(40m);
        effective.Effective[CatKey(101)] = Jan(25m);
        var actuals = new Dictionary<string, List<MonthlySpending>>
(StringComparer.Ordinal)
        {
            ["GroupA"] = new() { new MonthlySpending { Year = 2026, Month = 1, Amount = 50m } },
            ["LeafX"] = new() { new MonthlySpending { Year = 2026, Month = 1, Amount = 30m } },
        };
        var aggregateRowNames = new HashSet<string>(StringComparer.Ordinal) { "GroupA" };

        var (budgetTrends, variance) = BudgetTrendMath.BuildVarianceRows(
            batchKeys, names, effective, actuals, aggregateRowNames, budgetYear: 2026);

        Assert.Equal(2, variance.Count); // "Empty" dropped

        var group = variance.Single(v => v.CategoryName == "GroupA");
        Assert.Equal(40m, group.BudgetAmount);
        Assert.Equal(50m, group.ActualAmount);
        Assert.Equal(10m, group.Variance);
        Assert.True(group.IsAggregateRow);
        Assert.True(group.HasBudget);

        var leaf = variance.Single(v => v.CategoryName == "LeafX");
        Assert.Equal(25m, leaf.BudgetAmount);
        Assert.Equal(30m, leaf.ActualAmount);
        Assert.False(leaf.IsAggregateRow);

        Assert.True(budgetTrends.ContainsKey("GroupA"));
        Assert.True(budgetTrends.ContainsKey("LeafX"));
        Assert.False(budgetTrends.ContainsKey("Empty"));
    }

    [Fact]
    public void BuildVarianceRows_keeps_a_row_with_actuals_but_no_budget()
    {
        var batchKeys = new List<(long, CategoryItemType)> { CatKey(101) };
        var names = new Dictionary<(long, CategoryItemType), string> { [CatKey(101)] = "Shadow" };
        var effective = new BudgetEffectiveAmounts(); // no entry for 101 → HasBudget false
        var actuals = new Dictionary<string, List<MonthlySpending>>
(StringComparer.Ordinal)
        {
            ["Shadow"] = new() { new MonthlySpending { Year = 2026, Month = 1, Amount = 30m } },
        };

        var (budgetTrends, variance) = BudgetTrendMath.BuildVarianceRows(
            batchKeys, names, effective, actuals, new HashSet<string>(StringComparer.Ordinal), budgetYear: 2026);

        var row = Assert.Single(variance);
        Assert.False(row.HasBudget);
        Assert.Equal(0m, row.BudgetAmount);
        Assert.Equal(30m, row.ActualAmount);
        Assert.Equal(30m, row.Variance);
        Assert.False(budgetTrends.ContainsKey("Shadow")); // no budget series for a shadow row
    }

    // decimal[12] with only January set.
    private static decimal[] Jan(decimal amount)
    {
        var a = new decimal[12];
        a[0] = amount;
        return a;
    }
}
