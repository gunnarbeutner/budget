using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.Tests;

/// <summary>
/// Builders for a tiny CategoryGroupWithChildCount hierarchy. Used by the pure-function tests
/// so each test reads as data + expectation, not setup boilerplate.
/// </summary>
internal static class Hierarchy
{
    public static CategoryGroupWithChildCount Group(long id, long? parent = null, string? name = null) => new()
    {
        CategoryGroup = new CategoryGroup { Id = id, Name = name ?? $"G{id}" },
        ItemType = CategoryItemType.Group,
        ParentCategoryGroupId = parent,
        IsRootCategory = parent is null,
    };

    public static CategoryGroupWithChildCount Leaf(long id, long parent, string? name = null) => new()
    {
        CategoryGroup = new CategoryGroup { Id = parent },
        Category = new Category { Id = id, Name = name ?? $"C{id}", GroupId = parent },
        ItemType = CategoryItemType.Category,
        ParentCategoryGroupId = parent,
    };

    public static (long Id, CategoryItemType ItemType) GroupKey(long id) => (id, CategoryItemType.Group);
    public static (long Id, CategoryItemType ItemType) CatKey(long id) => (id, CategoryItemType.Category);

    public static decimal[] Months(decimal everyMonth)
    {
        var arr = new decimal[12];
        for (int i = 0; i < 12; i++) arr[i] = everyMonth;
        return arr;
    }
}
