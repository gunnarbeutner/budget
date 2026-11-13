using OpenBudget.Services;

namespace OpenBudget.Models;

/// <summary>
/// Per-project rollup for a month / YTD / year window: tracked-item rows plus the aggregate
/// budget vs. actual totals. Consumed by the dashboard tiles.
/// </summary>
public class ProjectSpendingSummary
{
    public int ProjectId { get; set; }
    public string OwnerUserId { get; set; } = string.Empty;
    public string ProjectName { get; set; } = string.Empty;
    public int Year { get; set; }
    public int FromMonth { get; set; }
    public int ToMonth { get; set; }
    public bool IsSingleMonth => FromMonth == ToMonth;
    public List<CategorySpending> CategorySpending { get; set; } = new();
    public decimal TotalBudget { get; set; }
    public decimal TotalSpent { get; set; }
    public decimal TotalRemaining { get; set; }

    /// <summary>True when an active Budget exists for the year and was used to populate Budget/Remaining.</summary>
    public bool HasBudget { get; set; }
    /// <summary>Sum of income across tracked income items for the month.</summary>
    public decimal MonthIncome { get; set; }
    /// <summary>Sum of expenses across tracked expense items for the month.</summary>
    public decimal MonthExpenses { get; set; }
    public decimal MonthNet => MonthIncome - MonthExpenses;
}

public class CategorySpending
{
    public long CategoryId { get; set; }
    public CategoryItemType ItemType { get; set; }
    public string CategoryName { get; set; } = string.Empty;
    public decimal Budget { get; set; }
    /// <summary>
    /// Month's amount in the item's natural direction: income amount for income items,
    /// expense amount for expense items. Always positive.
    /// </summary>
    public decimal Spent { get; set; }
    public decimal Remaining { get; set; }
    public bool IsIncome { get; set; }
    public decimal PercentUsed => Budget > 0 ? (Spent / Budget * 100) : 0;
}
