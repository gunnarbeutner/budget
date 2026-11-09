namespace OpenBudget.Connectors.Scalable;

/// <summary>Endpoints of the Scalable connector (overridable for tests).</summary>
public sealed class ScalableOptions
{
    public Uri BaseUri { get; init; } = new("https://de.scalable.capital/");
}
