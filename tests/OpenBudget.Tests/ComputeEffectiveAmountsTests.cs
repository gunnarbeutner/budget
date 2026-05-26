using OpenBudget.Models;
using OpenBudget.Services;
using Xunit;
using static OpenBudget.Tests.Hierarchy;

namespace OpenBudget.Tests;

/// <summary>
/// Pins the parent-vs-descendant rollup semantics. These tests would have caught both the
/// 70k double-count (when shadowedParents wasn't filtering on the dashboard) and the 34.5k
/// under-count (when TotalBudget was summing effective rather than stored).
/// </summary>
public class ComputeEffectiveAmountsTests
{
    private static List<CategoryGroupWithChildCount> Sample() => new()
    {
        Group(1),
        Group(2, parent: 1),
        Leaf(11, parent: 2),
        Leaf(12, parent: 2),
        Leaf(13, parent: 1),
    };

    [Fact]
    public void Parent_with_no_descendant_amounts_keeps_its_own()
    {
        var stored = new Dictionary<(long Id, CategoryItemType ItemType), decimal[]>
        {
            [GroupKey(1)] = Months(100m),
        };

        var result = BudgetEntryService.ComputeEffectiveAmounts(stored, Sample());

        Assert.Equal(100m, result.Effective[GroupKey(1)][0]);
        Assert.DoesNotContain(GroupKey(1), result.AutoSummed);
    }

    [Fact]
    public void Parent_with_descendant_amounts_is_auto_summed()
    {
        var stored = new Dictionary<(long Id, CategoryItemType ItemType), decimal[]>
        {
            [GroupKey(1)] = Months(0m),
            [CatKey(13)] = Months(50m),
        };

        var result = BudgetEntryService.ComputeEffectiveAmounts(stored, Sample());

        Assert.Equal(50m, result.Effective[GroupKey(1)][0]);
        Assert.Contains(GroupKey(1), result.AutoSummed);
    }

    [Fact]
    public void Parent_own_stored_is_overwritten_by_descendant_sum_when_descendants_have_amounts()
    {
        // This is the documented behavior that caused both the 70k and 34.5k bugs on the
        // dashboard. Pinning it so future code changes notice when this assumption shifts.
        var stored = new Dictionary<(long Id, CategoryItemType ItemType), decimal[]>
        {
            [GroupKey(1)] = Months(10m), // parent's own
            [CatKey(13)] = Months(50m),  // tracked descendant
        };

        var result = BudgetEntryService.ComputeEffectiveAmounts(stored, Sample());

        // Effective for the parent is the descendant sum (50), not parent + descendant (60).
        Assert.Equal(50m, result.Effective[GroupKey(1)][0]);
        Assert.Contains(GroupKey(1), result.AutoSummed);
        // Leaves keep their own.
        Assert.Equal(50m, result.Effective[CatKey(13)][0]);
    }

    [Fact]
    public void Multi_level_auto_sum_flows_through_intermediates()
    {
        // G1 contains G2 contains C11. Only C11 has an amount. Both G1 and G2 should be
        // auto-summed to that amount.
        var stored = new Dictionary<(long Id, CategoryItemType ItemType), decimal[]>
        {
            [GroupKey(1)] = Months(0m),
            [GroupKey(2)] = Months(0m),
            [CatKey(11)] = Months(25m),
        };

        var result = BudgetEntryService.ComputeEffectiveAmounts(stored, Sample());

        Assert.Equal(25m, result.Effective[GroupKey(1)][0]);
        Assert.Equal(25m, result.Effective[GroupKey(2)][0]);
        Assert.Contains(GroupKey(1), result.AutoSummed);
        Assert.Contains(GroupKey(2), result.AutoSummed);
    }

    [Fact]
    public void Sibling_leaf_amounts_sum_into_the_parent()
    {
        var stored = new Dictionary<(long Id, CategoryItemType ItemType), decimal[]>
        {
            [GroupKey(2)] = Months(0m),
            [CatKey(11)] = Months(10m),
            [CatKey(12)] = Months(15m),
        };

        var result = BudgetEntryService.ComputeEffectiveAmounts(stored, Sample());

        Assert.Equal(25m, result.Effective[GroupKey(2)][0]);
    }
}
