namespace OpenBudget.Services;

/// <summary>
/// Builds display labels for tracked categories/groups. The bare name is shown unless it
/// collides with another item in the same list — or an optional caller rule forces it — in
/// which case the item is qualified as "Parent › Name" so the two read unambiguously (e.g. an
/// umlagefähig vs. nicht-umlagefähig "Reparaturen"). The separator and the disambiguation rule
/// live here so every surface — overview, budget editor, wizard, charts — qualifies identically.
/// </summary>
public static class CategoryDisplay
{
    /// <summary>Separator between a qualifying parent group and the item's own name.</summary>
    public const string Separator = " › ";

    /// <summary>
    /// Maps each item's key to its display name. <paramref name="forcePrefix"/> lets a caller
    /// qualify an item even when its name is unique (used by the editors to keep a coexisting
    /// parent-group + sub-category row distinct). An item is never prefixed when its
    /// <c>ParentName</c> is empty.
    /// </summary>
    public static Dictionary<TKey, string> Disambiguate<TKey>(
        IEnumerable<(TKey Key, string BaseName, string? ParentName)> items,
        Func<TKey, bool>? forcePrefix = null)
        where TKey : notnull
    {
        var list = items.ToList();
        var nameCounts = list
            .GroupBy(i => i.BaseName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        return list.ToDictionary(
            i => i.Key,
            i => !string.IsNullOrEmpty(i.ParentName)
                && (nameCounts[i.BaseName] > 1 || (forcePrefix?.Invoke(i.Key) ?? false))
                    ? $"{i.ParentName}{Separator}{i.BaseName}"
                    : i.BaseName);
    }
}
