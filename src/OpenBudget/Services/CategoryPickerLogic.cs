using System.Globalization;
using System.Text;
using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>An assignable (leaf) category as the picker lists it. <see cref="Path"/> is "Group › Subgroup › Name".</summary>
public sealed record CategoryOption(long Id, string Name, string Path, string? Color);

/// <summary>
/// A category the bookings of a project can carry without it being one of the project's categories: its full path and
/// the first other project (of the ones the viewer sees) it belongs to, null when it belongs to none of them.
/// </summary>
public sealed record OutsideCategory(string Path, string? ProjectName);

/// <summary>One row of the picker's tree: a group header (not assignable) or a category.</summary>
/// <see cref="Color"/> is the category's own color (leaves only; null = palette by id).
public sealed record CategoryPickerRow(bool IsGroup, int Depth, long Id, string Name, string Path, string? Color = null);

/// <summary>The categories of one owner ready for the picker: a flat list (for search and lookup) and the tree rows.</summary>
public sealed class CategoryPickerData
{
    public IReadOnlyList<CategoryOption> Options { get; init; } = [];
    public IReadOnlyList<CategoryPickerRow> Tree { get; init; } = [];

    /// <summary>The owner's categories that are not among <see cref="Options"/> (set for a project's picker data).</summary>
    public IReadOnlyDictionary<long, OutsideCategory> Outside { get; init; } = new Dictionary<long, OutsideCategory>();

    private Dictionary<long, string>? _paths;

    /// <summary>The category as a category outside the project, or null when it is the project's own (or unknown).</summary>
    public OutsideCategory? OutsideOf(long? id) => id is { } v && Outside.TryGetValue(v, out var o) ? o : null;

    /// <summary>Only these categories, with the groups above them (groups left without a category below them are dropped).</summary>
    public CategoryPickerData Only(IReadOnlySet<long> categoryIds)
    {
        var rows = Tree.Where(r => r.IsGroup || categoryIds.Contains(r.Id)).ToList();
        int before;
        do
        {
            before = rows.Count;
            var current = rows;
            rows = current.Where((r, i) => !r.IsGroup || (i + 1 < current.Count && current[i + 1].Depth > r.Depth)).ToList();
        }
        while (rows.Count != before);
        return new CategoryPickerData { Options = Options.Where(o => categoryIds.Contains(o.Id)).ToList(), Tree = rows };
    }

    /// <summary>The category's full path ("Group › Subgroup › Name"), or its name when it is neither an option nor outside.</summary>
    public string PathOf(long? id, string name)
    {
        _paths ??= Options.GroupBy(o => o.Id).ToDictionary(g => g.Key, g => g.First().Path);
        if (id is not { } v) return name;
        return _paths.TryGetValue(v, out var path) ? path : Outside.TryGetValue(v, out var outside) ? outside.Path : name;
    }
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
    /// A project's picker data: the owner's categories narrowed to <paramref name="projectCategoryIds"/>, every other
    /// category of the owner kept as <see cref="CategoryPickerData.Outside"/> with the first of
    /// <paramref name="otherProjects"/> it belongs to.
    /// </summary>
    public static CategoryPickerData ForProject(CategoryPickerData all, IReadOnlySet<long> projectCategoryIds,
        IEnumerable<(string Name, IReadOnlySet<long> CategoryIds)> otherProjects)
    {
        var others = otherProjects.ToList();
        var outside = new Dictionary<long, OutsideCategory>();
        foreach (var o in all.Options)
        {
            if (projectCategoryIds.Contains(o.Id) || outside.ContainsKey(o.Id)) continue;
            var owner = others.FirstOrDefault(p => p.CategoryIds.Contains(o.Id)).Name;
            outside[o.Id] = new OutsideCategory(o.Path, owner);
        }
        var only = all.Only(projectCategoryIds);
        return new CategoryPickerData { Options = only.Options, Tree = only.Tree, Outside = outside };
    }

    /// <summary>
    /// For each category with namesakes, the shortest path suffix (in whole parts) that no namesake shares, e.g.
    /// "Unterhaltung › Amazon" next to "Onlinedienste › Amazon". Unique names get no entry.
    /// </summary>
    public static Dictionary<long, string> ShortLabels(IReadOnlyList<CategoryOption> options)
    {
        var labels = new Dictionary<long, string>();
        foreach (var same in options.GroupBy(o => o.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            var parts = same.Select(o => (o.Id, Parts: o.Path.Split(PathSeparator))).ToList();
            foreach (var (id, mine) in parts)
            {
                string Suffix(string[] p, int take) => string.Join(PathSeparator, p.Skip(Math.Max(0, p.Length - take)));
                var take = 2;
                while (take < mine.Length && parts.Any(o => o.Id != id
                           && string.Equals(Suffix(o.Parts, take), Suffix(mine, take), StringComparison.OrdinalIgnoreCase)))
                    take++;
                labels.TryAdd(id, Suffix(mine, take));
            }
        }
        return labels;
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
