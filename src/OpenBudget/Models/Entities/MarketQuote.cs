using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace OpenBudget.Models;

/// <summary>
/// A daily close of a quoted symbol, fetched from a market data feed (<see cref="Services.Quotes.QuoteRefreshService"/>).
/// Public market data, shared by every user whose PortfolioPerformance file quotes the symbol.
/// </summary>
[Table("market_quote")]
public class MarketQuote
{
    /// <summary>The feed's symbol, e.g. "EXAMPLE.DE".</summary>
    [MaxLength(64)]
    public string Symbol { get; set; } = "";

    /// <summary>Trading day as days since 1970-01-01.</summary>
    public long Day { get; set; }

    public double Close { get; set; }

    [MaxLength(8)]
    public string Currency { get; set; } = "";

    /// <summary>When the close was fetched (Unix seconds); a later fetch of the same day replaces it.</summary>
    public long FetchedAt { get; set; }
}
