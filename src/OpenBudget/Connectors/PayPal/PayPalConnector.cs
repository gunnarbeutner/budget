using System.Globalization;
using System.Text.Json;
using OpenBudget.Connectors.Scalable;

namespace OpenBudget.Connectors.PayPal;

/// <summary>Where the PayPal web site and its activity download live; tests point these at a local fake.</summary>
public sealed class PayPalOptions
{
    public Uri BaseUri { get; init; } = new("https://www.paypal.com/");

    /// <summary>The balance currency that is always listed as an account, even without activity.</summary>
    public string PrimaryCurrency { get; init; } = "EUR";

    /// <summary>The activity download page (sets up the page's CSRF token).</summary>
    public string ReportPage { get; init; } = "reports/dlog";

    /// <summary>Asks for a report over a date range (JSON POST); answers the CSV itself or the new report's id.</summary>
    public string ReportCreatePath { get; init; } = "reports/dlog/generate";

    /// <summary>The generated reports with their status (JSON).</summary>
    public string ReportListPath { get; init; } = "reports/dlog/list";

    /// <summary>A generated report's CSV; {0} is the report id.</summary>
    public string ReportDownloadPath { get; init; } = "reports/dlog/download/{0}";

    /// <summary>How often the report list is asked before giving up, and the wait in between.</summary>
    public int PollAttempts { get; init; } = 30;

    public TimeSpan PollDelay { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>The longest date range of one report; longer spans are split.</summary>
    public int MaxRangeDays { get; init; } = 365;

    /// <summary>How far back the first sync of an account reaches.</summary>
    public int HistoryYears { get; init; } = 3;

    /// <summary>How far back the account listing looks to find the balance currencies.</summary>
    public int ListingDays { get; init; } = 90;
}

/// <summary>
/// A private PayPal account over PayPal's own web site (PayPal offers no API for personal accounts).
///
/// Sign-in: PayPal's sign-in page in the app's browser, a web login that keeps the browser (PayPal's session only works
/// from it). The app types the e-mail address, clicks Next, types the password and clicks Log in; a second factor (SMS
/// code, app approval, security key) is completed by the user on that page. The cookie banner is declined (only
/// necessary cookies). The login counts as done on the signed-in account summary. With stored cookies the summary is
/// tried first, so a session PayPal still accepts needs no sign-in.
///
/// Accounts: one per balance currency (the primary currency always, others when the activity shows them), of type
/// "other". Transactions and balances come from the activity download ("Download activity", balance-affecting
/// payments as CSV) for a date range, requested and fetched through the signed-in page (see <see cref="PayPalOptions"/>
/// for the paths). The balance is the "Balance" column after the newest row of the currency. Mapping: see
/// <see cref="PayPalCsv.Map"/>.
/// </summary>
public sealed class PayPalConnector : IBankConnector
{
    public const string TypeName = "paypal";
    internal const string WebLoginReference = "paypal-web-login";
    internal const string CurrenciesKey = "currencies";

    internal const string EmailSelector = "#email, input[name=login_email]";
    internal const string NextSelector = "#btnNext";
    internal const string PasswordSelector = "#password, input[name=login_password]";
    internal const string LoginSelector = "#btnLogin";

    /// <summary>The cookie banner's decline button (necessary cookies only).</summary>
    internal static readonly string[] DismissSelectors =
        ["#bannerDeclineButton", "button::-p-text(Ablehnen)", "button::-p-text(Decline)", "button::-p-text(Reject)"];

    internal const string CredentialsErrorSelector = "[role=alert], .notification-critical, #notifications, .notifications";
    internal static readonly string[] CredentialsErrorTexts =
        ["info isn't correct", "info is not correct", "Angaben sind nicht korrekt", "Angaben stimmen nicht", "Angaben waren nicht korrekt"];
    internal const string CredentialsRejectedMessage = "PayPal rejected the e-mail address or password.";

    /// <summary>Signed in: on a /myaccount page without a sign-in or verification form.</summary>
    internal const string SignedInCheck =
        "() => { const busy = document.querySelector('input[type=password], input[name=login_email], input[autocomplete=one-time-code], form[name=login]');"
        + " return { ok: location.pathname.startsWith('/myaccount') && !busy, reason: busy ? 'a sign-in form is shown' : '' }; }";

