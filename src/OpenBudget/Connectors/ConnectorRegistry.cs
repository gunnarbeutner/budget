namespace OpenBudget.Connectors;

/// <summary>Which credentials a connector needs the user to store.</summary>
public enum CredentialRequirement
{
    /// <summary>Username and PIN/password (the default).</summary>
    UsernameAndSecret,

    /// <summary>Nothing: the user signs in on the bank's own page (a web login) each time the session is gone.</summary>
    None,
}

/// <summary>One connector type the app can offer: its stable type string, labels and factory.</summary>
public sealed record ConnectorRegistration(
    string ConnectorType,
    string DisplayName,
    string Description,
    Func<ConnectorContext, IBankConnector> Create,
    CredentialRequirement Credentials = CredentialRequirement.UsernameAndSecret,
    bool Statements = true);
// Statements: whether the connector can fetch documents (statements) at all; false greys the switch out.

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
