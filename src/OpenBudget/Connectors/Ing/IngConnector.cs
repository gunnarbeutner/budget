#pragma warning disable S1075 // the bank's FinTS endpoint is a fixed, published address
using System.Globalization;
using System.Text.Json;
using OpenBudget.Connectors.FinTs;

namespace OpenBudget.Connectors.Ing;

/// <summary>
/// ING (Germany): Girokonto and Extra-Konto over FinTS, plus a web banking part (<see cref="IngWebClient"/>) for what
/// FinTS does not offer: the Postbox (statements, securities and tax documents) and VL savings contracts. The web part
/// signs in with the same access number and PIN and needs a confirmation in the Banking to go app, so it only runs
/// when it is needed: once to discover web-only accounts, when a VL account is refreshed, and when statements are
/// fetched. A failed discovery never fails the FinTS part.
/// </summary>
public sealed class IngConnector : IBankConnector
{
    public const string TypeName = "ing";
    internal const string WebAccountsKey = "ing.web.accounts";
    internal const string WebAttemptKey = "ing.web.discoveryAttemptAt";
    internal const string DeviceKey = "ing.web.device";

    public static FinTsBankConfig Config { get; } = new(
        TypeName,
        "ING",
        "ING Germany: Girokonto and Extra-Konto over FinTS, VL-Sparen and the Postbox (statements, securities and tax documents) "
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

    public IngConnector(ConnectorContext context, FinTsOptions options, Func<ConnectorContext, IFinTsTransport>? transportFactory = null)
    {
        _context = context;
        _fints = FinTsConnector.Create(context, Config, options, transportFactory);
        _web = new IngWebClient(context.Http, context.Clock, context.Logger);
    }

    public static ConnectorRegistration Registration(FinTsOptions options, Func<ConnectorContext, IFinTsTransport>? transportFactory = null) =>
        new(Config.ConnectorType, Config.DisplayName, Config.Description, ctx => new IngConnector(ctx, options, transportFactory), Statements: true);

    public string ConnectorType => TypeName;

    private ILogger Log => _context.Logger;

    private sealed record WebAccount(string Iban, string Name);

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

    /// <summary>Discovery runs until it once succeeded; after a failure it waits a day before asking again.</summary>
    private bool DiscoveryDue()
    {
        if (_session.Values.ContainsKey(WebAccountsKey)) return false;
        if (_session.Values.TryGetValue(WebAttemptKey, out var text)
            && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
            && _context.Clock.GetUtcNow().UtcDateTime - at.ToUniversalTime() < TimeSpan.FromDays(1))
            return false;
        return true;
    }

    /// <summary>Signs in to the web banking (once per sync) and refreshes the remembered web-only accounts.</summary>
    private async Task EnsureWebAsync(IChallengeAsker ask, CancellationToken ct)
    {
        if (_web.LoggedIn) return;
        await _web.LoginAsync(_credentials ?? throw new ConnectorException("Not logged in."), ask, ct);
        if (_web.PreferredDevice != null) _session.Values[DeviceKey] = _web.PreferredDevice;
        var fintsIbans = _fintsAccounts.Select(a => a.Iban).Where(i => i != null).ToHashSet(StringComparer.Ordinal);
        var vl = _web.Accounts
            .Where(a => a.Iban != null && !fintsIbans.Contains(a.Iban) && a.Product.Contains("VL", StringComparison.OrdinalIgnoreCase))
            .Select(a => new WebAccount(a.Iban!, $"{a.Product} ...{a.Iban![^4..]}"))
            .ToList();
        _session.Values[WebAccountsKey] = JsonSerializer.Serialize(vl);
        _session.Values.Remove(WebAttemptKey);
        Log.LogInformation("ING web banking: {Count} VL savings account(s) found.", vl.Count);
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
                .Select(w => new RemoteAccount(w.Iban, w.Name, RemoteAccountType.Savings, w.Iban, "EUR")))
            .ToList();
    }

    public Task<ConnectorStep<AccountRefresh>> RefreshAsync(RemoteAccount account, DateOnly? since, ChallengeAnswer? answer, CancellationToken ct)
    {
        if (!WebAccounts().Any(w => string.Equals(w.Iban, account.RemoteId, StringComparison.Ordinal)))
            return _fints.RefreshAsync(account, since, answer, ct);
        if (answer == null || _refresh == null || !string.Equals(_refreshKey, account.RemoteId, StringComparison.Ordinal))
        {
            _refreshKey = account.RemoteId;
            _refresh = new ChallengeCoroutine<AccountRefresh>(async (ask, token) =>
            {
                await EnsureWebAsync(ask, token);
                return await _web.TransactionsAsync(account.RemoteId, since, token);
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
    private async Task<IReadOnlyList<RemoteStatement>> PostboxAsync(
        IReadOnlyList<RemoteAccount> accounts, IReadOnlySet<string> known, IChallengeAsker ask, CancellationToken ct)
    {
        await EnsureWebAsync(ask, ct);
        var full = !known.Any(k => k.StartsWith(IngWebClient.PostboxIdPrefix, StringComparison.Ordinal));
        var entries = await _web.ListPostboxAsync(full, ct);
        var wanted = accounts.Where(a => a.Iban != null).ToDictionary(a => a.Iban!, a => a.RemoteId, StringComparer.Ordinal);
        var all = (await ListAccountsAsync(ct)).Select(a => a.Iban).Where(i => i != null).ToHashSet(StringComparer.Ordinal);
        var result = new List<RemoteStatement>();
        foreach (var entry in entries)
        {
            if (known.Contains(entry.Identifier)) continue;
            string? accountId = null;
            if (IngHtml.FindIban(entry.Row.Subject) is { } iban)
            {
                if (wanted.TryGetValue(iban, out var id)) accountId = id;
                else if (all.Contains(iban)) continue;
            }
            var bytes = await _web.DownloadAsync(entry.Download, ct);
            if (bytes == null)
            {
                Log.LogWarning("ING Postbox: a {Type} document of {Date} did not download as a PDF; skipped.", entry.Row.Type, entry.Row.Date);
                continue;
            }
            var name = entry.Row.Subject.Length > 0 ? $"{entry.Row.Type}: {entry.Row.Subject}" : entry.Row.Type;
            result.Add(new RemoteStatement(entry.Identifier, accountId, entry.Row.Date, name, bytes));
        }
        Log.LogInformation("ING Postbox: {Count} new document(s) downloaded.", result.Count);
        return result;
    }

    public async Task<ConnectorSession?> LogoutAsync(CancellationToken ct)
    {
        var session = await _fints.LogoutAsync(ct) ?? _session;
        foreach (var key in new[] { WebAccountsKey, WebAttemptKey, DeviceKey })
        {
            if (_session.Values.TryGetValue(key, out var v)) session.Values[key] = v;
            else session.Values.Remove(key);
        }
        return session;
    }
}
