using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace OpenBudget.Models;

/// <summary>
/// Maps a PortfolioPerformance depot (<see cref="PpPortfolio.Uuid"/>) to a imported depot account
/// (<see cref="Account.Id"/>), so the FIFO lots reconstructed from PP attach to the right held
/// positions for per-depot tax math. Unlike the rebuilt-on-upload PP tables, this is <b>user
/// configuration</b> — it's keyed by the stable PP uuid and survives re-uploads, so it is not
/// cleared when the catalogue/lots are replaced.
/// </summary>
[Table("pp_portfolio_link")]
public class PpPortfolioLink
{
    [Key]
    public int Id { get; set; }

    public int SourceId { get; set; }

    /// <summary>The PortfolioPerformance depot being mapped (<see cref="PpPortfolio.Uuid"/>).</summary>
    public string PpPortfolioUuid { get; set; } = "";

    /// <summary>The imported depot account it maps to (namespaced <see cref="Account.Id"/>).</summary>
    public long AccountId { get; set; }
}
