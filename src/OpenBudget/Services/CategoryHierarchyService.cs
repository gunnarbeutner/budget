using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>
/// Read-only view of the imported category hierarchy + leaf-id resolution helpers used
/// across every spending / projection query. Split out of BudgetService because the
/// hierarchy fetch is one of the hottest read paths (5+ calls per dashboard render before
/// caching) and unrelated to the project/budget/cashflow concerns it used to share a class
/// with.
/// </summary>
public sealed class CategoryHierarchyService : IDisposable
{
    private readonly IDbContextFactory<BudgetDbContext> _dbFactory;
    private readonly ChangeNotificationService _notifier;

    // Per-instance cache of GetAllCategoriesWithChildCountsAsync results. The hierarchy was
    // being fetched ~5× per dashboard render before this. The service is scoped per Blazor
    // circuit, but the cache must be invalidated when another session imports data
    // (see OnDataChanged) — that notification fires on the *publishing* circuit's thread, so a
    // ConcurrentDictionary is used rather than a plain one to make clear-vs-read race-safe.
    private readonly ConcurrentDictionary<string, List<CategoryGroupWithChildCount>> _hierarchyByOwner = new(StringComparer.Ordinal);

    public CategoryHierarchyService(IDbContextFactory<BudgetDbContext> dbFactory, ChangeNotificationService notifier)
    {
        _dbFactory = dbFactory;
        _notifier = notifier;
        _notifier.Changed += OnDataChanged;
    }

    // An import in any session changes the category hierarchy, so
    // drop the cache; the next read for that circuit refetches the current structure.
    // origin is unused: a cache invalidation should happen regardless of which circuit triggered
    // the change (and imported-data/import-state changes originate from the API import, with no circuit).
    private void OnDataChanged(DataChange change, Guid? origin)
    {
        if (change.Area is DataArea.ImportedData or DataArea.ImportState)
            _hierarchyByOwner.Clear();
    }

    public void Dispose()
    {
        _notifier.Changed -= OnDataChanged;
        GC.SuppressFinalize(this);
    }

    /// <summary>All leaf categories (categories) for a source, name-ordered.</summary>
    public async Task<List<Category>> GetAllCategoriesAsync(string ownerId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.Categories
            .AsNoTracking()
            .Where(c => c.OwnerUserId == ownerId)
            .OrderBy(c => c.Name)
            .ToListAsync();
    }

    /// <summary>
    /// Unified hierarchy: groups + leaf categories tagged via <see cref="CategoryGroupWithChildCount.ItemType"/>,
    /// with each entry's parent and direct-child counts. MoneyMoney's sentinel rowid-0/1
    /// "Umbuchungen" root is filtered out so it doesn't pollute selection trees.
    /// </summary>
    public async Task<List<CategoryGroupWithChildCount>> GetAllCategoriesWithChildCountsAsync(string? ownerId)
    {
        if (ownerId == null) return [];
        if (_hierarchyByOwner.TryGetValue(ownerId, out var cached)) return cached;

        await using var db = await _dbFactory.CreateDbContextAsync();
        var sentinelRoot = await RowIds.SentinelRootAsync(db, ownerId);

        var allCategoryGroups = await db.CategoryGroups
            .AsNoTracking()
            .Where(cg => cg.OwnerUserId == ownerId)
            .OrderBy(cg => cg.Name)
            .ToListAsync();

        var allCategories = await db.Categories
            .AsNoTracking()
            .Where(c => c.OwnerUserId == ownerId)
            .OrderBy(c => c.Name)
            .ToListAsync();

        var result = new List<CategoryGroupWithChildCount>();

        foreach (var categoryGroup in allCategoryGroups)
        {
            // Skip MoneyMoney's sentinel root category group (no name, holds "Umbuchungen").
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

        _hierarchyByOwner[ownerId] = result;
        return result;
    }

    /// <summary>
    /// Single category-group lookup by ID. IDs are unique across users (their id spaces), so no owner filter is needed at the boundary.
    /// </summary>
    public async Task<CategoryGroup?> GetCategoryWithSubCategoriesAsync(long categoryGroupId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.CategoryGroups
            .AsNoTracking()
            .FirstOrDefaultAsync(cg => cg.Id == categoryGroupId);
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
