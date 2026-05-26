using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>
/// Group / category checkbox-toggle state mutations shared by Budgets and BudgetWizard. Both
/// pages previously hand-rolled the same triple (apply toggle, walk descendants, select
/// immediate children); behavior was equivalent but the code drifted.
/// </summary>
public static class CategorySelection
{
    /// <summary>
    /// Apply a checkbox toggle on a category group. Checking adds the group and clears any
    /// descendants from the selection (a tracked parent supersedes its descendants). Unchecking
    /// removes the group and auto-selects immediate children so the user can fine-tune.
    /// </summary>
    public static void ApplyGroupToggle(
        long groupId,
        bool isChecked,
        HashSet<long> selectedGroupIds,
        HashSet<long> selectedCategoryIds,
        List<CategoryGroupWithChildCount> allCategories)
    {
        if (isChecked)
        {
            selectedGroupIds.Add(groupId);
            RemoveDescendants(groupId, selectedGroupIds, selectedCategoryIds, allCategories);
        }
        else
        {
            selectedGroupIds.Remove(groupId);
            SelectImmediateChildren(groupId, selectedGroupIds, selectedCategoryIds, allCategories);
        }
    }

    private static void RemoveDescendants(
        long parentGroupId,
        HashSet<long> selectedGroupIds,
        HashSet<long> selectedCategoryIds,
        List<CategoryGroupWithChildCount> allCategories)
    {
        var descendants = CategoryGroupWithChildCount.CollectDescendants(
            parentGroupId, allCategories, includeLeafCategories: true);
        foreach (var d in descendants)
        {
            if (d.ItemType == CategoryItemType.Group) selectedGroupIds.Remove(d.Id);
            else if (d.ItemType == CategoryItemType.Category) selectedCategoryIds.Remove(d.Id);
        }
    }

    private static void SelectImmediateChildren(
        long parentGroupId,
        HashSet<long> selectedGroupIds,
        HashSet<long> selectedCategoryIds,
        List<CategoryGroupWithChildCount> allCategories)
    {
        var childGroups = allCategories
            .Where(c => c.ParentCategoryGroupId == parentGroupId && c.ItemType == CategoryItemType.Group)
            .ToList();
        var childCategories = allCategories
            .Where(c => c.ParentCategoryGroupId == parentGroupId && c.ItemType == CategoryItemType.Category)
            .ToList();

        foreach (var g in childGroups) selectedGroupIds.Add(g.Id);
        foreach (var c in childCategories) selectedCategoryIds.Add(c.Id);
    }
}
