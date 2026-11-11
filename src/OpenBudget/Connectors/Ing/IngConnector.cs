#pragma warning disable S1075 // the bank's FinTS endpoint is a fixed, published address
using System.Globalization;
using System.Text.Json;
using OpenBudget.Connectors.FinTs;

namespace OpenBudget.Connectors.Ing;

/// <summary>
/// ING (Germany): Girokonto and Extra-Konto over FinTS, plus a web banking part (<see cref="IngWebClient"/>) for what
/// FinTS does not offer: the Postbox (statements, securities and tax documents) and web-only products (VL savings
/// contracts, the Rahmenkredit credit line; see <see cref="WebProducts"/>). The web part signs in in a browser (a web
/// login that keeps the browser for the requests) with the same access number and PIN and needs a confirmation in the
/// Banking to go app, so it only runs when it is needed: once to discover web-only accounts, when a web-only account is
/// refreshed, and when statements are fetched. A failed discovery never fails the FinTS part.
/// </summary>
public sealed class IngConnector : IBankConnector, IBatchedStatements
{
    public const string TypeName = "ing";
    internal const string WebAccountsKey = "ing.web.accounts";
    internal const string WebAttemptKey = "ing.web.discoveryAttemptAt";
    internal const string DeviceKey = "ing.web.device";
    /// <summary>Which <see cref="WebProducts"/> list the remembered accounts were discovered with; a newer list discovers again once.</summary>
    internal const string WebProductsKey = "ing.web.products";
    internal const string WebProductsVersion = "2";

    public static FinTsBankConfig Config { get; } = new(
        TypeName,
        "ING",
        "ING Germany: Girokonto and Extra-Konto over FinTS, VL-Sparen, Rahmenkredit and the Postbox (statements, securities and tax documents) "
        + "over the web banking. Username is the ING username, PIN the ING PIN; web banking sign-ins are confirmed in the Banking to go app.",
        "50010517",
        new Uri("https://fints.ing.de/fints/"),
        FallbackBic: "INGDDEFFXXX",
        Statements: true,
        ProbeSecurityFunction: "900");

    private readonly ConnectorContext _context;
    private readonly FinTsConnector _fints;
    private readonly IngWebClient _web;
    private ConnectorCredentials? _credentials;
    private ConnectorSession _session = new();
    private bool _webPhase;
    private ChallengeCoroutine<bool>? _discovery;
    private ChallengeCoroutine<AccountRefresh>? _refresh;
    private string? _refreshKey;
    private ChallengeCoroutine<IReadOnlyList<RemoteStatement>>? _statements;
    private IReadOnlyList<RemoteAccount> _fintsAccounts = [];

    /// <summary>The connector; <paramref name="webBase"/> is where the web banking lives (tests use a local one), ING's own address when null.</summary>
    public IngConnector(ConnectorContext context, FinTsOptions options, Func<ConnectorContext, IFinTsTransport>? transportFactory = null, Uri? webBase = null)
    {
        _context = context;
        _fints = FinTsConnector.Create(context, Config, options, transportFactory);
        _web = new IngWebClient(webBase, context.Clock, context.Logger);
    }

    public static ConnectorRegistration Registration(FinTsOptions options, Func<ConnectorContext, IFinTsTransport>? transportFactory = null, Uri? webBase = null) =>
        new(Config.ConnectorType, Config.DisplayName, Config.Description, ctx => new IngConnector(ctx, options, transportFactory, webBase), Statements: true);

    public string ConnectorType => TypeName;

    private ILogger Log => _context.Logger;

    /// <summary>
    /// A product only the web banking offers, recognised on the overview by its product name or headline. A debt product
    /// (a credit line) is booked with a negative balance while money is owed, whatever sign the bank shows.
    /// </summary>
    private sealed record WebProduct(string Match, RemoteAccountType Type, bool Debt = false);

    private static readonly WebProduct[] WebProducts =
    [
        new("VL", RemoteAccountType.Savings),
        new("Rahmenkredit", RemoteAccountType.Loan, Debt: true),
    ];

