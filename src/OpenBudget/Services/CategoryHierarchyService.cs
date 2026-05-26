using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>
/// Read-only view of the MoneyMoney category hierarchy + leaf-id resolution helpers used
/// across every spending / projection query. Split out of BudgetService because the
/// hierarchy fetch is one of the hottest read paths (5+ calls per dashboard render before
/// caching) and unrelated to the project/budget/cashflow concerns it used to share a class
/// with.
/// </summary>
public class CategoryHierarchyService
{
    private readonly BudgetDbContext _db;

    // Per-instance cache of GetAllCategoriesWithChildCountsAsync results. The hierarchy is
    // read-only within this service's lifetime — uploads happen in a separate scope via the
    // singleton import service — and was being fetched ~5× per dashboard render before this.
    // The service is scoped per Blazor circuit and dispatched single-threadedly, so no lock
    // is needed.
    private readonly Dictionary<int, List<CategoryGroupWithChildCount>> _hierarchyBySourceId = new();

    public CategoryHierarchyService(BudgetDbContext db)
    {
        _db = db;
    }

    /// <summary>All MoneyMoney leaf categories (mm_categories) for a source, name-ordered.</summary>
    public async Task<List<Category>> GetAllCategoriesAsync(int sourceId)
    {
        return await _db.Categories
            .Where(c => c.SourceId == sourceId)
            .OrderBy(c => c.Name)
            .ToListAsync();
    }

    /// <summary>
    /// Unified hierarchy: groups + leaf categories tagged via <see cref="CategoryGroupWithChildCount.ItemType"/>,
    /// with each entry's parent and direct-child counts. The source's sentinel rowid-0/1
    /// "Umbuchungen" root is filtered out so it doesn't pollute selection trees.
    /// </summary>
    public async Task<List<CategoryGroupWithChildCount>> GetAllCategoriesWithChildCountsAsync(int sourceId)
    {
        if (_hierarchyBySourceId.TryGetValue(sourceId, out var cached)) return cached;

        var sentinelRoot = SourceScope.SentinelRoot(sourceId);

        var allCategoryGroups = await _db.CategoryGroups
            .Where(cg => cg.SourceId == sourceId)
            .OrderBy(cg => cg.Name)
            .ToListAsync();

        var allCategories = await _db.Categories
            .Where(c => c.SourceId == sourceId)
            .OrderBy(c => c.Name)
            .ToListAsync();

        var result = new List<CategoryGroupWithChildCount>();

        foreach (var categoryGroup in allCategoryGroups)
        {
            // Skip the source's sentinel root category group (no name, holds "Umbuchungen").
            if (categoryGroup.Id == sentinelRoot && string.IsNullOrEmpty(categoryGroup.Name))
                continue;

            var subGroupCount = allCategoryGroups.Count(cg => cg.ParentGroupId == categoryGroup.Id);
            var categoryCount = allCategories.Count(c => c.GroupId == categoryGroup.Id);

            var isRootCategory = categoryGroup.ParentGroupId == null
                || categoryGroup.ParentGroupId == 0
                || categoryGroup.ParentGroupId == sentinelRoot;

            result.Add(new CategoryGroupWithChildCount
            {
                CategoryGroup = categoryGroup,
                ItemType = CategoryItemType.Group,
                DirectChildCount = subGroupCount + categoryCount,
                IsRootCategory = isRootCategory,
                ParentCategoryGroupId = categoryGroup.ParentGroupId
            });
        }

        foreach (var category in allCategories)
        {
            if (!category.GroupId.HasValue) continue;
            result.Add(new CategoryGroupWithChildCount
            {
                CategoryGroup = new CategoryGroup { Id = category.GroupId.Value }, // Placeholder for parent reference
                Category = category,
                ItemType = CategoryItemType.Category,
                DirectChildCount = 0,
                IsRootCategory = false,
                ParentCategoryGroupId = category.GroupId
            });
        }

        _hierarchyBySourceId[sourceId] = result;
        return result;
    }

    /// <summary>
    /// Single category-group lookup by namespaced ID. The ID is unique across sources because
    /// of namespacing, so no source filter is needed at the boundary.
    /// </summary>
    public async Task<CategoryGroup?> GetCategoryWithSubCategoriesAsync(long categoryGroupId)
    {
        return await _db.CategoryGroups
            .FirstOrDefaultAsync(cg => cg.Id == categoryGroupId);
    }

    /// <summary>
    /// All leaf-category IDs reachable from a group (including via descendant sub-groups).
    /// </summary>
    public async Task<List<long>> GetAllCategoryIdsForGroupAsync(long categoryGroupId, int sourceId)
    {
        var categoryIds = new List<long>();
        var groupIds = await GetAllCategoryGroupIdsRecursiveAsync(categoryGroupId, sourceId);
        foreach (var groupId in groupIds)
        {
            var categoriesInGroup = await _db.Categories
                .Where(c => c.SourceId == sourceId && c.GroupId == groupId)
                .Select(c => c.Id)
                .ToListAsync();
            categoryIds.AddRange(categoriesInGroup);
        }
        return categoryIds;
    }

    private async Task<List<long>> GetAllCategoryGroupIdsRecursiveAsync(long categoryGroupId, int sourceId)
    {
        var groupIds = new List<long> { categoryGroupId };
        var subGroups = await _db.CategoryGroups
            .Where(cg => cg.SourceId == sourceId && cg.ParentGroupId == categoryGroupId)
            .ToListAsync();
        foreach (var subGroup in subGroups)
        {
            var subGroupIds = await GetAllCategoryGroupIdsRecursiveAsync(subGroup.Id, sourceId);
            groupIds.AddRange(subGroupIds);
        }
        return groupIds;
    }

    /// <summary>
    /// Resolve each tracked (Id, ItemType) tuple to the set of leaf categories beneath it.
    /// Pure: only depends on the already-fetched hierarchy. Used everywhere a tracked-row's
    /// "natural total" needs to expand to the union of leaves underneath.
    /// </summary>
    public static Dictionary<(long, CategoryItemType), HashSet<long>> BuildItemToLeafCategoryIds(
        IEnumerable<(long Id, CategoryItemType ItemType)> items,
        List<CategoryGroupWithChildCount> allCategoriesWithCounts)
    {
        var map = new Dictionary<(long, CategoryItemType), HashSet<long>>();
        foreach (var item in items)
        {
            var leafIds = new HashSet<long>();
            if (item.ItemType == CategoryItemType.Category)
            {
                leafIds.Add(item.Id);
            }
            else
            {
                var descendants = CategoryGroupWithChildCount.CollectDescendants(
                    item.Id, allCategoriesWithCounts, includeLeafCategories: true);
                foreach (var d in descendants)
                {
                    if (d.ItemType == CategoryItemType.Category)
                        leafIds.Add(d.Id);
                }
            }
            map[item] = leafIds;
        }
        return map;
    }
}
