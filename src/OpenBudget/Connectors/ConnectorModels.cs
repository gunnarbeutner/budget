namespace OpenBudget.Connectors;

/// <summary>Kinds of account a bank can list.</summary>
public enum RemoteAccountType
{
    Giro,
    Savings,
    CreditCard,
    Depot,
    Other,
    /// <summary>A loan or credit line; its balance is negative while money is owed.</summary>
    Loan,
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
    string? BankTransactionId = null,
    string? CounterpartyIban = null,
    string? CounterpartyBic = null,
    string? BookingText = null,
    SepaDetails? Sepa = null);

/// <summary>SEPA / bank detail fields of a booking, when the source reports them.</summary>
public sealed record SepaDetails(
    string? EndToEndReference = null,
    string? MandateReference = null,
    string? CreditorId = null,
    string? TransactionCode = null,
    string? Primanota = null,
    string? ReturnReason = null);

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

/// <summary>
/// What a refresh of one account yields. Positions are only filled for depots. <see cref="Scheduled"/> is the bank's
/// complete list of the account's standing orders and future-dated transfers, or null when the connector does not
/// report them (the stored ones stay); <see cref="ScheduledError"/> says why reading them failed (the rest of the refresh
/// still counts).
/// </summary>
public sealed record AccountRefresh(
    decimal? Balance,
    IReadOnlyList<RemoteTransaction> Transactions,
    IReadOnlyList<RemotePosition> Positions,
    IReadOnlyList<RemoteScheduledPayment>? Scheduled = null,
    string? ScheduledError = null);

/// <summary>A standing order (recurring) or a single transfer with a future execution date.</summary>
public enum ScheduledPaymentKind
{
    /// <summary>A single transfer the bank executes on a future date.</summary>
    Scheduled = 0,

    /// <summary>A standing order (Dauerauftrag).</summary>
    Standing = 1,
}

/// <summary>The time unit of a standing order's rhythm.</summary>
public enum ScheduleUnit
{
    Month = 0,
    Week = 1,
}

/// <summary>
/// A payment order the bank holds for an account. Amounts are decimals in <see cref="Currency"/>, negative for money
/// leaving the account. The rhythm fields are set for standing orders only: every <see cref="IntervalCount"/>
/// <see cref="IntervalUnit"/>s on <see cref="ExecutionDay"/> (day of month 1-31, or 97-99 for "ultimo minus 2/1/0";
/// weekday 1-7 for weeks), from <see cref="FirstDate"/> until <see cref="LastDate"/> (null: no end).
/// </summary>
public sealed record RemoteScheduledPayment(
    ScheduledPaymentKind Kind,
    string? BankOrderId,
    DateOnly NextDate,
    decimal Amount,
    string Currency,
    string? CounterpartyName,
    string? CounterpartyIban,
    string? CounterpartyBic,
    string? Purpose,
    DateOnly? FirstDate = null,
    DateOnly? LastDate = null,
    ScheduleUnit? IntervalUnit = null,
    int? IntervalCount = null,
    int? ExecutionDay = null);

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
public sealed record CookieState(
    string Name, string Value, string Domain, string Path, DateTime? ExpiresUtc, bool Secure, bool HttpOnly, string? SameSite = null)
{
    /// <summary>Never print the value (this record may end up in a log line by accident).</summary>
    public override string ToString() => $"CookieState {{ Name = {Name}, Domain = {Domain}, Value = *** }}";
}

/// <summary>
/// Everything a connector wants to remember between syncs (bank-side session tokens, device ids,
/// ...) plus the HTTP cookie jar. The app stores it encrypted and hands it back at the next login.
/// </summary>
public sealed class ConnectorSession
{
    public Dictionary<string, string> Values { get; set; } = new(StringComparer.Ordinal);

    public List<CookieState> Cookies { get; set; } = [];

    /// <summary>The browser identity associated with the saved cookies, also used by subsequent HTTPS requests.</summary>
    public string? UserAgent { get; set; }
}
