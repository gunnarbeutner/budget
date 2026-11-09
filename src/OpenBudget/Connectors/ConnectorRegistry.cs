namespace OpenBudget.Connectors;

/// <summary>One connector type the app can offer: its stable type string, labels and factory.</summary>
public sealed record ConnectorRegistration(
    string ConnectorType,
    string DisplayName,
    string Description,
    Func<ConnectorContext, IBankConnector> Create);

/// <summary>The connectors available in this installation, by type string.</summary>
public sealed class ConnectorRegistry
{
    private readonly Dictionary<string, ConnectorRegistration> _byType;

    public ConnectorRegistry(IEnumerable<ConnectorRegistration> registrations)
    {
        _byType = new Dictionary<string, ConnectorRegistration>(StringComparer.Ordinal);
        foreach (var r in registrations) _byType[r.ConnectorType] = r;
    }

    public IReadOnlyList<ConnectorRegistration> GetAvailable() => _byType.Values.OrderBy(r => r.DisplayName, StringComparer.Ordinal).ToList();

    public ConnectorRegistration? Find(string connectorType) => _byType.GetValueOrDefault(connectorType);
}
