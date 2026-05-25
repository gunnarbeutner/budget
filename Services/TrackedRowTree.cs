using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>
/// Projection helper that orders a flat set of tracked (Id, ItemType) keys into a DFS-preorder
/// list according to the category hierarchy, so a page can render them as a tree with one row
/// per item — parents before children, with explicit Level and HasChildren.
/// </summary>
public static class TrackedRowTree
{
    public static List<TrackedRowNode> Build(
        IEnumerable<(long Id, CategoryItemType ItemType)> items,
        List<CategoryGroupWithChildCount> hierarchy,
        Func<(long Id, CategoryItemType ItemType), string>? nameLookup = null)
    {
        var itemSet = items.ToHashSet();
        if (itemSet.Count == 0) return new List<TrackedRowNode>();

        // Lookup of hierarchy nodes by (Id, ItemType) so we can read ParentCategoryGroupId.
        var byKey = hierarchy.ToDictionary(h => (h.Id, h.ItemType));

        // For each tracked item, walk up the category tree to find the nearest also-tracked
        // ancestor. That becomes its parent in the projection. Items with no tracked ancestor
        // become roots (ParentKey = null).
        var parentOf = new Dictionary<(long Id, CategoryItemType ItemType), (long Id, CategoryItemType ItemType)?>();
        foreach (var item in itemSet)
        {
            (long Id, CategoryItemType ItemType)? parent = null;
            byKey.TryGetValue(item, out var node);
            var walker = node?.ParentCategoryGroupId;
            while (walker.HasValue)
            {
                var candidate = (walker.Value, CategoryItemType.Group);
                if (itemSet.Contains(candidate))
                {
                    parent = candidate;
                    break;
                }
                byKey.TryGetValue(candidate, out var parentNode);
                walker = parentNode?.ParentCategoryGroupId;
            }
            parentOf[item] = parent;
        }

        // Roots (no tracked ancestor) and children-of-tracked-parent maps. Stable sort by
        // displayed name if a lookup is provided, otherwise by raw Id.
        var roots = new List<(long Id, CategoryItemType ItemType)>();
        var childrenOf = new Dictionary<(long Id, CategoryItemType ItemType), List<(long Id, CategoryItemType ItemType)>>();
        foreach (var item in itemSet)
        {
            var p = parentOf[item];
            if (p == null)
            {
                roots.Add(item);
            }
            else
            {
                if (!childrenOf.TryGetValue(p.Value, out var list))
                {
                    list = new List<(long, CategoryItemType)>();
                    childrenOf[p.Value] = list;
                }
                list.Add(item);
            }
        }
        // Sort siblings by MoneyMoney's `sort` field (the user-facing order in MoneyMoney —
        // e.g. "Sonstiges" sits at the bottom of each group there). Tiebreak by display name
        // (when provided) so unsorted-vs-unsorted rows still have a stable order.
        long? SortKey((long Id, CategoryItemType ItemType) key)
        {
            if (!byKey.TryGetValue(key, out var node)) return null;
            return node.ItemType == CategoryItemType.Category ? node.Category?.Sort : node.CategoryGroup.Sort;
        }
        var comparer = Comparer<(long, CategoryItemType)>.Create((a, b) =>
        {
            var sa = SortKey(a);
            var sb = SortKey(b);
            // Nulls sort last.
            if (sa.HasValue != sb.HasValue) return sa.HasValue ? -1 : 1;
            if (sa.HasValue && sa.Value != sb!.Value) return sa.Value.CompareTo(sb!.Value);
            var na = nameLookup?.Invoke(a) ?? "";
            var nb = nameLookup?.Invoke(b) ?? "";
            var cmp = string.Compare(na, nb, StringComparison.CurrentCultureIgnoreCase);
            return cmp != 0 ? cmp : a.Item1.CompareTo(b.Item1);
        });
        roots.Sort(comparer);
        foreach (var list in childrenOf.Values) list.Sort(comparer);

        var result = new List<TrackedRowNode>();
        foreach (var r in roots) Walk(r, null, 0);
        return result;

        void Walk((long Id, CategoryItemType ItemType) item, (long Id, CategoryItemType ItemType)? parent, int level)
        {
            bool hasKids = childrenOf.ContainsKey(item);
            result.Add(new TrackedRowNode
            {
                Key = item,
                ParentKey = parent,
                Level = level,
                HasChildren = hasKids
            });
            if (childrenOf.TryGetValue(item, out var kids))
                foreach (var k in kids) Walk(k, item, level + 1);
        }
    }
}

public record TrackedRowNode
{
    public (long Id, CategoryItemType ItemType) Key { get; init; }
    public (long Id, CategoryItemType ItemType)? ParentKey { get; init; }
    public int Level { get; init; }
    public bool HasChildren { get; init; }
}
