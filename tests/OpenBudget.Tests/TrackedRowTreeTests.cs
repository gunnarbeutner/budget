using OpenBudget.Services;
using Xunit;
using static OpenBudget.Tests.Hierarchy;

namespace OpenBudget.Tests;

public class TrackedRowTreeTests
{
    // Three-level hierarchy:
    //   G1 (group, root)
    //     G2 (group, child of G1)
    //       C11 (leaf)
    //       C12 (leaf)
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
    public void Single_root_renders_at_level_0()
    {
        var nodes = TrackedRowTree.Build(new[] { GroupKey(1) }, Sample());
        Assert.Single(nodes);
        Assert.Equal(GroupKey(1), nodes[0].Key);
        Assert.Equal(0, nodes[0].Level);
        Assert.False(nodes[0].HasChildren);
    }

    [Fact]
    public void Parent_with_tracked_child_marks_HasChildren()
    {
        var nodes = TrackedRowTree.Build(new[] { GroupKey(1), CatKey(13) }, Sample());
        var g1 = nodes.Single(n => n.Key == GroupKey(1));
        var c13 = nodes.Single(n => n.Key == CatKey(13));
        Assert.True(g1.HasChildren);
        Assert.Equal(0, g1.Level);
        Assert.Equal(1, c13.Level);
        Assert.Equal(GroupKey(1), c13.ParentKey);
    }

    [Fact]
    public void Multi_level_chain_nests_every_intermediate()
    {
        // This was the bug: when shadow expansion only surfaced leaves, a tracked-parent →
        // (untracked sub-group) → leaf chain rendered the leaf as a direct child of the
        // parent at Level 1, skipping the sub-group. With all three tracked, the leaf must
        // land at Level 2 with the sub-group as its parent.
        var nodes = TrackedRowTree.Build(
            new[] { GroupKey(1), GroupKey(2), CatKey(11) },
            Sample());

        var c11 = nodes.Single(n => n.Key == CatKey(11));
        Assert.Equal(2, c11.Level);
        Assert.Equal(GroupKey(2), c11.ParentKey);

        var g2 = nodes.Single(n => n.Key == GroupKey(2));
        Assert.Equal(1, g2.Level);
        Assert.Equal(GroupKey(1), g2.ParentKey);
    }

    [Fact]
    public void Walk_jumps_over_untracked_intermediate_to_nearest_tracked_ancestor()
    {
        // G2 is intentionally NOT tracked. C11's nearest tracked ancestor is G1, so it
        // becomes a Level-1 child of G1 in the projection.
        var nodes = TrackedRowTree.Build(
            new[] { GroupKey(1), CatKey(11) },
            Sample());

        var c11 = nodes.Single(n => n.Key == CatKey(11));
        Assert.Equal(1, c11.Level);
        Assert.Equal(GroupKey(1), c11.ParentKey);
    }
}
