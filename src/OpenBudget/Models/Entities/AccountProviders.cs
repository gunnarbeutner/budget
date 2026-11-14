namespace OpenBudget.Models;

/// <summary>Well-known values for <see cref="Account.Provider"/>.</summary>
public static class AccountProviders
{
    /// <summary>
    /// Nobody refreshes the account: entered by hand, imported from MoneyMoney, or left behind by a removed connector
    /// link. Its data stays editable; it can be linked to a connector, which then takes it over.
    /// </summary>
    public const string Manual = "manual";

    /// <summary>Computed from the owner's PortfolioPerformance file (a portfolio's depot or ledger account).</summary>
    public const string PortfolioPerformance = "portfolioperformance";
}
