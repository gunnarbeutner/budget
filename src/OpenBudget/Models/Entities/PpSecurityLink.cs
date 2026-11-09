using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace OpenBudget.Models;

/// <summary>
/// Maps an ISIN-less PortfolioPerformance instrument (crypto, real estate) to a imported holding,
/// so its reconstructed FIFO lots attach to the right position for tax math. ISIN-bearing securities
/// join automatically by ISIN and don't need this; the ISIN-less ones have nothing to join on, hence
/// a manual mapping. User configuration — keyed by the stable PP security uuid and kept across
/// uploads.
/// </summary>
[Table("pp_security_link")]
public class PpSecurityLink
{
    [Key]
    public int Id { get; set; }

    public int SourceId { get; set; }

    /// <summary>The PortfolioPerformance instrument being mapped (<see cref="PpSecurity.Uuid"/>).</summary>
    public string PpSecurityUuid { get; set; } = "";

    /// <summary>The imported holding it maps to, by the same identity key the holdings views use
    /// ("sym:BTC", "name:Musterstraße 1", …).</summary>
    public string MmHoldingKey { get; set; } = "";
}
