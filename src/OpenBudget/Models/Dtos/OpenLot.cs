namespace OpenBudget.Models;

/// <summary>
/// One open (still-held) tax lot reconstructed from PortfolioPerformance's transaction ledger by
/// replaying buys/sells FIFO. Carries the German-tax essentials: when it was acquired (drives the
/// §23 Spekulationsfrist) and what it cost (the FIFO cost basis for a realised gain). Shares and
/// cost are the lot's <i>remaining</i> amounts after earlier sales consumed part of it.
/// </summary>
public class OpenLot
{
    /// <summary>The PortfolioPerformance depot (portfolio uuid) currently holding this lot. German
    /// FIFO is per depot per security, and a transfer moves a lot here keeping its acquisition date.</summary>
    public string PortfolioUuid { get; init; } = "";

    /// <summary>ISIN of the security this lot belongs to.</summary>
    public string Isin { get; init; } = "";

    /// <summary>Acquisition date — the trade date of the buy/inbound that opened the lot. A lot
    /// transferred between the user's own depots keeps its original acquisition date.</summary>
    public DateOnly Acquired { get; init; }

    /// <summary>Remaining units in this lot.</summary>
    public decimal Shares { get; init; }

    /// <summary>Remaining acquisition cost (incl. buy fees) for the remaining units, in
    /// <see cref="Currency"/> — the FIFO basis netted against proceeds to get the realised gain.</summary>
    public decimal Cost { get; init; }

    public string Currency { get; init; } = "EUR";
}
