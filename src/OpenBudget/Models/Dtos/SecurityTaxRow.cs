namespace OpenBudget.Models;

/// <summary>One instrument on the tax-settings screen: its identity, tax kind, and (for securities)
/// Teilfreistellung rate, defaulted when the user hasn't overridden them.</summary>
public class SecurityTaxRow
{
    public string Uuid { get; init; } = "";
    public string Isin { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary><see cref="InstrumentTaxKind"/> as an int.</summary>
    public int Kind { get; init; }
    public int TeilfreistellungBp { get; init; }
    public bool HasIsin => !string.IsNullOrEmpty(Isin);
}
