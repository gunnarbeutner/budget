namespace OpenBudget.Models;

/// <summary>
/// Everything needed to realise a sale of one rebalance position for tax: its FIFO lots plus the
/// resolved regime — keyed in the project's tax-profile map by the position's identity key. Assembled
/// by joining lots (scoped to the project's linked depots), the instrument mapping (for ISIN-less
/// positions), and the per-instrument tax settings.
/// </summary>
public class TaxProfile
{
    public List<OpenLot> Lots { get; init; } = new();
    public TaxAssetKind AssetKind { get; init; }
    public int SpekulationsfristYears { get; init; }
    public decimal TeilfreistellungRate { get; init; }
}
