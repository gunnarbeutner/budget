namespace OpenBudget.Models;

/// <summary>Everything the instrument-mapping screen needs: the ISIN-less PortfolioPerformance
/// instruments to map and the ISIN-less imported holdings they can map to, for one source.</summary>
public class InstrumentMapping
{
    public List<InstrumentMappingRow> Instruments { get; init; } = new();
    public List<HoldingOption> Holdings { get; init; } = new();
}

/// <summary>One ISIN-less PortfolioPerformance instrument with its lot summary and current mapping.</summary>
public class InstrumentMappingRow
{
    public string PpUuid { get; init; } = "";
    public string PpName { get; init; } = "";
    public int LotCount { get; init; }
    public string? LinkedHoldingKey { get; init; }
}

/// <summary>A imported holding offered as a mapping target, by its identity key.</summary>
public class HoldingOption
{
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
}
