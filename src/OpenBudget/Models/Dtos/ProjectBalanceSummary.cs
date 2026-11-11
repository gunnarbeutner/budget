namespace OpenBudget.Models;

public class ProjectBalanceSummary
{
    /// <summary>Cashflow balance: accounts marked "In cashflow" only.</summary>
    public decimal Balance { get; set; }
    /// <summary>Count of accounts marked "In cashflow".</summary>
    public int AccountCount { get; set; }
    /// <summary>Balance across every assigned account, including those outside the cashflow.</summary>
    public decimal TotalBalance { get; set; }
    /// <summary>Count of all assigned accounts.</summary>
    public int TotalAccountCount { get; set; }

    /// <summary>True when accounts outside the cashflow make the total differ from the cashflow balance.</summary>
    public bool HasNonCashflowAccounts => TotalAccountCount > AccountCount;
}