    /// <summary>The page's CSRF token (meta tag, hidden input or the page's data), returned as the reason.</summary>
    internal const string CsrfScript =
        "() => { const m = document.querySelector('meta[name=csrf-token], meta[name=_csrf], input[name=_csrf], input[name=csrf], [data-csrf]');"
        + " const t = m ? (m.getAttribute('content') || m.value || m.getAttribute('data-csrf') || '') : '';"
        + " return { ok: t.length > 0, reason: t }; }";

    private readonly ConnectorContext _context;
    private readonly PayPalOptions _options;
    private ConnectorSession _session = new();
    private ConnectorCredentials? _credentials;
    private IBrowserFetcher? _browser;
    private List<PayPalRow> _rows = [];
    private DateOnly? _coveredFrom;

    public PayPalConnector(ConnectorContext context, PayPalOptions options)
    {
        _context = context;
        _options = options;
    }

    public static ConnectorRegistration CreateRegistration(PayPalOptions options) => new(
        TypeName,
        "PayPal",
        "PayPal personal account (paypal.com): balance per currency and payments from PayPal's activity download. Sign in with your PayPal e-mail address and password: the app fills them in on PayPal's own sign-in page, where you complete a security check (SMS code, app approval) yourself.",
        ctx => new PayPalConnector(ctx, options),
        CredentialRequirement.UsernameAndSecret,
        Statements: false);

    public string ConnectorType => TypeName;

    private ILogger Log => _context.Logger;

    private DateOnly Today => DateOnly.FromDateTime(_context.Clock.GetUtcNow().UtcDateTime);

    // ---- login ----

    internal WebLoginSpec WebLogin(ConnectorCredentials? credentials) => new(
        new Uri(_options.BaseUri, "signin"), [_options.BaseUri.Host], successUrlPrefix: new Uri(_options.BaseUri, "myaccount/"))
    {
        Autofill = credentials == null ? null : new WebLoginAutofill(credentials.Username, credentials.Secret,
        [
            new WebLoginStep(EmailSelector, WebLoginCredential.Username, NextSelector),
            new WebLoginStep(PasswordSelector, WebLoginCredential.Secret, LoginSelector),
        ]),
        SuccessCheck = SignedInCheck,
        Dismiss = DismissSelectors,
        CredentialsError = new WebLoginError(CredentialsErrorSelector, CredentialsErrorTexts, CredentialsRejectedMessage),
        KeepBrowser = true,
        ResumeUrl = new Uri(_options.BaseUri, "myaccount/summary"),
    };

    private ConnectorChallenge AskSignIn() =>
        ConnectorChallenge.WebLogin("Sign in on the PayPal page.", WebLogin(_credentials), WebLoginReference);

    public Task<ConnectorStep<ConnectorSession?>> LoginAsync(
        ConnectorCredentials credentials, ConnectorSession? storedSession, ChallengeAnswer? answer, CancellationToken ct)
    {
        _credentials = credentials;
        if (answer == null)
        {
            _session = storedSession ?? new ConnectorSession();
            return Task.FromResult(ConnectorStep.Ask<ConnectorSession?>(AskSignIn()));
        }
        if (answer.Challenge.Reference != WebLoginReference || answer.Browser is not { IsOpen: true } browser)
            return Task.FromResult(ConnectorStep.Fail<ConnectorSession?>("The PayPal sign-in did not complete; please sync again."));
        _browser = browser;
        Log.LogInformation("Signed in to PayPal through the web login.");
        return Task.FromResult(ConnectorStep.Done<ConnectorSession?>(_session));
    }

    /// <summary>The stored cookies (a remembered device spares the second factor next time).</summary>
    public Task<ConnectorSession?> LogoutAsync(CancellationToken ct) => Task.FromResult<ConnectorSession?>(_session);

    // ---- accounts ----

