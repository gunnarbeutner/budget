using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace OpenBudget.Models;

/// <summary>
/// Per-instrument tax parameters a user sets, keyed by the PortfolioPerformance security uuid (so it
/// covers ISIN-less instruments too): the tax kind (which fixes the regime and §23 period) and, for
/// securities, the Teilfreistellung rate. User configuration kept across uploads, unlike the rebuilt
/// catalogue/lots. Absence means "use the defaults", so only overrides are stored.
/// </summary>
[Table("security_tax_setting")]
public class SecurityTaxSetting
{
    [Key]
    public int Id { get; set; }

    public int SourceId { get; set; }

    /// <summary>PortfolioPerformance security uuid this setting applies to.</summary>
    public string SecurityUuid { get; set; } = "";

    /// <summary><see cref="InstrumentTaxKind"/> as an int — Security / Crypto §23 / Real estate §23.</summary>
    public int Kind { get; set; }

    /// <summary>Teilfreistellung in basis points (3000 = 30%). Common values: 0 (direct shares /
    /// bond funds), 1500 (mixed fund), 3000 (equity fund), 6000 (domestic real-estate fund),
    /// 8000 (foreign real-estate fund). Only meaningful when <see cref="Kind"/> is Security.</summary>
    public int TeilfreistellungBp { get; set; }
}
