namespace OpenBudget.Models;

public class ProjectBalanceSummary
{
    /// <summary>Cashflow-relevant balance: Shared + Exclusive accounts only.</summary>
    public decimal Balance { get; set; }
    /// <summary>Count of cashflow accounts (Shared + Exclusive).</summary>
    public int AccountCount { get; set; }
    /// <summary>Balance across every assigned account, including Informational ones.</summary>
    public decimal TotalBalance { get; set; }
    /// <summary>Count of all assigned accounts.</summary>
    public int TotalAccountCount { get; set; }

    /// <summary>True when Informational accounts make the total differ from the cashflow balance.</summary>
    public bool HasInformational => TotalAccountCount > AccountCount;
}