    public async Task<IReadOnlyList<RemoteAccount>> ListAccountsAsync(CancellationToken ct)
    {
        var browser = _browser is { IsOpen: true } b ? b : throw new ConnectorException("The PayPal session has ended; please sync again.");
        await EnsureRowsAsync(browser, Today.AddDays(-_options.ListingDays), ct);
        var currencies = new SortedSet<string>(StringComparer.Ordinal) { _options.PrimaryCurrency };
        if (_session.Values.TryGetValue(CurrenciesKey, out var known))
            currencies.UnionWith(known.Split(',', StringSplitOptions.RemoveEmptyEntries));
        // A foreign currency only counts when something in it remains after its conversions cancel out.
        foreach (var c in PayPalCsv.Currencies(_rows))
        {
            if (PayPalCsv.Map(_rows, c, null).Count > 0 || PayPalCsv.LatestBalance(_rows, c) is { } bal && bal != 0) currencies.Add(c);
        }
        _session.Values[CurrenciesKey] = string.Join(',', currencies);
        Log.LogInformation("PayPal: balance currencies {Currencies}.", string.Join(", ", currencies));
        return currencies.Select(c => new RemoteAccount("paypal-" + c, "PayPal " + c, RemoteAccountType.Other, null, c)).ToList();
    }

    // ---- balance and transactions ----

    public async Task<ConnectorStep<AccountRefresh>> RefreshAsync(RemoteAccount account, DateOnly? since, ChallengeAnswer? answer, CancellationToken ct)
    {
        if (answer?.Browser is { IsOpen: true } fresh) _browser = fresh;
        if (_browser is not { IsOpen: true } browser)
        {
            if (answer != null) return ConnectorStep.Fail<AccountRefresh>("The PayPal sign-in did not complete; please sync again.");
            Log.LogInformation("PayPal: the browser session has ended; asking for a sign-in.");
            return ConnectorStep.Ask<AccountRefresh>(AskSignIn());
        }
        var from = since ?? Today.AddYears(-_options.HistoryYears);
        await EnsureRowsAsync(browser, from, ct);
        var transactions = PayPalCsv.Map(_rows, account.Currency, from);
        var balance = PayPalCsv.LatestBalance(_rows, account.Currency);
        Log.LogInformation("PayPal {Currency}: {Count} transactions since {Since}; balance {Known}.",
            account.Currency, transactions.Count, from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), balance == null ? "unknown" : "read");
        return ConnectorStep.Done(new AccountRefresh(balance, transactions, []));
    }

    public Task<ConnectorStep<IReadOnlyList<RemoteStatement>>> FetchStatementsAsync(
        IReadOnlyList<RemoteAccount> accounts, IReadOnlySet<string> knownIdentifiers, ChallengeAnswer? answer, CancellationToken ct) =>
        Task.FromResult(ConnectorStep.Done<IReadOnlyList<RemoteStatement>>([]));

    // ---- activity download ----

    /// <summary>Makes sure the downloaded rows reach back to <paramref name="from"/>; only the missing span is fetched.</summary>
    private async Task EnsureRowsAsync(IBrowserFetcher browser, DateOnly from, CancellationToken ct)
    {
        var to = _coveredFrom?.AddDays(-1) ?? Today;
        if (from > to) return;
        var rows = new List<PayPalRow>();
        for (var end = to; end >= from;)
        {
            var start = end.AddDays(-(_options.MaxRangeDays - 1));
            if (start < from) start = from;
            rows.AddRange(await DownloadAsync(browser, start, end, ct));
            end = start.AddDays(-1);
        }
        // Rows keep their file order within a report; newer reports are listed first, so the indexes are renumbered by date.
        _rows = [.. _rows.Concat(rows).OrderBy(r => r.Date).ThenBy(r => r.Time ?? TimeOnly.MinValue).Select((r, i) => r with { Index = i })];
        _coveredFrom = from;
    }

    private Uri Url(string path) => new(_options.BaseUri, path);

    /// <summary>One report over [from, to] through the signed-in page: ask for it, wait until it is ready, fetch the CSV.</summary>
    private async Task<List<PayPalRow>> DownloadAsync(IBrowserFetcher browser, DateOnly from, DateOnly to, CancellationToken ct)
    {
        await browser.NavigateAsync(Url(_options.ReportPage), ct);
        var (hasToken, token) = await browser.RunAsync(CsrfScript, "", ct);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Accept"] = "application/json, text/csv",
            ["Content-Type"] = "application/json",
        };
        if (hasToken) headers["X-CSRF-Token"] = token;
        var body = JsonSerializer.Serialize(new
        {
            startDate = from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            endDate = to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            transactionType = "BALANCE_AFFECTING",
            fileFormat = "CSV",
            fields = "ALL",
        });
        var created = await browser.SendAsync(new BrowserFetchRequest("POST", Url(_options.ReportCreatePath)) { Headers = headers, Body = body }, ct);
        if (created.IsSuccess && PayPalCsv.LooksLikeCsv(created.Text)) return Parsed(created.Text, from, to);
        var id = created.IsSuccess ? ReportId(created.Text) : null;
        if (id == null)
        {
            LogShape("report request", created);
            throw new ConnectorException("PayPal did not accept the request for the activity download.");
        }

