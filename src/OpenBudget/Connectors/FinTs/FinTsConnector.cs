#pragma warning disable S1075 // the banks' FinTS endpoints are fixed, published addresses
#pragma warning disable MA0026, S1135 // the TODO notes below are deliberate pointers to planned work

namespace OpenBudget.Connectors.FinTs;

/// <summary>
/// A bank reachable over FinTS 3.0 PIN/TAN. All protocol work lives in <see cref="FinTsClient"/>; a bank is
/// just its <see cref="FinTsBankConfig"/>. Calls that may need the user (TAN method choice, TAN, app approval)
/// run as coroutines so the app's "call again with the answer" contract maps onto straight-line protocol code.
/// </summary>
public class FinTsConnector : IBankConnector, IBackgroundRunInfo
{
    private readonly ConnectorContext _context;
    private readonly FinTsBankConfig _config;
    private readonly FinTsOptions _options;
    private readonly Func<ConnectorContext, IFinTsTransport> _transportFactory;
    private FinTsClient? _client;
    private ChallengeCoroutine<ConnectorSession?>? _login;
    private ChallengeCoroutine<AccountRefresh>? _refresh;
    private string? _refreshKey;
    private ChallengeCoroutine<IReadOnlyList<RemoteStatement>>? _statements;

    /// <summary>The credentials of the current login (for a bank's web part that signs in with the same ones).</summary>
    protected ConnectorCredentials? Credentials { get; private set; }

    protected ConnectorContext Context => _context;

    /// <summary>The login's durable session values (stored after the sync), for a bank's web part; null before the login.</summary>
    protected IDictionary<string, string>? SessionValues => _client?.Session.Values;

    protected FinTsConnector(ConnectorContext context, FinTsBankConfig config, FinTsOptions options, Func<ConnectorContext, IFinTsTransport>? transportFactory = null)
    {
        _context = context;
        _config = config;
        _options = options;
        _transportFactory = transportFactory ?? (ctx => new FinTsHttpTransport(ctx.Http, config.Url));
    }

    public string ConnectorType => _config.ConnectorType;

    protected static ConnectorRegistration CreateRegistration(FinTsBankConfig config, Func<ConnectorContext, IBankConnector> create) =>
        new(config.ConnectorType, config.DisplayName, config.Description, create, Statements: config.Statements)
        {
            Silent = SilentCapability.Full,
            FinTsBlz = config.Blz,
        };

    /// <summary>A registration for any configured bank (the thin bank classes use their own).</summary>
    public static ConnectorRegistration Registration(FinTsBankConfig config, FinTsOptions options, Func<ConnectorContext, IFinTsTransport>? transportFactory = null) =>
        CreateRegistration(config, ctx => new FinTsConnector(ctx, config, options, transportFactory));

    /// <summary>A plain FinTS connector for <paramref name="config"/>, for connectors that combine FinTS with another channel.</summary>
    internal static FinTsConnector Create(ConnectorContext context, FinTsBankConfig config, FinTsOptions options, Func<ConnectorContext, IFinTsTransport>? transportFactory = null) =>
        new(context, config, options, transportFactory);

    public Task<ConnectorStep<ConnectorSession?>> LoginAsync(
        ConnectorCredentials credentials, ConnectorSession? storedSession, ChallengeAnswer? answer, CancellationToken ct)
    {
        Credentials = credentials;
        if (answer == null || _login == null)
        {
            if (!_options.IsConfigured)
                return Task.FromResult(ConnectorStep.Fail<ConnectorSession?>(FinTsOptions.NotConfiguredMessage));
            // A background run decides before sending anything: the bank pushes a TAN request while processing the init.
            if (_context.Silent && FinTsClient.SilentBlocker(storedSession, _options.SilentScaWindow, _context.Clock.GetUtcNow().UtcDateTime) is { } blocker)
                return Task.FromResult(ConnectorStep.NeedsUser<ConnectorSession?>(blocker));
            var client = new FinTsClient(_transportFactory(_context), _config, _options, credentials, storedSession, _context.Clock, _context.Logger, _context.Silent);
            _client = client;
            _login = new ChallengeCoroutine<ConnectorSession?>(async (ask, token) =>
            {
                await client.LoginAsync(ask, token);
                return client.Session;
            }, _context.Silent);
        }
        return _login.ResumeAsync(answer, ct);
    }

    public Task<IReadOnlyList<RemoteAccount>> ListAccountsAsync(CancellationToken ct) =>
        Task.FromResult(Client().ListAccounts());

