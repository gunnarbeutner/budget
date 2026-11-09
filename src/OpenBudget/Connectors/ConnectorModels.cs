namespace OpenBudget.Connectors;

/// <summary>Kinds of account a bank can list.</summary>
public enum RemoteAccountType
{
    Giro,
    Savings,
    CreditCard,
    Depot,
    Other,
}

/// <summary>An account as the bank describes it. <see cref="RemoteId"/> is stable across logins.</summary>
public sealed record RemoteAccount(string RemoteId, string Name, RemoteAccountType Type, string? Iban, string Currency);

/// <summary>
/// One booking (or pending reservation) reported by a bank. Amounts are decimals in the account
/// currency (negative = money out); the app converts them to cents when storing.
/// </summary>
public sealed record RemoteTransaction(
    decimal Amount,
    string Currency,
    DateOnly BookingDate,
    DateOnly? ValueDate,
    string? Name,
    string? Purpose,
    bool Booked,
    string? BankTransactionId = null);

/// <summary>A depot position with its current price.</summary>
public sealed record RemotePosition(
    string Name,
    string? Isin,
    string? Wkn,
    string? Symbol,
    decimal Quantity,
    decimal? Price,
    string? PriceCurrency,
    decimal? Value,
    decimal? PurchasePrice = null);

/// <summary>What a refresh of one account yields. Positions are only filled for depots.</summary>
public sealed record AccountRefresh(
    decimal? Balance,
    IReadOnlyList<RemoteTransaction> Transactions,
    IReadOnlyList<RemotePosition> Positions);

/// <summary>A document (usually a PDF) the bank published, for an account or (null account) for the login as a whole.</summary>
public sealed record RemoteStatement(string Identifier, string? AccountRemoteId, DateOnly Date, string Name, byte[] Content);

/// <summary>The decrypted credentials of a bank login. Never log or persist this unencrypted.</summary>
public sealed class ConnectorCredentials
{
    public ConnectorCredentials(string username, string secret)
    {
        Username = username;
        Secret = secret;
    }

    public string Username { get; }

    /// <summary>PIN or password.</summary>
    public string Secret { get; }

    public override string ToString() => "ConnectorCredentials { Username = ***, Secret = *** }";
}

/// <summary>A cookie of the connector's HTTP session, in a form that survives serialization.</summary>
public sealed record CookieState(string Name, string Value, string Domain, string Path, DateTime? ExpiresUtc, bool Secure, bool HttpOnly);

/// <summary>
/// Everything a connector wants to remember between syncs (bank-side session tokens, device ids,
/// ...) plus the HTTP cookie jar. The app stores it encrypted and hands it back at the next login.
/// </summary>
public sealed class ConnectorSession
{
    public Dictionary<string, string> Values { get; set; } = new(StringComparer.Ordinal);

    public List<CookieState> Cookies { get; set; } = [];
}
