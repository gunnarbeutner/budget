namespace OpenBudget.Models;

public class MonthlySpending
{
    public int Year { get; set; }
    public int Month { get; set; }
    public decimal Amount { get; set; }
    public string MonthName => new DateTime(Year, Month, 1).ToString("MMM yyyy");
}
