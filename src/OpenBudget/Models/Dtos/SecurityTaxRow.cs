namespace OpenBudget.Models;

/// <summary>One security on the tax-settings screen: its identity and current Teilfreistellung rate
/// (basis points), defaulted when the user hasn't overridden it.</summary>
public class SecurityTaxRow
{
    public string Isin { get; init; } = "";
    public string Name { get; init; } = "";
    public int TeilfreistellungBp { get; init; }
}
