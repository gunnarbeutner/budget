namespace OpenBudget.Connectors;

/// <summary>Which credentials a connector needs the user to store.</summary>
public enum CredentialRequirement
{
    /// <summary>Username and PIN/password (the default).</summary>
    UsernameAndSecret,

    /// <summary>Nothing: the user signs in on the bank's own page (a web login) each time the session is gone.</summary>
    None,
}

/// <summary>What a connector can do in a background run, without the owner.</summary>
public enum SilentCapability
{
    /// <summary>Background syncs are not offered.</summary>
    None,

    /// <summary>Only while the bank keeps the stored session (cookies, tokens); otherwise the login needs a manual sync.</summary>
    SessionOnly,

    /// <summary>Signs in by itself (API key, stored second factor, an SCA exemption the bank grants).</summary>
    Full,
}

/// <summary>One connector type the app can offer: its stable type string, labels and factory.</summary>
public sealed record ConnectorRegistration(
    string ConnectorType,
    string DisplayName,
    string Description,
    Func<ConnectorContext, IBankConnector> Create,
    CredentialRequirement Credentials = CredentialRequirement.UsernameAndSecret,
    bool Statements = true)
{
    /// <summary>Further credential values beyond username and secret (stored encrypted with them), shown in the login dialogs.</summary>
    public IReadOnlyList<CredentialField> ExtraCredentials { get; init; } = [];

    /// <summary>What background syncs can do for this connector (<see cref="SilentFor"/> refines it per login).</summary>
    public SilentCapability Silent { get; init; } = SilentCapability.None;

    /// <summary>The capability for a login's stored credentials (a stored TOTP secret, say); null: <see cref="Silent"/>.</summary>
    public Func<ConnectorCredentials?, SilentCapability>? SilentFor { get; init; }

    /// <summary>Whether a background run may sign in on the bank's web page with the stored credentials (only with <see cref="SilentCapability.Full"/>).</summary>
    public bool SilentWebSignIn { get; init; }

    /// <summary>An order source (<see cref="IOrderSource"/>): its accounts are payment instruments mapped to existing accounts.</summary>
    public bool OrderSource { get; init; }

    /// <summary>The shop of a receipt source (<see cref="LidlPlus.IReceiptSource"/>): no accounts; its receipts are matched to the owner's bookings.</summary>
    public string? ReceiptShop { get; init; }

    /// <summary>The German bank code of a FinTS bank: background FinTS runs are switched on per bank.</summary>
    public string? FinTsBlz { get; init; }

    public SilentCapability SilentCapabilityFor(ConnectorCredentials? credentials) => SilentFor?.Invoke(credentials) ?? Silent;
}
// Statements: whether the connector can fetch documents (statements) at all; false greys the switch out.

/// <summary>
/// An extra credential value a connector accepts (a TOTP secret, say), stored encrypted like the password and never
/// shown again once saved. <paramref name="Normalize"/> checks a non-empty entry and returns what is stored; it throws
/// <see cref="FormatException"/> (with a message that does not contain the value) when the entry is unusable.
/// </summary>
/// <param name="Key">Stable key in the stored credentials and in <see cref="ConnectorCredentials.Extra"/>.</param>
/// <param name="Label">English label; translated as "CredentialField_&lt;type&gt;_&lt;key&gt;" when available.</param>
public sealed record CredentialField(string Key, string Label, bool Optional = true, Func<string, string>? Normalize = null)
{
    /// <summary>The value to store for an entry: trimmed and checked.</summary>
    public string Accept(string value) => Normalize == null ? value.Trim() : Normalize(value);
}

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
