using OpenBudget.Services;
using Xunit;
using static OpenBudget.Tests.Hierarchy;

namespace OpenBudget.Tests;

public class CategorySelectionTests
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
    public void Checking_a_parent_removes_all_descendants_from_selection()
    {
        var groups = new HashSet<long> { 2 };
        var cats = new HashSet<long> { 11, 12, 13 };

        CategorySelection.ApplyGroupToggle(
            groupId: 1, isChecked: true,
            selectedGroupIds: groups, selectedCategoryIds: cats,
            allCategories: Sample());

        Assert.Contains(1L, groups);
        Assert.DoesNotContain(2L, groups);
        Assert.Empty(cats);
    }

    [Fact]
    public void Unchecking_a_parent_auto_selects_immediate_children()
    {
        var groups = new HashSet<long> { 1 };
        var cats = new HashSet<long>();

        CategorySelection.ApplyGroupToggle(
            groupId: 1, isChecked: false,
            selectedGroupIds: groups, selectedCategoryIds: cats,
            allCategories: Sample());

        Assert.DoesNotContain(1L, groups);
        Assert.Contains(2L, groups); // immediate child group
        Assert.Contains(13L, cats);   // immediate child leaf
        Assert.DoesNotContain(11L, cats); // grandchild, not immediate
        Assert.DoesNotContain(12L, cats);
    }

    [Fact]
    public void Multi_level_descendant_walk_clears_grandchildren_too()
    {
        var groups = new HashSet<long> { 2 };
        var cats = new HashSet<long> { 11, 12, 13 };

        // Checking the root should walk all the way down and clear every nested entry.
        CategorySelection.ApplyGroupToggle(
            groupId: 1, isChecked: true,
            selectedGroupIds: groups, selectedCategoryIds: cats,
            allCategories: Sample());

        Assert.Equal(new HashSet<long> { 1 }, groups);
        Assert.Empty(cats);
    }
}
