using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace OpenBudget.Models;

/// <summary>
/// One security → taxonomy-classification weighting imported from PortfolioPerformance, keyed by
/// ISIN so it joins to <see cref="Holding"/> (which the source app provides with ISINs). A security
/// can appear in several classifications of the same taxonomy (e.g. a World ETF split across
/// regions), so this is one row per (security, classification) assignment.
///
/// App-native derived data — not namespaced like the imported tables. Scoped by <see cref="SourceId"/>
/// and replaced wholesale on each PortfolioPerformance upload for that source.
/// </summary>
[Table("pp_security_taxonomy")]
public class SecurityTaxonomy
{
    [Key]
    public int Id { get; set; }

    public int SourceId { get; set; }

    /// <summary>ISIN of the security; the join key to <see cref="Holding.Isin"/>.</summary>
    public string Isin { get; set; } = "";

    /// <summary>PortfolioPerformance taxonomy UUID (stable across renames).</summary>
    public string TaxonomyId { get; set; } = "";

    /// <summary>Taxonomy display name, e.g. "Regions" or "Asset Classes".</summary>
    public string TaxonomyName { get; set; } = "";

    public string ClassificationId { get; set; } = "";

    /// <summary>Leaf classification this assignment targets, e.g. "North America".</summary>
    public string ClassificationName { get; set; } = "";

    /// <summary>Path from the taxonomy's top level to the classification, " / "-joined (root excluded).</summary>
    public string ClassificationPath { get; set; } = "";

    /// <summary>
    /// Weight of the security in this classification, in basis points where 10000 = 100%. A
    /// security's weights across one taxonomy may sum to less than 100% (PP allows partial
    /// classification).
    /// </summary>
    public int WeightBp { get; set; }
}
