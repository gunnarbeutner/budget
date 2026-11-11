using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace OpenBudget.Models;

/// <summary>
/// A PortfolioPerformance depot (its "portfolio"), catalogued from an uploaded ".portfolio" file so
/// it can be mapped to a imported depot account — the link that lets per-depot FIFO lots
/// (<see cref="PpLot"/>) attach to a project's holdings. App-native; scoped by <see cref="SourceId"/>
/// and replaced wholesale on each upload.
/// </summary>
[Table("pp_portfolio")]
public class PpPortfolio
{
    [Key]
    public int Id { get; set; }

    public int SourceId { get; set; }

    /// <summary>PortfolioPerformance portfolio UUID — the key <see cref="PpLot.PortfolioUuid"/>
    /// references and the depot link maps to a imported account.</summary>
    public string Uuid { get; set; } = "";

    /// <summary>Depot display name, e.g. "Scalable-Capital-Depot".</summary>
    public string Name { get; set; } = "";

    /// <summary>Retired ("archived") in PortfolioPerformance.</summary>
    public bool IsRetired { get; set; }
}