    public Task<ConnectorStep<AccountRefresh>> RefreshAsync(RemoteAccount account, DateOnly? since, ChallengeAnswer? answer, CancellationToken ct)
    {
        var client = Client();
        if (answer == null || _refresh == null || !string.Equals(_refreshKey, account.RemoteId, StringComparison.Ordinal))
        {
            _refreshKey = account.RemoteId;
            _refresh = new ChallengeCoroutine<AccountRefresh>((ask, token) => client.RefreshAsync(account, since, ask, token), _context.Silent);
        }
        return _refresh.ResumeAsync(answer, ct);
    }

    public virtual Task<ConnectorStep<IReadOnlyList<RemoteStatement>>> FetchStatementsAsync(
        IReadOnlyList<RemoteAccount> accounts, IReadOnlySet<string> knownIdentifiers, ChallengeAnswer? answer, CancellationToken ct)
    {
        var client = Client();
        if (answer == null || _statements == null)
        {
            _statements = new ChallengeCoroutine<IReadOnlyList<RemoteStatement>>(async (ask, token) =>
                await client.FetchStatementsAsync(accounts, knownIdentifiers, ask, token), _context.Silent);
        }
        return _statements.ResumeAsync(answer, ct);
    }

    public async Task<ConnectorSession?> LogoutAsync(CancellationToken ct)
    {
        if (_client == null) return null;
        await _client.EndAsync(ct);
        return _client.ExportSession();
    }

    public string? FinTsCodes => _client is { InitCodes.Count: > 0 } c ? string.Join(", ", c.InitCodes) : null;

    public bool ScaCompleted => _client?.ScaCompleted == true;

    /// <summary>After a stop in a background run the session still holds the bank's answer (TAN wanted: pause background runs).</summary>
    public ConnectorSession? SessionAfterStop => _client?.ExportSession();

    private FinTsClient Client() => _client ?? throw new ConnectorException("Not logged in.");
}

/// <summary>
/// Baader Bank: the cash account and the depot (positions via HKWPD) over FinTS; documents (statements, trade
/// confirmations, tax documents) from the bank's customer portal (<see cref="BaaderPortal"/>), which FinTS does not offer.
/// </summary>
public sealed class BaaderConnector : FinTsConnector
{
    public const string TypeName = "baader";

    public static FinTsBankConfig Config { get; } = new(
        TypeName,
        "Baader Bank",
        "Baader Bank over FinTS: cash account (balance, transactions) and depot (positions). Documents (statements, trade confirmations, "
        + "tax documents) come from the Baader customer portal (konto.baaderbank.de), signed in with the same credentials in the app's browser.",
        "70033100",
        new Uri("https://fints.baaderbank.de"),
        FallbackBic: "BDWBDEMMXXX");

    private readonly Uri? _portalUri;

    private BaaderPortal? _portal;

    public BaaderConnector(
        ConnectorContext context, FinTsOptions options, Func<ConnectorContext, IFinTsTransport>? transportFactory = null, Uri? portalUri = null)
        : base(context, Config, options, transportFactory)
    {
        _portalUri = portalUri;
    }

    public static ConnectorRegistration Registration(FinTsOptions options, Func<ConnectorContext, IFinTsTransport>? transportFactory = null) =>
        CreateRegistration(Config, ctx => new BaaderConnector(ctx, options, transportFactory));

    /// <summary>Documents come from the portal, not FinTS (Baader offers no HKEKA).</summary>
    public override Task<ConnectorStep<IReadOnlyList<RemoteStatement>>> FetchStatementsAsync(
        IReadOnlyList<RemoteAccount> accounts, IReadOnlySet<string> knownIdentifiers, ChallengeAnswer? answer, CancellationToken ct)
    {
        _portal ??= new BaaderPortal(Context.Http, Context.Logger, _portalUri, SessionValues, Context.Clock);
        return _portal.FetchAsync(accounts, knownIdentifiers, Credentials, answer, ct);
    }
}

/// <summary>PSD Bank Nürnberg (Atruvia), FinTS including the electronic statements (HKEKA).</summary>
public sealed class PsdNuernbergConnector : FinTsConnector
{
    public const string TypeName = "psd-nuernberg";

    public static FinTsBankConfig Config { get; } = new(
        TypeName,
        "PSD Bank Nürnberg",
        "PSD Bank Nürnberg over FinTS: accounts (balance, transactions) and electronic account statements as PDF.",
        "76090900",
        new Uri("https://fints2.atruvia.de/cgi-bin/hbciservlet"),
        FallbackBic: "GENODEF1P17",
        UpdInventory: true);

    public PsdNuernbergConnector(ConnectorContext context, FinTsOptions options, Func<ConnectorContext, IFinTsTransport>? transportFactory = null)
        : base(context, Config, options, transportFactory)
    {
    }

    public static ConnectorRegistration Registration(FinTsOptions options, Func<ConnectorContext, IFinTsTransport>? transportFactory = null) =>
        CreateRegistration(Config, ctx => new PsdNuernbergConnector(ctx, options, transportFactory));
}