    private static WebProduct? ProductOf(IngOverviewAccount a) =>
        WebProducts.FirstOrDefault(p => a.Product.Contains(p.Match, StringComparison.OrdinalIgnoreCase)
            || a.Headline.Contains(p.Match, StringComparison.OrdinalIgnoreCase));

    /// <summary>A remembered web-only account; <see cref="Type"/> is missing in sessions stored before it existed (then VL savings).</summary>
    private sealed record WebAccount(string Iban, string Name, RemoteAccountType? Type = null, bool Debt = false)
    {
        public RemoteAccountType Kind => Type ?? RemoteAccountType.Savings;
    }

    public async Task<ConnectorStep<ConnectorSession?>> LoginAsync(
        ConnectorCredentials credentials, ConnectorSession? storedSession, ChallengeAnswer? answer, CancellationToken ct)
    {
        if (!_webPhase)
        {
            _credentials = credentials;
            _session = storedSession ?? new ConnectorSession();
            _web.PreferredDevice = _session.Values.GetValueOrDefault(DeviceKey);
            var step = await _fints.LoginAsync(credentials, _session, answer, ct);
            if (step.Status != StepStatus.Done) return step;
            _fintsAccounts = await _fints.ListAccountsAsync(ct);
            if (!DiscoveryDue()) return ConnectorStep.Done<ConnectorSession?>(_session);
            _webPhase = true;
            answer = null;
            _discovery = new ChallengeCoroutine<bool>(async (ask, token) =>
            {
                await EnsureWebAsync(ask, token);
                return true;
            });
        }

        var web = await _discovery!.ResumeAsync(answer, ct);
        if (web.Status == StepStatus.Challenge) return ConnectorStep.Ask<ConnectorSession?>(web.Challenge!);
        if (web.Status == StepStatus.Failed)
        {
            Log.LogWarning("ING web banking sign-in for account discovery failed: {Reason}", web.FailureReason);
            _session.Values[WebAttemptKey] = _context.Clock.GetUtcNow().UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        }
        return ConnectorStep.Done<ConnectorSession?>(_session);
    }

    /// <summary>Discovery runs until it once succeeded (and again once when <see cref="WebProducts"/> grew); after a failure it waits a day before asking again.</summary>
    private bool DiscoveryDue()
    {
        if (_session.Values.ContainsKey(WebAccountsKey)
            && string.Equals(_session.Values.GetValueOrDefault(WebProductsKey), WebProductsVersion, StringComparison.Ordinal))
            return false;
        if (_session.Values.TryGetValue(WebAttemptKey, out var text)
            && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
            && _context.Clock.GetUtcNow().UtcDateTime - at.ToUniversalTime() < TimeSpan.FromDays(1))
            return false;
        return true;
    }

    /// <summary>
    /// Signs in to the web banking in a browser unless the one kept from an earlier step of this sync is still open, and
    /// refreshes the remembered web-only accounts.
    /// </summary>
    private async Task EnsureWebAsync(IChallengeAsker ask, CancellationToken ct)
    {
        if (_web.LoggedIn) return;
        var answer = await ask.AskAsync(ConnectorChallenge.WebLogin(
            "Sign in to ING's web banking and confirm the sign-in in your Banking to go app.",
            _web.WebLogin(_credentials ?? throw new ConnectorException("Not logged in.")), IngWebClient.WebLoginReference));
        if (answer.Failure != null) throw new ConnectorException(answer.Failure);
        await _web.AttachAsync(answer.Browser ?? throw new ConnectorException("The sign-in to ING's web banking did not complete."), ct);
        if (_web.PreferredDevice != null) _session.Values[DeviceKey] = _web.PreferredDevice;
        var fintsIbans = _fintsAccounts.Select(a => a.Iban).Where(i => i != null).ToHashSet(StringComparer.Ordinal);
        var found = _web.Accounts
            .Where(a => a.Iban != null && !fintsIbans.Contains(a.Iban))
            .Select(a => (Row: a, Product: ProductOf(a)))
            .Where(x => x.Product != null)
            .Select(x => new WebAccount(x.Row.Iban!, $"{x.Row.Product} ...{x.Row.Iban![^4..]}", x.Product!.Type, x.Product.Debt))
            .ToList();
        _session.Values[WebAccountsKey] = JsonSerializer.Serialize(found);
        _session.Values[WebProductsKey] = WebProductsVersion;
        _session.Values.Remove(WebAttemptKey);
        Log.LogInformation("ING web banking: {Count} web-only account(s) found ({Kinds}).", found.Count,
            string.Join(", ", found.GroupBy(f => f.Kind).Select(g => $"{g.Count()} {g.Key}")));
    }

