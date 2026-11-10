using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>One category row of the Budget view's table: a tracked expense item of the spending summary.</summary>
public sealed record BudgetTableRow(long Id, CategoryItemType ItemType, string Name, string? Color, decimal Budget, decimal Spent)
{
    public decimal Left => Budget - Spent;

    /// <summary>The Transactions selection key for this row ("cat-12" / "grp-3").</summary>
    public string Selection => (ItemType == CategoryItemType.Group ? "grp-" : "cat-") + Id;
}

/// <summary>A bold group row of the table: the parent group of its rows, with their sums. A null
/// <see cref="GroupId"/> collects rows that sit at the top of the category tree.</summary>
public sealed record BudgetTableSection(long? GroupId, string? Name, IReadOnlyList<BudgetTableRow> Rows)
{
    public decimal Budget => Rows.Sum(r => r.Budget);
    public decimal Spent => Rows.Sum(r => r.Spent);
    public decimal Left => Budget - Spent;
}

/// <summary>
/// The Budget view's table: the expense rows of a <see cref="ProjectSpendingSummary"/> grouped under their parent
/// category group. Only regroups — every amount is the summary's own; <see cref="TotalBudget"/> and
/// <see cref="TotalSpent"/> are the summary's totals (the same numbers the switcher, the sidebar badge and the
/// Transactions summary strip show), not re-added from the rows.
/// </summary>
public sealed record BudgetTableModel(IReadOnlyList<BudgetTableSection> Sections, decimal TotalBudget, decimal TotalSpent, bool HasBudget)
{
    public decimal TotalLeft => TotalBudget - TotalSpent;

    /// <summary>
    /// The tracked income items, below the expense groups: <see cref="BudgetTableRow.Budget"/> is what is expected,
    /// <see cref="BudgetTableRow.Spent"/> what was received. Kept apart from the spending totals.
    /// </summary>
    public IReadOnlyList<BudgetTableRow> Income { get; init; } = [];

    public decimal IncomeExpected => Income.Sum(r => r.Budget);
    public decimal IncomeReceived => Income.Sum(r => r.Spent);

    public static BudgetTableModel Build(ProjectSpendingSummary summary, IReadOnlyList<CategoryGroupWithChildCount> hierarchy, long? hiddenRootGroupId = null)
    {
        var byKey = new Dictionary<(long, CategoryItemType), CategoryGroupWithChildCount>();
        foreach (var h in hierarchy) byKey.TryAdd((h.Id, h.ItemType), h);
        var groupNames = hierarchy.Where(h => h.ItemType == CategoryItemType.Group)
            .GroupBy(h => h.Id).ToDictionary(g => g.Key, g => g.First().Name);

        var sections = summary.CategorySpending
            .Where(c => !c.IsIncome)
            .Select(c =>
            {
                byKey.TryGetValue((c.CategoryId, c.ItemType), out var entry);
                var color = c.ItemType == CategoryItemType.Category ? entry?.Category?.Color : entry?.CategoryGroup?.Color;
                long? parent = entry?.ParentCategoryGroupId is { } p && p != hiddenRootGroupId && groupNames.ContainsKey(p) ? p : null;
                return (Parent: parent, Row: new BudgetTableRow(c.CategoryId, c.ItemType, c.CategoryName, color, c.Budget, c.Spent));
            })
            .GroupBy(x => x.Parent)
            .Select(g => new BudgetTableSection(
                g.Key,
                g.Key is { } id ? groupNames[id] : null,
                g.Select(x => x.Row).OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase).ToList()))
            // Named groups alphabetically, the top-level rows last.
            .OrderBy(s => s.GroupId == null)
            .ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var income = summary.CategorySpending
            .Where(c => c.IsIncome)
            .Select(c =>
            {
                byKey.TryGetValue((c.CategoryId, c.ItemType), out var entry);
                var color = c.ItemType == CategoryItemType.Category ? entry?.Category?.Color : entry?.CategoryGroup?.Color;
                return new BudgetTableRow(c.CategoryId, c.ItemType, c.CategoryName, color, c.Budget, c.Spent);
            })
            .OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return new BudgetTableModel(sections, summary.TotalBudget, summary.TotalSpent, summary.HasBudget) { Income = income };
    }
}
