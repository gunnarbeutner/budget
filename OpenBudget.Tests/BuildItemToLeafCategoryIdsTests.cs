using OpenBudget.Models;
using OpenBudget.Services;
using Xunit;
using static OpenBudget.Tests.Hierarchy;

namespace OpenBudget.Tests;

public class BuildItemToLeafCategoryIdsTests
{
    // Sample hierarchy used across these tests:
    //   G1 (group, root)
    //     G2 (group, child of G1)
    //       C11 (leaf, child of G2)
    //       C12 (leaf, child of G2)
    //     C13 (leaf, direct child of G1)
    private static List<CategoryGroupWithChildCount> Sample() => new()
    {
        Group(1),
        Group(2, parent: 1),
        Leaf(11, parent: 2),
        Leaf(12, parent: 2),
        Leaf(13, parent: 1),
    };

    [Fact]
    public void Tracked_leaf_resolves_to_itself()
    {
        var map = CategoryHierarchyService.BuildItemToLeafCategoryIds(
            new[] { CatKey(13) },
            Sample());

        Assert.Equal(new HashSet<long> { 13 }, map[CatKey(13)]);
    }

    [Fact]
    public void Tracked_group_expands_to_every_descendant_leaf()
    {
        var map = CategoryHierarchyService.BuildItemToLeafCategoryIds(
            new[] { GroupKey(1) },
            Sample());

        Assert.Equal(new HashSet<long> { 11, 12, 13 }, map[GroupKey(1)]);
    }

    [Fact]
    public void Multi_level_groups_walk_all_the_way_down()
    {
        // G2 sits between G1 and C11/C12. A tracked G2 should still pull in both leaves.
        var map = CategoryHierarchyService.BuildItemToLeafCategoryIds(
            new[] { GroupKey(2) },
            Sample());

        Assert.Equal(new HashSet<long> { 11, 12 }, map[GroupKey(2)]);
    }

    [Fact]
    public void Overlapping_parent_and_descendant_keep_their_natural_leaf_sets()
    {
        // Per-row totals are intentional natural rollups — overlap is the caller's problem
        // (the dashboard uses a separate leaf union for aggregate totals).
        var map = CategoryHierarchyService.BuildItemToLeafCategoryIds(
            new[] { GroupKey(1), GroupKey(2), CatKey(11) },
            Sample());

        Assert.Equal(new HashSet<long> { 11, 12, 13 }, map[GroupKey(1)]);
        Assert.Equal(new HashSet<long> { 11, 12 }, map[GroupKey(2)]);
        Assert.Equal(new HashSet<long> { 11 }, map[CatKey(11)]);
    }
}
