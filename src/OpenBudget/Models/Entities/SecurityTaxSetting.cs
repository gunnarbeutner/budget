using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace OpenBudget.Models;

/// <summary>
/// Per-security tax parameters a user sets, keyed by ISIN — currently the Teilfreistellung rate (the
/// fund-type-dependent exempt fraction of a gain). User configuration: keyed by the stable ISIN and
/// kept across PortfolioPerformance uploads, unlike the rebuilt catalogue/lots. Absence means "use
/// the default" (an equity fund's 30%), so only overrides are stored.
/// </summary>
[Table("security_tax_setting")]
public class SecurityTaxSetting
{
    [Key]
    public int Id { get; set; }

    public int SourceId { get; set; }

    /// <summary>ISIN of the security.</summary>
    public string Isin { get; set; } = "";

    /// <summary>Teilfreistellung in basis points (3000 = 30%). Common values: 0 (direct shares /
    /// bond funds), 1500 (mixed fund), 3000 (equity fund), 6000 (domestic real-estate fund),
    /// 8000 (foreign real-estate fund).</summary>
    public int TeilfreistellungBp { get; set; }
}
