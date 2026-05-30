using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace OpenBudget.Models;

/// <summary>
/// One open (still-held) tax lot, reconstructed by <see cref="Services.FifoLedger"/> from a
/// PortfolioPerformance upload and persisted for the tax engine. Carries the German-tax essentials:
/// the acquisition date (§23 Spekulationsfrist) and the remaining FIFO cost basis. Keyed to a
/// <see cref="PpPortfolio"/> depot via <see cref="PortfolioUuid"/>. App-native; scoped by
/// <see cref="SourceId"/> and rebuilt wholesale on each upload.
/// </summary>
[Table("pp_lot")]
public class PpLot
{
    [Key]
    public int Id { get; set; }

    public int SourceId { get; set; }

    /// <summary>The PortfolioPerformance depot holding this lot (a <see cref="PpPortfolio.Uuid"/>).</summary>
    public string PortfolioUuid { get; set; } = "";

    /// <summary>ISIN of the security; the join key to holdings and taxonomy.</summary>
    public string Isin { get; set; } = "";

    /// <summary>Acquisition (trade) date of the lot — preserved across depot transfers.</summary>
    public DateOnly Acquired { get; set; }

    /// <summary>Remaining units in the lot.</summary>
    public decimal Shares { get; set; }

    /// <summary>Remaining acquisition cost (incl. buy fees) in <see cref="Currency"/>.</summary>
    public decimal Cost { get; set; }

    public string Currency { get; set; } = "EUR";
}
