using OpenBudget.Models;

namespace OpenBudget.Services;

public static class TrackingItemExtensions
{
    /// <summary>
    /// Each <see cref="ProjectTrackingItem"/> carries either a CategoryGroupId or a CategoryId —
    /// never both. This is the canonical fold to a tracked-key tuple, replacing several
    /// hand-rolled copies that drifted apart on the page side.
    /// </summary>
    public static IEnumerable<(long Id, CategoryItemType ItemType)> ToTrackedKeys(
        this IEnumerable<ProjectTrackingItem> items)
    {
        foreach (var ti in items)
        {
            if (ti.CategoryGroupId.HasValue)
                yield return (ti.CategoryGroupId.Value, CategoryItemType.Group);
            else if (ti.CategoryId.HasValue)
                yield return (ti.CategoryId.Value, CategoryItemType.Category);
        }
    }
}
