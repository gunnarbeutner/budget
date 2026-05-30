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
}

public class HoldingRow
{
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

public class AssetClassSlice
{
    public string Label { get; init; } = "";
    public decimal Value { get; init; }
    /// <summary>Fraction of the account's total value (0..1).</summary>
    public decimal Fraction { get; init; }
}
