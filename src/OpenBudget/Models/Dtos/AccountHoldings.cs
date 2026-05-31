namespace OpenBudget.Models;

/// <summary>
/// The live holdings of a single depot account, decorated for the Browse holdings card: per-position
/// rows (sorted by value), the account's total market value, and an asset-class allocation breakdown.
/// Built by <see cref="Services.PortfolioService"/> from imported <see cref="Holding"/> rows.
/// </summary>
public class AccountHoldings
{
    public long AccountId { get; init; }
    public string Currency { get; init; } = "EUR";
    public decimal TotalValue { get; init; }
    public List<HoldingRow> Rows { get; init; } = new();
    public List<AssetClassSlice> Allocation { get; init; } = new();
    /// <summary>
    /// PortfolioPerformance look-through allocations (e.g. by Region or Asset Class), one entry per
    /// taxonomy that covers at least one of this account's holdings. Empty when no taxonomy has been
    /// uploaded or none of the holdings carry a matching ISIN (e.g. a pure-crypto depot).
    /// </summary>
    public List<TaxonomyLookThrough> Taxonomies { get; init; } = new();
}

/// <summary>
/// A look-through breakdown of the account by one PortfolioPerformance taxonomy: each holding's
/// value distributed across top-level classifications by its assigned weights, with the unassigned
/// remainder rolled into an "Unclassified" slice so the slices sum to the account total.
/// </summary>
public class TaxonomyLookThrough
{
    public string TaxonomyName { get; init; } = "";
    public List<AssetClassSlice> Slices { get; init; } = new();
}

public class HoldingRow
{
    /// <summary>Stable identity (ISIN, else symbol, else name) — used to track selection across renders.</summary>
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Isin { get; init; }
    public string? Wkn { get; init; }
    public string? Symbol { get; init; }
    public string AssetClassLabel { get; init; } = "";
    /// <summary>Units held. Kept as double — crypto positions are fractional to many decimals.</summary>
    public double Quantity { get; init; }
    public decimal? Price { get; init; }
    public string? PriceCurrency { get; init; }
    public decimal Value { get; init; }
    /// <summary>Fraction of the account's total value this position represents (0.25 = 25%).</summary>
    public decimal ShareOfAccount { get; init; }
    /// <summary>Total acquisition cost, or null when no purchase price is known (e.g. crypto).</summary>
    public decimal? CostBasis { get; init; }
    /// <summary>Unrealized gain/loss, or null when cost basis is unavailable.</summary>
    public decimal? Gain { get; init; }
    /// <summary>Unrealized gain as a fraction of cost basis (0.1 = +10%), or null when not computable.</summary>
    public double? GainFraction { get; init; }
}

/// <summary>
/// A security from the PortfolioPerformance catalogue offered for adding to a rebalance even
/// though it isn't currently held. Carries just enough to display, search, and join by ISIN.
/// </summary>
public class CatalogSecurity
{
    public string Isin { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Symbol { get; init; }
    public string? Wkn { get; init; }
    /// <summary>Latest quoted price per unit (for share counts on a manually-added security), or null.</summary>
    public decimal? Price { get; init; }
    public string? Currency { get; init; }
}

public class AssetClassSlice
{
    public string Label { get; init; } = "";
    public decimal Value { get; init; }
    /// <summary>Fraction of the account's total value (0..1).</summary>
    public decimal Fraction { get; init; }
}
