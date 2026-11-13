namespace OpenBudget.Models;

public class UncategorizedSummary
{
    public string OwnerUserId { get; set; } = string.Empty;
    public int Count { get; set; }
    public decimal AbsoluteAmount { get; set; }
}
