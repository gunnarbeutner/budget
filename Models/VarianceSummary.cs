namespace OpenBudget.Models;

public class VarianceSummary
{
    public string CategoryName { get; set; } = string.Empty;
    public decimal BudgetAmount { get; set; }
    public decimal ActualAmount { get; set; }
    public decimal Variance { get; set; }
}
