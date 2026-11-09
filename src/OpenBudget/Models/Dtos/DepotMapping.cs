namespace OpenBudget.Models;

/// <summary>Everything the depot-mapping screen needs: the PortfolioPerformance depots to map and
/// the imported depot accounts they can map to, for one source.</summary>
public class DepotMapping
{
    public List<DepotMappingRow> Depots { get; init; } = new();
    public List<DepotAccountOption> Accounts { get; init; } = new();
}

/// <summary>One PortfolioPerformance depot on the mapping screen, with its reconstructed-lot summary
/// and the imported account it currently maps to (if any).</summary>
public class DepotMappingRow
{
    public string PpUuid { get; init; } = "";
    public string PpName { get; init; } = "";
    public int LotCount { get; init; }
    public int IsinCount { get; init; }
    public long? LinkedAccountId { get; init; }
}

/// <summary>A imported depot account offered as a mapping target.</summary>
public class DepotAccountOption
{
    public long Id { get; init; }
    public string Name { get; init; } = "";
}
