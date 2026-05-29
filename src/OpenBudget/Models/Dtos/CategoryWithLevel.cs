using OpenBudget.Services;

namespace OpenBudget.Models;

/// <summary>
/// A category-hierarchy node paired with its indent depth, for flat rendering of the
/// income/expense configuration table. Built by ProjectDetail and consumed by
/// ProjectIncomeExpenseCard.
/// </summary>
public class CategoryWithLevel
{
    public CategoryGroupWithChildCount CategoryGroup { get; set; } = null!;
    public int Level { get; set; }
}