    private List<WebAccount> WebAccounts()
    {
        if (!_session.Values.TryGetValue(WebAccountsKey, out var json)) return [];
        try { return JsonSerializer.Deserialize<List<WebAccount>>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    public async Task<IReadOnlyList<RemoteAccount>> ListAccountsAsync(CancellationToken ct)
    {
        var fints = await _fints.ListAccountsAsync(ct);
        _fintsAccounts = fints;
        var ibans = fints.Select(a => a.Iban).ToHashSet(StringComparer.Ordinal);
        return fints.Concat(WebAccounts().Where(w => !ibans.Contains(w.Iban))
                .Select(w => new RemoteAccount(w.Iban, w.Name, w.Kind, w.Iban, "EUR")))
            .ToList();
    }

    public Task<ConnectorStep<AccountRefresh>> RefreshAsync(RemoteAccount account, DateOnly? since, ChallengeAnswer? answer, CancellationToken ct)
    {
        var web = WebAccounts().FirstOrDefault(w => string.Equals(w.Iban, account.RemoteId, StringComparison.Ordinal));
        if (web == null) return _fints.RefreshAsync(account, since, answer, ct);
        if (answer == null || _refresh == null || !string.Equals(_refreshKey, account.RemoteId, StringComparison.Ordinal))
        {
            _refreshKey = account.RemoteId;
            _refresh = new ChallengeCoroutine<AccountRefresh>(async (ask, token) =>
            {
                await EnsureWebAsync(ask, token);
                var refresh = await _web.TransactionsAsync(account.RemoteId, since, token);
                return web.Debt && refresh.Balance is { } b ? refresh with { Balance = -Math.Abs(b) } : refresh;
            });
        }
        return _refresh.ResumeAsync(answer, ct);
    }

    public Task<ConnectorStep<IReadOnlyList<RemoteStatement>>> FetchStatementsAsync(
        IReadOnlyList<RemoteAccount> accounts, IReadOnlySet<string> knownIdentifiers, ChallengeAnswer? answer, CancellationToken ct)
    {
        if (answer == null || _statements == null)
            _statements = new ChallengeCoroutine<IReadOnlyList<RemoteStatement>>((ask, token) => PostboxAsync(accounts, knownIdentifiers, ask, token));
        return _statements.ResumeAsync(answer, ct);
    }

    /// <summary>
    /// Postbox documents not downloaded before. A document whose subject names the IBAN of a requested account goes to
    /// that account; one naming another account of this login is skipped (its statements are off); everything else
    /// (securities, tax, information letters, combined savings statements) belongs to the login as a whole.
    /// </summary>
    /// <summary>Documents downloaded per sync at most; the rest follow with the next syncs.</summary>
    internal const int PostboxBatch = 40;

    public bool MoreStatements { get; private set; }

    public int? StatementsTotal { get; private set; }

    /// <summary>
    /// Set once a pass over the whole Postbox got through: the IBANs whose statements were on then, newline-separated
    /// after a "v2" line (the bare "1" of older versions, whose listing ING had cut at 500 documents, counts as no
    /// pass). An account not in it gets a full pass again, so switching its statements on later brings its older
    /// documents too.
    /// </summary>
    internal const string PostboxCompleteKey = "ing.postbox.complete";

    private const string PostboxCompleteVersion = "v3"; // v2 could be set by a listing ING had cut at 500

    /// <summary>The listing of the pass in progress (reused by its later batches) and the documents that failed in it.</summary>
    private IReadOnlyList<IngWebClient.PostboxEntry>? _listing;

    private readonly HashSet<string> _failed = new(StringComparer.Ordinal);

    private async Task<IReadOnlyList<RemoteStatement>> PostboxAsync(
        IReadOnlyList<RemoteAccount> accounts, IReadOnlySet<string> known, IChallengeAsker ask, CancellationToken ct)
    {
        await EnsureWebAsync(ask, ct);
        var wanted = accounts.Where(a => a.Iban != null).ToDictionary(a => a.Iban!, a => a.RemoteId, StringComparer.Ordinal);
        var covered = _session.Values.TryGetValue(PostboxCompleteKey, out var c) && c.StartsWith(PostboxCompleteVersion, StringComparison.Ordinal)
            ? c.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToHashSet(StringComparer.Ordinal)
            : null;
        // The first pass covers the whole Postbox (years of documents) and runs over several fetches, a batch at a time,
        // reusing one listing; only once a pass got through for every account with statements on is the recent
        // listing enough.
        var full = covered == null || wanted.Keys.Any(i => !covered.Contains(i));
        if (!MoreStatements || _listing == null)
        {
            _listing = (await _web.ListPostboxAsync(full, ct)).OrderByDescending(e => e.Row.Date).ToList();
            _failed.Clear();
        }
        var entries = _listing;
        var all = (await ListAccountsAsync(ct)).Select(a => a.Iban).Where(i => i != null).ToHashSet(StringComparer.Ordinal);

        int alreadyKnown = 0, statementsOff = 0, notPdf = 0;
        var candidates = new List<(IngWebClient.PostboxEntry Entry, string? AccountId)>();
        foreach (var entry in entries)
        {
            if (known.Contains(entry.Identifier)) { alreadyKnown++; continue; }
            if (_failed.Contains(entry.Identifier)) { notPdf++; continue; } // tried in an earlier batch of this pass
            string? accountId = null;
            if (IngHtml.FindIban(entry.Row.Subject) is { } iban)
            {
                if (wanted.TryGetValue(iban, out var id)) accountId = id;
                else if (all.Contains(iban)) { statementsOff++; continue; }
            }
            candidates.Add((entry, accountId));
        }
        StatementsTotal = candidates.Count;

        var result = new List<RemoteStatement>();
        var tried = 0;
        foreach (var (entry, accountId) in candidates)
        {
            if (result.Count >= PostboxBatch) break;
            tried++;
            var bytes = await _web.DownloadAsync(entry.Download, ct);
            if (bytes == null)
            {
                notPdf++;
                _failed.Add(entry.Identifier);
                Log.LogWarning("ING Postbox: a {Type} document of {Date} did not download as a PDF; skipped.", entry.Row.Type, entry.Row.Date);
                continue;
            }
            var name = entry.Row.Subject.Length > 0 ? $"{entry.Row.Type}: {entry.Row.Subject}" : entry.Row.Type;
            result.Add(new RemoteStatement(entry.Identifier, accountId, entry.Row.Date, name, bytes));
        }
        var remaining = candidates.Count - tried;
        MoreStatements = remaining > 0;
        if (!MoreStatements)
        {
            _listing = null;
            if (full && !_web.ListingTruncated) _session.Values[PostboxCompleteKey] = string.Join('\n', wanted.Keys.Order(StringComparer.Ordinal).Prepend(PostboxCompleteVersion));
        }
        Log.LogInformation(
            "ING Postbox: {Listed} listed; {Known} already known, {Off} for accounts with statements off, {NotPdf} not a PDF; {Downloaded} downloaded, {Remaining} to follow.",
            entries.Count, alreadyKnown, statementsOff, notPdf, result.Count, remaining);
        return result;
    }

    public async Task<ConnectorSession?> LogoutAsync(CancellationToken ct)
    {
        var session = await _fints.LogoutAsync(ct) ?? _session;
        foreach (var key in new[] { WebAccountsKey, WebProductsKey, WebAttemptKey, DeviceKey })
        {
            if (_session.Values.TryGetValue(key, out var v)) session.Values[key] = v;
            else session.Values.Remove(key);
        }
        return session;
    }
}
