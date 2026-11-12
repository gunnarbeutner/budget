using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
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

    /// <summary>The reporting page's configuration and data freshness (JSON POST).</summary>
    public string ReportMetadataPath { get; init; } = "reports/apis/dlog/ql";

    /// <summary>Creates, lists and downloads reports through the web client's reporting interface (JSON POST).</summary>
    public string ReportApiPath { get; init; } = "reports/apis/common/ql";

    /// <summary>How often the report list is asked before giving up, and the wait in between.</summary>
    public int PollAttempts { get; init; } = 100;

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
/// from it). The app fills the e-mail address and password, on one page or two, and clicks Log in; a second factor (SMS
/// code, app approval, security key) is completed by the user on that page, except an authenticator-app code when the
/// login stores a TOTP secret: then the app picks the authenticator option and enters the code itself. The cookie banner is declined (only
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

    /// <summary>The optional authenticator-app secret among the login's credentials.</summary>
    internal const string TotpKey = "totp";

    /// <summary>PayPal's "authenticator app" option where the security check offers several methods.</summary>
    internal static readonly string[] AuthenticatorChoiceSelectors =
        ["input[type=radio][value*=SOFTWARE_TOKEN i]", "input[type=radio][value*=AUTHENTICATOR i]",
         "label::-p-text(Authenticator)", "label::-p-text(Authentifizierungs)"];

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

    private readonly ConnectorContext _context;
    private readonly PayPalOptions _options;
    private ConnectorSession _session = new();
    private ConnectorCredentials? _credentials;
    private IBrowserFetcher? _browser;
    private List<PayPalRow> _rows = [];
    private DateOnly? _coveredFrom;
    private ReportingSession? _reporting;

    public PayPalConnector(ConnectorContext context, PayPalOptions options)
    {
        _context = context;
        _options = options;
    }

    public static ConnectorRegistration CreateRegistration(PayPalOptions options) => new(
        TypeName,
        "PayPal",
        "PayPal personal account (paypal.com): balance per currency and payments from PayPal's activity download. Sign in with your PayPal e-mail address and password: the app fills them in on PayPal's own sign-in page, where you complete a security check (SMS code, app approval) yourself. Optionally store a TOTP secret so the authenticator code is entered automatically and syncs run unattended: in PayPal open Settings › Security › 2-step verification, add an authenticator app and choose \"Can't scan?\" to see the key (an otpauth:// link works too). Trade-off: whoever gets hold of the credentials stored on this server then has both factors of your PayPal account.",
        ctx => new PayPalConnector(ctx, options),
        CredentialRequirement.UsernameAndSecret,
        Statements: false)
    {
        ExtraCredentials = [new CredentialField(TotpKey, "TOTP secret (optional)", Normalize: NormalizeTotp)],
    };

    /// <summary>A usable secret is stored as entered (trimmed); <see cref="TotpSecret.Parse"/> reads it at sign-in.</summary>
    private static string NormalizeTotp(string value)
    {
        var trimmed = value.Trim();
        TotpSecret.Parse(trimmed);
        return trimmed;
    }

    public string ConnectorType => TypeName;

    private ILogger Log => _context.Logger;

    private DateOnly Today => DateOnly.FromDateTime(_context.Clock.GetUtcNow().UtcDateTime);

    // ---- login ----

    internal WebLoginSpec WebLogin(ConnectorCredentials? credentials) => new(
        new Uri(_options.BaseUri, "signin"), [_options.BaseUri.Host], successUrlPrefix: new Uri(_options.BaseUri, "myaccount/"))
    {
        Autofill = credentials == null ? null : new WebLoginAutofill(credentials.Username, credentials.Secret,
        [
            new WebLoginStep([
                new WebLoginField(EmailSelector, WebLoginCredential.Username),
                new WebLoginField(PasswordSelector, WebLoginCredential.Secret) { Optional = true },
            ], NextSelector + ", " + LoginSelector),
            new WebLoginStep(PasswordSelector, WebLoginCredential.Secret, LoginSelector) { SkipWhenEntered = true },
        ]),
        SuccessCheck = SignedInCheck,
        CodePrompt = new WebLoginCodePrompt("/authflow/",
            "input[autocomplete=one-time-code], input[inputmode=numeric][maxlength='1'], input[type=tel][maxlength='1']",
            "button[type=submit], input[type=submit]", "Enter the six-digit security code from PayPal or your authenticator app.")
        {
            Totp = TotpSecret.TryParse(credentials?.ExtraValue(TotpKey), out var totp) ? totp : null,
            ChooseAuthenticator = AuthenticatorChoiceSelectors,
        },
        WaitingScript = "() => ({ ok: !location.pathname.startsWith('/authflow/'), reason: 'Complete the security check on the PayPal page.' })",
        Dismiss = DismissSelectors,
        CredentialsError = new WebLoginError(CredentialsErrorSelector, CredentialsErrorTexts, CredentialsRejectedMessage),
        ObservedRequests = ["/signin", "geo.ddc.paypal.com", "ct.ddc.paypal.com"],
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
        _reporting = null;
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
        if (answer?.Browser is { IsOpen: true } fresh)
        {
            _browser = fresh;
            _reporting = null;
        }
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
        var requests = new List<RequestedReport>();
        for (var end = to; end >= from;)
        {
            var start = end.AddDays(-(_options.MaxRangeDays - 1));
            if (start < from) start = from;
            if (await PrepareReportAsync(browser, start, end, ct) is { } request) requests.Add(request);
            end = start.AddDays(-1);
        }
        var rows = new List<PayPalRow>();
        foreach (var request in requests) rows.AddRange(await DownloadAsync(browser, request, ct));
        // Rows keep their file order within a report; newer reports are listed first, so the indexes are renumbered by date.
        _rows = [.. _rows.Concat(rows).OrderBy(r => r.Date).ThenBy(r => r.Time ?? TimeOnly.MinValue).Select((r, i) => r with { Index = i })];
        _coveredFrom = from;
    }

    private Uri Url(string path) => new(_options.BaseUri, path);

    private sealed record ReportingSession(Dictionary<string, string> Headers, TimeZoneInfo TimeZone, DateTime UpdatedUntilUtc, string Template);

    private sealed record ActivityReport(ReportState State, string[] Files);

    private sealed record RequestedReport(DateOnly From, DateOnly To, ReportingSession Session, DateTime Start, DateTime End, ActivityReport? Existing);

    private async Task<ReportingSession> ReportingAsync(IBrowserFetcher browser, CancellationToken ct)
    {
        if (_reporting != null) return _reporting;
        await browser.NavigateAsync(Url(_options.ReportPage), ct);
        // The site's JavaScript consumes and removes #server-data. Read the original response, rather than the rendered DOM.
        var page = await browser.GetAsync(Url(_options.ReportPage), "text/html", ct);
        try
        {
            var source = Regex.Match(page.Text, """<script\b[^>]*\bid=["']server-data["'][^>]*>(?<json>.*?)</script\s*>""",
                RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(1));
            using var model = JsonDocument.Parse(source.Success ? source.Groups["json"].Value : "{}");
            var token = model.RootElement.Str("_csrf");
            var zone = model.RootElement.Str("locality", "timezone");
            if (!page.IsSuccess || string.IsNullOrEmpty(token) || string.IsNullOrEmpty(zone))
                throw new JsonException("Missing reporting bootstrap fields.");
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Accept"] = "application/json, text/plain, */*",
                ["Content-Type"] = "application/json",
                ["X-CSRF-Token"] = token,
                ["X-Requested-With"] = "XMLHttpRequest",
            };
            var response = await browser.SendAsync(new BrowserFetchRequest("POST", Url(_options.ReportMetadataPath))
            {
                Headers = headers,
                Body = JsonSerializer.Serialize(new { apiType = "activityDownload", isAdmin = false }),
            }, ct);
            if (!response.IsSuccess)
            {
                LogShape("reporting metadata", response);
                throw new ConnectorException("PayPal did not provide the activity download settings.");
            }
            using var metadata = JsonDocument.Parse(response.Text);
            var settings = metadata.RootElement.Path().Items().FirstOrDefault();
            var updated = settings.Str("data", "dlogRefreshInfo", "last_updated");
            if (!DateTime.TryParseExact(updated, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var pacific))
                throw new JsonException("Missing reporting freshness timestamp.");
            // PayPal's client interprets last_updated in Los Angeles, while request dates are UTC and CSV dates are account-local.
            var until = TimeZoneInfo.ConvertTimeToUtc(pacific, TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles"));
            _reporting = new ReportingSession(headers, TimeZoneInfo.FindSystemTimeZoneById(zone), until,
                settings.Str("userGroup") == "Personal" ? "DLOGCONSUMERTEMPLATE" : "DLOGTEMPLATE");
            return _reporting;
        }
        catch (Exception ex) when (ex is JsonException or TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            LogShape("activity page", page);
            throw new ConnectorException("PayPal's activity page did not provide a valid reporting session.", ex);
        }
    }

    private Task<BrowserFetchResponse> ReportRequestAsync(IBrowserFetcher browser, ReportingSession session, object body, CancellationToken ct) =>
        browser.SendAsync(new BrowserFetchRequest("POST", Url(_options.ReportApiPath)) { Headers = session.Headers, Body = JsonSerializer.Serialize(body) }, ct);

    /// <summary>Queue every missing date range before waiting, so PayPal can prepare the exports together.</summary>
    private async Task<RequestedReport?> PrepareReportAsync(IBrowserFetcher browser, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var session = await ReportingAsync(browser, ct);
        var start = TimeZoneInfo.ConvertTimeToUtc(from.ToDateTime(TimeOnly.MinValue), session.TimeZone);
        var end = TimeZoneInfo.ConvertTimeToUtc(to.ToDateTime(new TimeOnly(23, 59, 59)), session.TimeZone);
        if (end > session.UpdatedUntilUtc) end = session.UpdatedUntilUtc;
        if (start > end) return null;
        var localStart = TimeZoneInfo.ConvertTimeFromUtc(start, session.TimeZone);
        var localEnd = TimeZoneInfo.ConvertTimeFromUtc(end, session.TimeZone);
        var listBody = new { apiType = "reports", reportType = "DLOG", isAdmin = false };
        var list = await ReportRequestAsync(browser, session, listBody, ct);
        var report = FindReport(list, localStart, localEnd);
        if (report == null)
        {
            var created = await ReportRequestAsync(browser, session, new
            {
                apiType = "reportCreate",
                reportType = "DLOG",
                isAdmin = (bool?)null,
                formdata = new
                {
                    start_date = start.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture),
                    end_date = end.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture),
                    name = session.Template,
                    file_format = "CSV",
                    delivery_channel = "WEB",
                    filters = "BALANCE_IMPACTING",
                },
            }, ct);
            if (!ReportCreated(created))
            {
                LogShape("report request", created);
                throw new ConnectorException("PayPal did not accept the request for the activity download.");
            }
        }
        return new RequestedReport(from, to, session, localStart, localEnd, report);
    }

    private async Task<List<PayPalRow>> DownloadAsync(IBrowserFetcher browser, RequestedReport request, CancellationToken ct)
    {
        var (from, to, session, localStart, localEnd, existing) = request;
        var report = existing;
        var listBody = new { apiType = "reports", reportType = "DLOG", isAdmin = false };
        if (report?.State is not (ReportState.Ready or ReportState.Failed))
            _context.Progress?.Invoke($"Waiting for PayPal to prepare the activity export ({from:yyyy-MM-dd} to {to:yyyy-MM-dd})");
        for (var attempt = 0; attempt < _options.PollAttempts; attempt++)
        {
            if (report?.State is not (ReportState.Ready or ReportState.Failed))
            {
                if (attempt > 0) await Task.Delay(_options.PollDelay, _context.Clock, ct);
                var list = await ReportRequestAsync(browser, session, listBody, ct);
                report = FindReport(list, localStart, localEnd);
                if (attempt == 0 && !list.IsSuccess) LogShape("report list", list);
            }
            if (report?.State == ReportState.Failed) throw new ConnectorException("PayPal could not create the activity download.");
            if (report?.State == ReportState.Ready)
            {
                if (report.Files.Length == 0) throw new ConnectorException("PayPal's activity download did not provide a file.");
                _context.Progress?.Invoke($"Downloading PayPal activity ({from:yyyy-MM-dd} to {to:yyyy-MM-dd})");
                var rows = new List<PayPalRow>();
                // One file per request keeps large, split reports as CSV rather than asking the site to bundle them into a ZIP.
                foreach (var file in report.Files)
                {
                    var csv = await ReportRequestAsync(browser, session, new { apiType = "download", reportType = "DLOG", reportNames = new[] { file } }, ct);
                    if (!csv.IsSuccess || !PayPalCsv.LooksLikeCsv(csv.Text))
                    {
                        LogShape("report download", csv);
                        throw new ConnectorException("PayPal's activity download did not answer a CSV file.");
                    }
                    rows.AddRange(Parsed(csv.Text, from, to));
                }
                return rows;
            }
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

    private static bool ReportCreated(BrowserFetchResponse response)
    {
        if (!response.IsSuccess) return false;
        try
        {
            using var doc = JsonDocument.Parse(response.Text);
            return doc.RootElement.Path().Items().Any(item => item.Str("document_id") is { Length: > 0 });
        }
        catch (JsonException) { return false; }
    }

    private static ActivityReport? FindReport(BrowserFetchResponse response, DateTime start, DateTime end)
    {
        if (!response.IsSuccess) return null;
        try
        {
            using var doc = JsonDocument.Parse(response.Text);
            foreach (var item in doc.RootElement.Path().Items().SelectMany(scope => scope.Path("reportDetails").Items()))
            {
                if (item.Str("documentFileFormat") != "CSV" || item.Str("documentMetadata", "filters") != "BALANCE_IMPACTING"
                    || !ReportDate(item.Str("documentStartDate"), start) || !ReportDate(item.Str("documentEndDate"), end)) continue;
                var status = item.Str("status") ?? "";
                var state = status == "AVAILABLE" ? ReportState.Ready
                    : status == "FAILED" || status.EndsWith("_FAILED", StringComparison.Ordinal) ? ReportState.Failed : ReportState.Waiting;
                var files = item.Path("downloadUrls").Items().Select(file => file.Str()).Where(file => !string.IsNullOrEmpty(file)).Select(file => file!).ToArray();
                return new ActivityReport(state, files);
            }
        }
        catch (JsonException) { /* an unexpected list can still be transient while the report is being prepared */ }
        return null;
    }

    private static bool ReportDate(string? value, DateTime expected) =>
        DateTime.TryParseExact(value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) && parsed == expected;
}
