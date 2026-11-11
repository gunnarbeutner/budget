namespace OpenBudget.Models;

/// <summary>Well-known values for <see cref="Account.Provider"/>.</summary>
public static class AccountProviders
{
    public const string MoneyMoney = "moneymoney";

    /// <summary>
    /// Nobody refreshes the account any more (a connector-created account whose connector link was
    /// removed). Its data stays; it can be linked to a connector again.
    /// </summary>
    public const string Manual = "manual";

    /// <summary>Computed from the source's PortfolioPerformance file (a portfolio's depot or ledger account).</summary>
    public const string PortfolioPerformance = "portfolioperformance";
}
