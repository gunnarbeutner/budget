using OpenBudget.Services;
using Xunit;
using static OpenBudget.Tests.Hierarchy;

namespace OpenBudget.Tests;

/// <summary>
/// NormalizeBudgetEntries enforces the "one level per subtree" invariant on save: if a Group
/// entry has at least one descendant in the same input set with non-zero months, the Group's
/// own months get zeroed. These tests pin that behavior.
/// </summary>
public class NormalizeBudgetEntriesTests
{
    private static List<CategoryGroupWithChildCount> Sample() => new()
    {
        Group(1),
        Group(2, parent: 1),
        Leaf(11, parent: 2),
        Leaf(13, parent: 1),
    };

    [Fact]
    public void Parent_with_zero_amount_descendants_keeps_its_own()
    {
        var entries = new List<(long Id, CategoryItemType ItemType, decimal[] Months)>
        {
            (1, CategoryItemType.Group, Months(100m)),
            (13, CategoryItemType.Category, Months(0m)),
        };

        var result = BudgetEntryService.NormalizeBudgetEntries(entries, Sample());
        var parent = result.Single(e => e.Id == 1 && e.ItemType == CategoryItemType.Group);

        Assert.All(parent.Months, m => Assert.Equal(100m, m));
    }

    [Fact]
    public void Parent_with_budgeted_descendant_gets_its_own_zeroed()
    {
        var entries = new List<(long Id, CategoryItemType ItemType, decimal[] Months)>
        {
            (1, CategoryItemType.Group, Months(100m)),
            (13, CategoryItemType.Category, Months(50m)),
        };

        var result = BudgetEntryService.NormalizeBudgetEntries(entries, Sample());
        var parent = result.Single(e => e.Id == 1 && e.ItemType == CategoryItemType.Group);
        var child = result.Single(e => e.Id == 13 && e.ItemType == CategoryItemType.Category);

        Assert.All(parent.Months, m => Assert.Equal(0m, m));
        Assert.All(child.Months, m => Assert.Equal(50m, m));
    }

    [Fact]
    public void Multi_level_nesting_zeroes_every_ancestor()
    {
        // Grandparent G1 contains G2 contains C11. A non-zero amount on C11 should zero
        // both G1 and G2.
        var entries = new List<(long Id, CategoryItemType ItemType, decimal[] Months)>
        {
            (1, CategoryItemType.Group, Months(100m)),
            (2, CategoryItemType.Group, Months(80m)),
            (11, CategoryItemType.Category, Months(50m)),
        };

        var result = BudgetEntryService.NormalizeBudgetEntries(entries, Sample());

        Assert.All(result.Single(e => e.Id == 1).Months, m => Assert.Equal(0m, m));
        Assert.All(result.Single(e => e.Id == 2).Months, m => Assert.Equal(0m, m));
        Assert.All(result.Single(e => e.Id == 11).Months, m => Assert.Equal(50m, m));
    }

    [Fact]
    public void Leaf_entries_pass_through_unchanged()
    {
        // Categories (leaves) are never modified — only Groups get zeroed by the invariant.
        var months = Months(50m);
        var entries = new List<(long Id, CategoryItemType ItemType, decimal[] Months)>
        {
            (11, CategoryItemType.Category, months),
        };

        var result = BudgetEntryService.NormalizeBudgetEntries(entries, Sample());

        Assert.Same(months, result.Single().Months);
    }

    [Fact]
    public void Group_with_descendant_NOT_in_set_keeps_its_own()
    {
        // The invariant only fires when both the Group and the budgeted descendant are in
        // the same input set. Otherwise the Group is the only source of truth and must
        // keep its amount.
        var entries = new List<(long Id, CategoryItemType ItemType, decimal[] Months)>
        {
            (1, CategoryItemType.Group, Months(100m)),
            // C13 (under G1) intentionally absent — even though it might exist with an
            // amount in the database, this save submits only G1.
        };

        var result = BudgetEntryService.NormalizeBudgetEntries(entries, Sample());

        Assert.All(result.Single().Months, m => Assert.Equal(100m, m));
    }
}
