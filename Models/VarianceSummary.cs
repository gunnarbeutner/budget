using OpenBudget.Services;

namespace OpenBudget.Models;

public class VarianceSummary
{
    public long CategoryId { get; set; }
    public CategoryItemType ItemType { get; set; }
    public string CategoryName { get; set; } = string.Empty;
    public decimal BudgetAmount { get; set; }
    public decimal ActualAmount { get; set; }
    public decimal Variance { get; set; }

    /// <summary>
    /// True for rows that should participate in totals (leaves of the chart's selected
    /// category tree). Group rows whose tracked descendants are also charted show in the
    /// table for context but their numbers are excluded from the footer to avoid double-counting.
    /// </summary>
    public bool IsAggregateRow { get; set; }

    /// <summary>
    /// False for shadow sub-cats that have no budget entry — the table renders "—" for
    /// Budget / Variance / % so they don't look like deliberate zero-budget rows.
    /// </summary>
    public bool HasBudget { get; set; }
}
