#pragma warning disable S1075 // the banks' FinTS endpoints are fixed, published addresses
#pragma warning disable MA0026, S1135 // the TODO notes below are deliberate pointers to planned work

namespace OpenBudget.Connectors.FinTs;

/// <summary>
/// A bank reachable over FinTS 3.0 PIN/TAN. All protocol work lives in <see cref="FinTsClient"/>; a bank is
/// just its <see cref="FinTsBankConfig"/>. Calls that may need the user (TAN method choice, TAN, app approval)
/// run as coroutines so the app's "call again with the answer" contract maps onto straight-line protocol code.
/// </summary>
public class FinTsConnector : IBankConnector
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

    protected FinTsConnector(ConnectorContext context, FinTsBankConfig config, FinTsOptions options, Func<ConnectorContext, IFinTsTransport>? transportFactory = null)
    {
        _context = context;
        _config = config;
        _options = options;
        _transportFactory = transportFactory ?? (ctx => new FinTsHttpTransport(ctx.Http, config.Url));
    }

    public string ConnectorType => _config.ConnectorType;

    protected static ConnectorRegistration CreateRegistration(FinTsBankConfig config, Func<ConnectorContext, IBankConnector> create) =>
        new(config.ConnectorType, config.DisplayName, config.Description, create, Statements: config.Statements);

    /// <summary>A registration for any configured bank (the thin bank classes use their own).</summary>
    public static ConnectorRegistration Registration(FinTsBankConfig config, FinTsOptions options, Func<ConnectorContext, IFinTsTransport>? transportFactory = null) =>
        CreateRegistration(config, ctx => new FinTsConnector(ctx, config, options, transportFactory));

    public Task<ConnectorStep<ConnectorSession?>> LoginAsync(
        ConnectorCredentials credentials, ConnectorSession? storedSession, ChallengeAnswer? answer, CancellationToken ct)
    {
        if (answer == null || _login == null)
        {
            if (!_options.IsConfigured)
                return Task.FromResult(ConnectorStep.Fail<ConnectorSession?>(FinTsOptions.NotConfiguredMessage));
            var client = new FinTsClient(_transportFactory(_context), _config, _options, credentials, storedSession, _context.Clock, _context.Logger);
            _client = client;
            _login = new ChallengeCoroutine<ConnectorSession?>(async (ask, token) =>
            {
                await client.LoginAsync(ask, token);
                return client.Session;
            });
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
            _refresh = new ChallengeCoroutine<AccountRefresh>((ask, token) => client.RefreshAsync(account, since, ask, token));
        }
        return _refresh.ResumeAsync(answer, ct);
    }

    public Task<ConnectorStep<IReadOnlyList<RemoteStatement>>> FetchStatementsAsync(
        IReadOnlyList<RemoteAccount> accounts, IReadOnlySet<string> knownIdentifiers, ChallengeAnswer? answer, CancellationToken ct)
    {
        var client = Client();
        if (answer == null || _statements == null)
        {
            _statements = new ChallengeCoroutine<IReadOnlyList<RemoteStatement>>(async (ask, token) =>
                await client.FetchStatementsAsync(accounts, knownIdentifiers, ask, token));
        }
        return _statements.ResumeAsync(answer, ct);
    }

    public async Task<ConnectorSession?> LogoutAsync(CancellationToken ct)
    {
        if (_client == null) return null;
        await _client.EndAsync(ct);
        return _client.ExportSession();
    }

    private FinTsClient Client() => _client ?? throw new ConnectorException("Not logged in.");
}

/// <summary>ING (Germany), FinTS part: Girokonto and Extra-Konto.</summary>
public sealed class IngConnector : FinTsConnector
{
    public const string TypeName = "ing";

    // TODO: ING's VL-Spar contracts and the Postbox (statements) are not reachable over FinTS; they need a separate
    // web part. Until that exists this connector deliberately offers neither.
    public static FinTsBankConfig Config { get; } = new(
        TypeName,
        "ING",
        "ING Germany over FinTS: Girokonto and Extra-Konto (balance and transactions). Username is the ING access key (Zugangsnummer), PIN the ING PIN. "
        + "VL-Spar and the Postbox are not supported yet.",
        "50010517",
        new Uri("https://fints.ing.de/fints/"),
        FallbackBic: "INGDDEFFXXX",
        Statements: false);

    public IngConnector(ConnectorContext context, FinTsOptions options, Func<ConnectorContext, IFinTsTransport>? transportFactory = null)
        : base(context, Config, options, transportFactory)
    {
    }

    public static ConnectorRegistration Registration(FinTsOptions options, Func<ConnectorContext, IFinTsTransport>? transportFactory = null) =>
        CreateRegistration(Config, ctx => new IngConnector(ctx, options, transportFactory));
}

/// <summary>Baader Bank, FinTS part: the cash account and the depot (positions via HKWPD).</summary>
public sealed class BaaderConnector : FinTsConnector
{
    public const string TypeName = "baader";

    // TODO: statements (Dokumentenarchiv) come later through the bank's web portal.
    public static FinTsBankConfig Config { get; } = new(
        TypeName,
        "Baader Bank",
        "Baader Bank over FinTS: cash account (balance, transactions) and depot (positions). Statements are not supported yet.",
        "70033100",
        new Uri("https://fints.baaderbank.de"),
        FallbackBic: "BDWBDEMMXXX",
        Statements: false);

    public BaaderConnector(ConnectorContext context, FinTsOptions options, Func<ConnectorContext, IFinTsTransport>? transportFactory = null)
        : base(context, Config, options, transportFactory)
    {
    }

    public static ConnectorRegistration Registration(FinTsOptions options, Func<ConnectorContext, IFinTsTransport>? transportFactory = null) =>
        CreateRegistration(Config, ctx => new BaaderConnector(ctx, options, transportFactory));
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
        UpdInventory: true);

    public PsdNuernbergConnector(ConnectorContext context, FinTsOptions options, Func<ConnectorContext, IFinTsTransport>? transportFactory = null)
        : base(context, Config, options, transportFactory)
    {
    }

    public static ConnectorRegistration Registration(FinTsOptions options, Func<ConnectorContext, IFinTsTransport>? transportFactory = null) =>
        CreateRegistration(Config, ctx => new PsdNuernbergConnector(ctx, options, transportFactory));
}
