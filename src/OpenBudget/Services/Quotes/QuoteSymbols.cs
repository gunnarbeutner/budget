using OpenBudget.PortfolioPerformance.Proto;

namespace OpenBudget.Services.Quotes;

/// <summary>Which PortfolioPerformance securities the app quotes itself, and under which Yahoo symbol.</summary>
public static class QuoteSymbols
{
    /// <summary>
    /// The Yahoo symbol of a security whose history comes from Yahoo, or from PortfolioPerformance's own feed (a sign-in
    /// the server does not have) with a Yahoo-style ticker; null for every other feed (manual, JSON, adjusted closes, …).
    /// </summary>
    public static string? YahooSymbol(PSecurity security)
    {
        var ticker = security.TickerSymbol?.Trim();
        if (string.IsNullOrEmpty(ticker) || string.IsNullOrEmpty(security.CurrencyCode)) return null;
        return security.Feed is "YAHOO" or "PP" ? ticker : null;
    }
}