        for (var attempt = 0; attempt < _options.PollAttempts; attempt++)
        {
            var list = await browser.GetAsync(Url(_options.ReportListPath), "application/json", ct);
            var state = ReportStatus(list, id);
            if (state == ReportState.Failed) throw new ConnectorException("PayPal could not create the activity download.");
            if (state == ReportState.Ready)
            {
                var csv = await browser.GetAsync(Url(string.Format(CultureInfo.InvariantCulture, _options.ReportDownloadPath, Uri.EscapeDataString(id))), "text/csv", ct);
                if (csv.IsSuccess && (PayPalCsv.LooksLikeCsv(csv.Text) || csv.Text.Trim().Length == 0)) return Parsed(csv.Text, from, to);
                LogShape("report download", csv);
                throw new ConnectorException("PayPal's activity download did not answer a CSV file.");
            }
            if (attempt == 0 && !list.IsSuccess) LogShape("report list", list);
            await Task.Delay(_options.PollDelay, _context.Clock, ct);
        }
        throw new ConnectorException("PayPal's activity download was not ready in time; the next sync tries again.");
    }

    private List<PayPalRow> Parsed(string csv, DateOnly from, DateOnly to)
    {
        var rows = PayPalCsv.Parse(csv).Where(r => r.Date >= from && r.Date <= to).ToList();
        Log.LogInformation("PayPal activity download {From}..{To}: {Count} rows.",
            from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), rows.Count);
        return rows;
    }

    /// <summary>Status, content type and the top-level JSON field names of an unexpected answer; never values.</summary>
    private void LogShape(string what, BrowserFetchResponse response)
    {
        string fields = "-";
        try
        {
            using var doc = JsonDocument.Parse(response.Text);
            fields = doc.RootElement.ValueKind == JsonValueKind.Object
                ? string.Join(", ", doc.RootElement.EnumerateObject().Select(p => p.Name))
                : doc.RootElement.ValueKind.ToString();
        }
        catch (JsonException)
        {
            fields = "not JSON";
        }
        Log.LogWarning("PayPal {What}: HTTP {Status}, {Type}, {Length} bytes, fields: {Fields}.",
            what, response.Status, response.ContentType ?? "-", response.Body.Length, fields);
    }

    internal enum ReportState
    {
        Waiting,
        Ready,
        Failed,
    }

    private static readonly string[] ListFields = ["reports", "data", "items"];

    private static readonly string[] IdFields = ["reportId", "id", "requestId", "jobId"];

    /// <summary>The new report's id in the request's answer (top level or under "data"/"report").</summary>
    internal static string? ReportId(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            foreach (var scope in new[] { (JsonElement?)root, root.Path("data"), root.Path("report") })
            {
                foreach (var f in IdFields)
                {
                    if (scope.Str(f) is { Length: > 0 } id) return id;
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }
        return null;
    }

    /// <summary>The state of report <paramref name="id"/> in the report list (an array at the top or under reports/data/items).</summary>
    internal static ReportState ReportStatus(BrowserFetchResponse list, string id)
    {
        if (!list.IsSuccess) return ReportState.Waiting;
        try
        {
            using var doc = JsonDocument.Parse(list.Text);
            var root = doc.RootElement;
            var items = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToList()
                : ListFields.Select(k => root.Path(k)).FirstOrDefault(e => e is { ValueKind: JsonValueKind.Array }).Items().ToList();
            foreach (var item in items)
            {
                if (!IdFields.Any(f => item.Str(f) == id)) continue;
                var status = (item.Str("status") ?? item.Str("state") ?? "").ToUpperInvariant();
                if (status.Contains("FAIL", StringComparison.Ordinal) || status.Contains("ERROR", StringComparison.Ordinal)) return ReportState.Failed;
                if (status is "COMPLETED" or "COMPLETE" or "READY" or "AVAILABLE" or "DONE" or "SUCCESS") return ReportState.Ready;
            }
        }
        catch (JsonException)
        {
            return ReportState.Waiting;
        }
        return ReportState.Waiting;
    }
}
