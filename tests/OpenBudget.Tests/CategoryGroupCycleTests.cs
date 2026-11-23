using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;
using OpenBudget.Services;
using Xunit;
using static OpenBudget.Tests.Hierarchy;

namespace OpenBudget.Tests;

/// <summary>
/// A category group whose parent chain loops (its own parent, or A → B → A) behaves like a group whose loop edge is
/// ignored: every walk over the hierarchy ends instead of overflowing the stack.
/// </summary>
public class CategoryGroupCycleTests
{
    private static async Task<TestDb> WorldAsync(params (long Id, long? Parent)[] loopedParents)
    {
        var t = new TestDb();
        foreach (var (id, _) in loopedParents) t.AddGroup(id);
        await t.Db.SaveChangesAsync();
        foreach (var (id, parent) in loopedParents)
            await t.Db.CategoryGroups.Where(g => g.Id == id).ExecuteUpdateAsync(u => u.SetProperty(g => g.ParentGroupId, parent));
        return t;
    }

    [Fact]
    public async Task A_group_that_is_its_own_parent_becomes_a_root()
    {
        await using var t = await WorldAsync((1, 1));
        t.AddLeaf(11, 1);
        await t.Db.SaveChangesAsync();

        var hierarchy = await t.Hierarchy.GetAllCategoriesWithChildCountsAsync(TestDb.DefaultOwner);
        var group = hierarchy.Single(h => h.ItemType == CategoryItemType.Group && h.Id == 1);
        Assert.Null(group.ParentCategoryGroupId);
        Assert.True(group.IsRootCategory);
        Assert.Equal(1, group.DirectChildCount);

        var map = CategoryHierarchyService.BuildItemToLeafCategoryIds([GroupKey(1)], hierarchy);
        Assert.Equal(new HashSet<long> { 11 }, map[GroupKey(1)]);
    }

    [Fact]
    public async Task A_two_group_loop_keeps_one_edge_and_drops_the_one_closing_the_loop()
    {
        await using var t = await WorldAsync((1, 2), (2, 1));
        t.AddLeaf(11, 1);
        t.AddLeaf(21, 2);
        await t.Db.SaveChangesAsync();

        var hierarchy = await t.Hierarchy.GetAllCategoriesWithChildCountsAsync(TestDb.DefaultOwner);
        var one = hierarchy.Single(h => h.ItemType == CategoryItemType.Group && h.Id == 1);
        var two = hierarchy.Single(h => h.ItemType == CategoryItemType.Group && h.Id == 2);
        Assert.Null(one.ParentCategoryGroupId);
        Assert.True(one.IsRootCategory);
        Assert.Equal(1L, two.ParentCategoryGroupId);

        var map = CategoryHierarchyService.BuildItemToLeafCategoryIds([GroupKey(1), GroupKey(2)], hierarchy);
        Assert.Equal(new HashSet<long> { 11, 21 }, map[GroupKey(1)]);
        Assert.Equal(new HashSet<long> { 21 }, map[GroupKey(2)]);
    }

    [Fact]
    public void Walks_over_a_looped_hierarchy_end()
    {
        // Built directly, without the hierarchy service dropping the loop edges.
        List<CategoryGroupWithChildCount> selfLoop = [Group(1, parent: 1), Leaf(11, parent: 1)];
        Assert.Equal(new HashSet<long> { 11 }, CategoryHierarchyService.BuildItemToLeafCategoryIds([GroupKey(1)], selfLoop)[GroupKey(1)]);

        List<CategoryGroupWithChildCount> twoLoop = [Group(1, parent: 2), Group(2, parent: 1), Leaf(11, parent: 1), Leaf(21, parent: 2)];
        Assert.Equal(new HashSet<long> { 11, 21 }, CategoryHierarchyService.BuildItemToLeafCategoryIds([GroupKey(1)], twoLoop)[GroupKey(1)]);
        Assert.False(CategoryGroupWithChildCount.IsDescendantOf(twoLoop[2], 99, twoLoop));
        Assert.True(CategoryGroupWithChildCount.IsDescendantOf(twoLoop[2], 2, twoLoop));
    }

    [Fact]
    public void Loop_edges_cover_every_loop_and_nothing_else()
    {
        Assert.Equal(new HashSet<long> { 1 }, CategoryHierarchyService.LoopEdges([(1, 1)]));
        Assert.Equal(new HashSet<long> { 1 }, CategoryHierarchyService.LoopEdges([(2, 1), (1, 2)]));
        Assert.Equal(new HashSet<long> { 3 }, CategoryHierarchyService.LoopEdges([(3, 4), (4, 5), (5, 3), (6, 3), (7, null)]));
        Assert.Empty(CategoryHierarchyService.LoopEdges([(1, null), (2, 1), (3, 2), (4, 99)]));
    }
}
