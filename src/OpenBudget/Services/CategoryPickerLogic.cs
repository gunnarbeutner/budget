using System.Globalization;
using System.Text;
using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>An assignable (leaf) category as the picker lists it. <see cref="Path"/> is "Group › Subgroup › Name".</summary>
public sealed record CategoryOption(long Id, string Name, string Path, string? Color);

/// <summary>One row of the picker's tree: a group header (not assignable) or a category.</summary>
/// <see cref="Color"/> is the category's own color (leaves only; null = palette by id).
public sealed record CategoryPickerRow(bool IsGroup, int Depth, long Id, string Name, string Path, string? Color = null);

/// <summary>The categories of one owner ready for the picker: a flat list (for search and lookup) and the tree rows.</summary>
public sealed class CategoryPickerData
{
    public IReadOnlyList<CategoryOption> Options { get; init; } = [];
    public IReadOnlyList<CategoryPickerRow> Tree { get; init; } = [];
}

/// <summary>
/// Builds the picker data (pure, no DB) and searches it. Search is token based over name AND path, ignoring case and
/// diacritics, so "lebens" finds "Haushalt › Lebensmittel" and "haus leb" finds it too.
/// </summary>
public static class CategoryPickerLogic
{
    public const string PathSeparator = " › ";

    public static CategoryPickerData Build(IReadOnlyCollection<CategoryGroup> groups, IReadOnlyCollection<Category> categories, long sentinelRoot)
    {
        var groupById = groups.GroupBy(g => g.Id).ToDictionary(g => g.Key, g => g.First());
        bool IsKnown(long? id) => id is { } v && groupById.ContainsKey(v);

        // Hidden: MoneyMoney's unnamed sentinel root. Its children are shown at the top level without a prefix.
        bool IsHidden(CategoryGroup g) => string.IsNullOrEmpty(g.Name) && (g.Id == sentinelRoot || !IsKnown(g.ParentGroupId));

        var leaves = categories.Where(c => !string.IsNullOrEmpty(c.Name)).ToList();
        var childGroups = groups.GroupBy(g => IsKnown(g.ParentGroupId) && g.ParentGroupId != g.Id ? g.ParentGroupId!.Value : 0L).ToDictionary(g => g.Key, g => g.ToList());
        var leavesByGroup = leaves.GroupBy(c => IsKnown(c.GroupId) ? c.GroupId!.Value : 0L).ToDictionary(g => g.Key, g => g.ToList());

        var options = new List<CategoryOption>();
        var rows = new List<CategoryPickerRow>();
        var onPath = new HashSet<long>();

        void Walk(long groupKey, int depth, string prefix)
        {
            var items = new List<(long? Sort, string Name, CategoryGroup? Group, Category? Leaf)>();
            if (childGroups.TryGetValue(groupKey, out var gs)) items.AddRange(gs.Select(g => (g.Sort, g.Name ?? "", (CategoryGroup?)g, (Category?)null)));
            if (leavesByGroup.TryGetValue(groupKey, out var ls)) items.AddRange(ls.Select(c => (c.Sort, c.Name ?? "", (CategoryGroup?)null, (Category?)c)));
            foreach (var item in items.OrderBy(i => i.Sort ?? long.MaxValue).ThenBy(i => i.Group == null ? 1 : 0).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                if (item.Leaf is { } leaf)
                {
                    var path = prefix + leaf.Name;
                    options.Add(new CategoryOption(leaf.Id, leaf.Name!, path, leaf.Color));
                    rows.Add(new CategoryPickerRow(false, depth, leaf.Id, leaf.Name!, path, leaf.Color));
                }
                else if (item.Group is { } g && onPath.Add(g.Id))
                {
                    if (IsHidden(g))
                    {
                        Walk(g.Id, depth, prefix);
                    }
                    else
                    {
                        rows.Add(new CategoryPickerRow(true, depth, g.Id, g.Name ?? "", prefix + g.Name));
                        Walk(g.Id, depth + 1, prefix + g.Name + PathSeparator);
                    }
                    onPath.Remove(g.Id);
                }
            }
        }

        Walk(0, 0, "");
        // A group without any assignable category below it (a header not followed by something deeper) is noise.
        int before;
        do
        {
            before = rows.Count;
            var current = rows;
            rows = current.Where((r, i) => !r.IsGroup || (i + 1 < current.Count && current[i + 1].Depth > r.Depth)).ToList();
        }
        while (rows.Count != before);
        return new CategoryPickerData { Options = options, Tree = rows };
    }

    /// <summary>
    /// Options matching the query (all tokens must occur in path + name), best first: names starting with the first token,
    /// then names containing it, then path-only hits; ties in tree order. An empty query returns everything in tree order.
    /// </summary>
    public static IReadOnlyList<CategoryOption> Search(IReadOnlyList<CategoryOption> options, string? query)
    {
        var tokens = Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return options;
        var scored = new List<(int Score, int Order, CategoryOption Option)>();
        for (var i = 0; i < options.Count; i++)
        {
            var o = options[i];
            var path = Normalize(o.Path);
            if (!tokens.All(t => path.Contains(t, StringComparison.Ordinal))) continue;
            var name = Normalize(o.Name);
            var score = name.StartsWith(tokens[0], StringComparison.Ordinal) ? 0
                : tokens.All(t => name.Contains(t, StringComparison.Ordinal)) ? 1
                : 2;
            scored.Add((score, i, o));
        }
        return scored.OrderBy(s => s.Score).ThenBy(s => s.Order).Select(s => s.Option).ToList();
    }

    /// <summary>Lower-cases, strips diacritics and folds ß, so "Lebensmittel", "lebensmittel" and "Lébensmittel" compare equal.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var decomposed = text.Trim().Replace("ß", "ss", StringComparison.Ordinal).Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(char.ToUpperInvariant(ch));
        }
        return sb.ToString().ToUpperInvariant();
    }
}
