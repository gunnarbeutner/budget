using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace OpenBudget.Models;

/// <summary>
/// A security catalogued from a PortfolioPerformance ".portfolio" file — its name and identifiers,
/// keyed by ISIN. MoneyMoney only knows the securities you currently hold, so this catalogue is
/// what lets the Rebalance page offer a security you don't yet own (its taxonomy weights live in
/// <see cref="SecurityTaxonomy"/>, joined by the same ISIN).
///
/// App-native derived data — not namespaced like the mm_* tables. Scoped by <see cref="SourceId"/>
/// and replaced wholesale on each PortfolioPerformance upload for that source. Only ISIN-bearing
/// securities are stored (the ISIN is the join key to holdings and taxonomy).
/// </summary>
[Table("pp_security")]
public class PpSecurity
{
    [Key]
    public int Id { get; set; }

    public int SourceId { get; set; }

    /// <summary>ISIN of the security; the join key to <see cref="Holding.Isin"/> and
    /// <see cref="SecurityTaxonomy.Isin"/>.</summary>
    public string Isin { get; set; } = "";

    /// <summary>Display name, e.g. "Vanguard FTSE All-World UCITS ETF".</summary>
    public string Name { get; set; } = "";

    public string? TickerSymbol { get; set; }

    public string? Wkn { get; set; }
}
