namespace OpenBudget.Models;

/// <summary>How a rebalance target is expressed.</summary>
public enum RebalanceTargetMode
{
    /// <summary>Target is an absolute amount per security.</summary>
    Amount,
    /// <summary>Target is a relative weight per security; the pool is split in proportion.</summary>
    Ratio
}

/// <summary>One security entering the rebalance: its key, name, and current market value.</summary>
public class RebalanceInput
{
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public decimal CurrentValue { get; init; }
    /// <summary>Per-unit price, used to round trades to whole units. Null when unknown.</summary>
    public decimal? Price { get; init; }
    /// <summary>Round trades to whole units (shares/stocks/ETFs); false for fractional assets (crypto).</summary>
    public bool WholeShares { get; init; }
}

public class RebalanceRow
{
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public decimal CurrentValue { get; init; }
    public decimal CurrentFraction { get; init; }
    public decimal TargetValue { get; init; }
    public decimal TargetFraction { get; init; }
    /// <summary>Trade to reach the target: positive = buy, negative = sell.</summary>
    public decimal Delta { get; init; }
}

/// <summary>
/// Result of a per-security rebalance: the selected securities' pool (current value + injected
/// cash), each security's target and required trade, and the totals.
/// </summary>
public class RebalancePlan
{
    public decimal Pool { get; init; }
    public decimal Cash { get; init; }
    public decimal TotalBuys { get; init; }
    public decimal TotalSells { get; init; }
    /// <summary>Pool not allocated by the targets (Amount mode only; Ratio always spends the pool).</summary>
    public decimal Leftover { get; init; }
    /// <summary>False when the targets demand more than the pool can fund (Amount mode over-allocation).</summary>
    public bool Feasible { get; init; }
    public List<RebalanceRow> Rows { get; init; } = new();
}
