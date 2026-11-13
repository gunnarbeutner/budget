using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace OpenBudget.Models;

/// <summary>
/// A security catalogued from a PortfolioPerformance ".portfolio" file — its name and identifiers,
/// keyed by ISIN. The source app only knows the securities you currently hold, so this catalogue is
/// what lets the Rebalance page offer a security you don't yet own (its taxonomy weights live in
/// <see cref="SecurityTaxonomy"/>, joined by the same ISIN).
///
/// App-native derived data — not namespaced like the imported tables. Scoped by <see cref="OwnerUserId"/>
/// and replaced wholesale on each PortfolioPerformance upload for that source. Only ISIN-bearing
/// securities are stored (the ISIN is the join key to holdings and taxonomy).
/// </summary>
[Table("pp_security")]
public class PpSecurity
{
    [Key]
    public int Id { get; set; }

    /// <summary>The owning user (AppUser.Id).</summary>
    [Required]
    [MaxLength(450)]
    public string OwnerUserId { get; set; } = string.Empty;

    /// <summary>PortfolioPerformance security UUID — stable identity for every instrument, including
    /// the ISIN-less ones (crypto, real estate) that join to the source app via a manual mapping.</summary>
    public string Uuid { get; set; } = "";

    /// <summary>ISIN of the security, or empty for ISIN-less instruments (crypto, real estate). The
    /// join key to <see cref="Holding.Isin"/> / <see cref="SecurityTaxonomy.Isin"/> when present.</summary>
    public string Isin { get; set; } = "";

    /// <summary>Display name, e.g. "Vanguard FTSE All-World UCITS ETF".</summary>
    public string Name { get; set; } = "";

    public string? TickerSymbol { get; set; }

    public string? Wkn { get; set; }

    /// <summary>Latest quoted price per unit from PortfolioPerformance, in <see cref="Currency"/>, or
    /// null when the security has no latest quote. Lets a manually-added (not-held) security show
    /// share counts and round to whole shares.</summary>
    public decimal? Price { get; set; }

    /// <summary>Quote currency of <see cref="Price"/>.</summary>
    public string? Currency { get; set; }
}
