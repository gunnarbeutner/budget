using System.Globalization;
#pragma warning disable S1075 // the portal's address is a fixed, published URL
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenBudget.Connectors.FinTs;

/// <summary>
/// Baader Bank's customer portal (konto.baaderbank.de, "Baader Kundenportal"): the documents (Postfach) that FinTS does
/// not offer. This is Baader's own portal for its direct customers (Smartbroker+ for example), not the one of a broker
/// front-end: documents of brokers that only use Baader as custodian come from that broker's own connector.
///
/// The portal's session only works from the browser that signed in, so every portal request runs in that browser's page
/// (a web login with <see cref="WebLoginSpec.KeepBrowser"/>). Protocol (as observed in the portal):
/// <list type="bullet">
/// <item><c>GET /fstatus</c>: JSON array of the signed-in customers; anything else means no session.</item>
/// <item>Sign-in: a form at <c>/login</c>, sometimes followed by an SMS-TAN, then <c>/dashboard</c>.</item>
/// <item><c>POST /mailrows</c> with an empty multipart form: JSON <c>[{"yyyy-MM-dd": [[read, description, fileid], ...], ...}]</c>,
/// by default only the recent documents.</item>
/// <item>Older documents: a session-wide filter set by the full-page form <c>POST /mail</c> (<c>filter=1</c>, the page token
/// <c>infotok</c>, <c>timeframe=period</c>, <c>periods=720</c>, ...). The extended listing needs an SMS-TAN: until it was
/// entered, <c>/mailrows</c> answers empty and the page shows the <c>mfaInputForm</c> dialog; the TAN goes as a multipart
/// form (<c>tan</c>, <c>token</c>) to <c>POST /mfa/check</c>.</item>
/// <item><c>GET /maildownload/{fileid}</c>: a redirect to a pre-signed storage URL (another origin, no CORS) that serves
/// the PDF; the browser reads the redirect's address and the PDF is fetched from it directly, without cookies.</item>
/// </list>
/// The first download (no Baader document known yet) asks for the TAN and lists the whole filtered history; later syncs
/// use the default listing of recent documents, which needs no TAN.
/// </summary>
internal sealed partial class BaaderPortal
{
    public static readonly Uri DefaultBaseUri = new("https://konto.baaderbank.de/");
    internal const string WebLoginReference = "baader-portal-web-login";
    internal const string TanReference = "baader-portal-tan";
    internal const string IdentifierPrefix = "baader:";
    internal const string UsernameSelector = "input#user, input[name=user]";
    internal const string PasswordSelector = "input#password, input[name=password]";
    internal const string SubmitSelector = "button#ctaLogin, form#loginform button[type=submit]";

    /// <summary>The longest period the portal's filter offers, in days.</summary>
    internal const string HistoryPeriod = "720";

    internal enum DocumentTarget
    {
        Cash,
        Depot,
        Login,
    }

    private readonly IBankHttpClient _http;
    private readonly ILogger _log;
    private readonly Uri _base;
    private IBrowserFetcher? _browser;
    private bool _checked;
    private bool _filterSet;
    private bool _tanRequired;
    private bool _tanSubmitted;
    private string? _token;

    public BaaderPortal(IBankHttpClient http, ILogger log, Uri? baseUri = null)
    {
        _http = http;
        _log = log;
        _base = baseUri ?? DefaultBaseUri;
    }

    internal WebLoginSpec WebLogin(ConnectorCredentials? credentials) => new(
        new Uri(_base, "login"), [_base.Host], successUrlPrefix: new Uri(_base, "dashboard"))
    {
        // The portal calls the sign-in browser "not supported" and wants this click before showing the login form.
        // The dashboard opens before the SMS-TAN is entered; only a working status call means the session is signed in.
        SuccessCheck = "async () => { try { const r = await fetch('/fstatus', { headers: { Accept: 'application/json' }, credentials: 'same-origin' }); const j = r.ok ? await r.json() : null; return { ok: Array.isArray(j) && j.length > 0, reason: '' }; } catch { return { ok: false, reason: '' }; } }",
        Dismiss = ["button::-p-text(Risiken bewusst)", "a::-p-text(Risiken bewusst)", "input[value*=\"Risiken bewusst\"]"],
        Autofill = credentials == null ? null
            : new WebLoginAutofill(credentials.Username, credentials.Secret, UsernameSelector, PasswordSelector, SubmitSelector),
        // The portal's session only works from the browser that signed in: the requests run in its page.
        KeepBrowser = true,
        // With stored cookies the dashboard is tried first; a session that still passes the status check needs no sign-in.
        ResumeUrl = new Uri(_base, "dashboard"),
    };

    /// <summary>
    /// New documents for the wanted accounts. Without a signed-in browser it asks for a web login (which resumes a stored
    /// session without user interaction when it still works); on the first download it also asks for the SMS-TAN of the
    /// extended listing.
    /// </summary>
    public async Task<ConnectorStep<IReadOnlyList<RemoteStatement>>> FetchAsync(
        IReadOnlyList<RemoteAccount> accounts, IReadOnlySet<string> known, ConnectorCredentials? credentials, ChallengeAnswer? answer, CancellationToken ct)
    {
        if (answer?.Browser != null)
        {
            _browser = answer.Browser;
            _checked = false;
        }
        if (_browser is { IsOpen: false }) _browser = null;
        var signingIn = answer?.Challenge.Kind == ChallengeKind.WebLogin;
        if (_browser == null || (!_checked && !await SignedInAsync(_browser, ct)))
        {
            if (signingIn) throw new ConnectorException("The sign-in on the Baader portal did not complete.");
            _browser = null;
            _log.LogInformation("No signed-in Baader portal browser; asking for a sign-in.");
            return ConnectorStep.Ask<IReadOnlyList<RemoteStatement>>(ConnectorChallenge.WebLogin(
                "Sign in on the Baader portal to fetch documents.", WebLogin(credentials), WebLoginReference));
        }
        _checked = true;
        var browser = _browser;

        // The long listing (with its SMS-TAN) runs until every requested account holds documents.
        var history = !known.Any(k => k.StartsWith(IdentifierPrefix, StringComparison.Ordinal))
            || accounts.Any(a => !known.Contains(OpenBudget.Connectors.Sync.BankStatementStore.AccountHasDocumentsMarker + a.RemoteId));
        if (answer?.Challenge.Reference == TanReference)
        {
            await SubmitTanAsync(browser, answer.Value ?? "", ct);
            _tanSubmitted = true;
        }
        if (history && !_filterSet)
        {
            await SetHistoryFilterAsync(browser, ct);
            _filterSet = true;
        }

        // While the portal waits for the SMS-TAN, the document list answers like an ended session: ask for the TAN first.
        var tanPending = history && !_tanSubmitted && _tanRequired;
        List<PortalDocument> listing;
        if (tanPending) listing = [];
        else if (history && _tanSubmitted)
        {
            // A TAN the portal did not accept leaves the filter waiting, so the list still answers like an ended session.
            try { listing = await ListAsync(browser, ct); }
            catch (ConnectorException ex) when (ex.Message.Contains("ended the session", StringComparison.Ordinal))
            {
                _log.LogWarning(ex, "Baader portal: the document list still waits for a TAN after the TAN was sent; Baader probably did not accept it.");
                listing = [];
            }
        }
        else listing = await ListAsync(browser, ct);
        if (history && !_tanSubmitted && (_tanRequired || listing.Count == 0))
        {
            _log.LogInformation("Baader portal: the document history needs an SMS-TAN.");
            if (!_pageDriven) await RequestTanAsync(browser, ct);
            return ConnectorStep.Ask<IReadOnlyList<RemoteStatement>>(ConnectorChallenge.Code(
                "Enter the TAN Baader sent you by SMS (needed once to list the older documents).", numeric: true, reference: TanReference));
        }
        if (history && listing.Count == 0)
            _log.LogWarning("Baader portal: no documents listed after the TAN.");

        var cash = accounts.FirstOrDefault(a => a.Type != RemoteAccountType.Depot)?.RemoteId;
        var depot = accounts.FirstOrDefault(a => a.Type == RemoteAccountType.Depot)?.RemoteId;
        var result = new List<RemoteStatement>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int alreadyKnown = 0, unwanted = 0, notPdf = 0;
        foreach (var doc in listing)
        {
            var identifier = IdentifierPrefix + doc.Id;
            if (known.Contains(identifier) || !seen.Add(identifier)) { alreadyKnown++; continue; }
            var target = TargetOf(doc.Title);
            var account = target switch
            {
                DocumentTarget.Cash => cash,
                DocumentTarget.Depot => depot ?? cash,
                DocumentTarget.Login => null,
                _ => null,
            };
            // A cash/depot document whose account's statements are off: not wanted.
            if (target != DocumentTarget.Login && account == null) { unwanted++; continue; }
            var bytes = await DownloadAsync(browser, doc.Id, ct);
            if (bytes == null) { notPdf++; continue; }
            result.Add(new RemoteStatement(identifier, account, doc.Date, doc.Title, bytes));
        }
        _log.LogInformation("Baader portal: {Listed} document(s) listed; {Known} already known, {Unwanted} for accounts not requested or with statements off, {NotPdf} not a PDF; {Count} new.",
            listing.Count, alreadyKnown, unwanted, notPdf, result.Count);
        return ConnectorStep.Done<IReadOnlyList<RemoteStatement>>(result);
    }

    internal sealed record PortalDocument(string Id, DateOnly Date, string Title);

    /// <summary>Which account a document belongs to, from its description.</summary>
    internal static DocumentTarget TargetOf(string title)
    {
        static bool Has(string t, string s) => t.Contains(s, StringComparison.OrdinalIgnoreCase);
        if (Has(title, "Kontoauszug") || Has(title, "Zins") || Has(title, "Rechnungsabschluss")) return DocumentTarget.Cash;
        if (Has(title, "Wertpapier") || Has(title, "Abrechnung") || Has(title, "Kauf") || Has(title, "Verkauf") || Has(title, "Dividende")
            || Has(title, "Ertrag") || Has(title, "Depot") || Has(title, "Kapitalmaßnahme") || Has(title, "Ausschüttung"))
            return DocumentTarget.Depot;
        return DocumentTarget.Login;
    }

    private static readonly IReadOnlyDictionary<string, string> JsonHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["X-Requested-With"] = "XMLHttpRequest",
        ["Accept"] = "application/json, text/javascript, */*; q=0.01",
    };

    private static readonly IReadOnlyDictionary<string, string> NoFields = new Dictionary<string, string>(StringComparer.Ordinal);

    private async Task<List<PortalDocument>> ListAsync(IBrowserFetcher browser, CancellationToken ct)
    {
        var response = await browser.PostMultipartAsync(new Uri(_base, "mailrows"), NoFields, JsonHeaders, ct);
        EnsureSession(response, "listing documents");
        if (!response.IsSuccess)
            throw new ConnectorException($"The Baader portal answered HTTP {response.Status} to the document list.");
        var text = response.Text;
        JsonElement json;
        try { json = JsonElement.Parse(text); }
        catch (JsonException ex)
        {
            _log.LogWarning(ex, "The Baader portal's document list was not JSON ({Length} characters).", text.Length);
            if (text.Contains("mfaInputForm", StringComparison.Ordinal)) _tanRequired = true;
            return [];
        }
        return ParseRows(json, Shape);
    }

    private void Shape(string shape) => _log.LogInformation("Baader portal: the document list had an unexpected shape ({Shape}).", shape);

    /// <summary>
    /// Reads the documents of a <c>/mailrows</c> answer. An empty list, "no documents" or any other shape count as no
    /// documents (an unexpected shape is reported to <paramref name="unexpected"/>, by kinds only).
    /// </summary>
    internal static List<PortalDocument> ParseRows(JsonElement json, Action<string>? unexpected = null)
    {
        var docs = new List<PortalDocument>();
        if (json.ValueKind != JsonValueKind.Array)
        {
            if (json.ValueKind == JsonValueKind.Object)
                unexpected?.Invoke(string.Join(", ", json.EnumerateObject().Select(p => $"{p.Name}:{p.Value.ValueKind}")));
            else unexpected?.Invoke(json.ValueKind.ToString());
            return docs;
        }
        foreach (var group in json.EnumerateArray())
        {
            if (group.ValueKind != JsonValueKind.Object) continue;
            foreach (var day in group.EnumerateObject())
            {
                if (!DateOnly.TryParseExact(day.Name, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date)
                    || day.Value.ValueKind != JsonValueKind.Array) continue;
                foreach (var row in day.Value.EnumerateArray())
                {
                    // [read flag, description, file id]
                    if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() < 3) continue;
                    var title = row[1].ValueKind == JsonValueKind.String ? row[1].GetString() : null;
                    var id = row[2].ValueKind == JsonValueKind.String ? row[2].GetString() : null;
                    if (id == null || !DocumentId().IsMatch(id)) continue;
                    docs.Add(new PortalDocument(id, date, string.IsNullOrWhiteSpace(title) ? "Baader document" : title.Trim()));
                }
            }
        }
        return docs;
    }

    /// <summary>The fields of the mail filter form for the longest period (read and unread documents).</summary>
    internal static IEnumerable<KeyValuePair<string, string>> HistoryFilterFields(string? token)
    {
        yield return new("filter", "1");
        if (token != null) yield return new("token", token);
        yield return new("timeframe", "period");
        yield return new("periods", HistoryPeriod);
        yield return new("from", "");
        yield return new("until", "");
        yield return new("isin", "");
        yield return new("wkn", "");
        yield return new("portfolio", "");
    }

    /// <summary>Sets the portal's session filter to the longest period, as the mail page's filter form does.</summary>
    // Sets the 720-day filter through the mail page's own form, so the portal's script opens its TAN dialog and sends the SMS.
    private const string SubmitFilterScript = """
        (days) => {
          const f = document.getElementById('formFilterMail');
          if (!f) return { ok: false, reason: 'no filter form' };
          const period = f.querySelector('input[name=timeframe][value=period]');
          if (period) period.checked = true;
          if (f.elements['periods']) f.elements['periods'].value = days;
          const unread = f.querySelector('input[name=unread]');
          if (unread) unread.checked = false;
          const button = document.getElementById('submitFilterMail');
          setTimeout(() => f.requestSubmit ? f.requestSubmit(button || undefined) : f.submit(), 50);
          return { ok: true, reason: '' };
        }
        """;

    private const string TanDialogScript = """
        () => { const f = document.getElementById('mfaInputForm'); return { ok: !!f && f.offsetParent !== null, reason: f ? 'present' : 'none' }; }
        """;

    // Enters the TAN into the page's own dialog and submits it, the way a person would.
    private const string EnterTanScript = """
        (tan) => {
          const f = document.getElementById('mfaInputForm');
          const input = f && f.querySelector('input[name=tan]');
          if (!input) return { ok: false, reason: 'no TAN field' };
          const set = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value').set;
          set.call(input, tan);
          input.dispatchEvent(new Event('input', { bubbles: true }));
          input.dispatchEvent(new Event('keyup', { bubbles: true }));
          const button = f.querySelector('button[type=submit], input[type=submit], button:not([type])');
          setTimeout(() => button ? button.click() : (f.requestSubmit ? f.requestSubmit() : f.submit()), 50);
          return { ok: true, reason: '' };
        }
        """;

    private bool _pageDriven;

    /// <summary>Drives the mail page itself: true when the filter went through the page's form (the page then handles the TAN).</summary>
    private async Task<bool> SetHistoryFilterInPageAsync(IBrowserFetcher browser, CancellationToken ct)
    {
        await browser.NavigateAsync(new Uri(_base, "mail"), ct);
        await Task.Delay(TimeSpan.FromSeconds(2), ct);
        var (submitted, reason) = await browser.RunAsync(SubmitFilterScript, HistoryPeriod.ToString(CultureInfo.InvariantCulture), ct);
        if (!submitted)
        {
            _log.LogInformation("Baader portal: the mail page's filter could not be used ({Reason}).", reason);
            return false;
        }
        await Task.Delay(TimeSpan.FromSeconds(5), ct);
        var (dialog, _) = await browser.RunAsync(TanDialogScript, "", ct);
        _tanRequired = dialog;
        _log.LogInformation("Baader portal: document filter set in the page ({Period} days){Mfa}.", HistoryPeriod, dialog ? ", TAN dialog shown" : "");
        return true;
    }

    private async Task SetHistoryFilterAsync(IBrowserFetcher browser, CancellationToken ct)
    {
        if (await SetHistoryFilterInPageAsync(browser, ct)) { _pageDriven = true; return; }
        _token ??= await TokenAsync(browser, ct);
        var response = await browser.PostFormAsync(new Uri(_base, "mail"), HistoryFilterFields(_token),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Accept"] = "text/html" }, ct);
        EnsureSession(response, "setting the document filter");
        var html = response.Text;
        if (html.Contains("mfaInputForm", StringComparison.Ordinal)) _tanRequired = true;
        if (InfoToken().Match(html) is { Success: true } m) _token = TokenOf(m);
        _log.LogInformation("Baader portal: document filter set ({Period} days): HTTP {Status}{Mfa}.", HistoryPeriod, response.Status, _tanRequired ? ", TAN dialog shown" : "");
    }

    /// <summary>
    /// Has the portal send the SMS-TAN: the page's TAN dialog opens with a <c>/mfa/check</c> call that carries no TAN,
    /// which is what triggers the SMS.
    /// </summary>
    private async Task RequestTanAsync(IBrowserFetcher browser, CancellationToken ct)
    {
        var response = await browser.PostMultipartAsync(new Uri(_base, "mfa/check"), NoFields, JsonHeaders, ct);
        _log.LogInformation("Baader portal: TAN requested (HTTP {Status}).", response.Status);
    }

    private async Task SubmitTanAsync(IBrowserFetcher browser, string tan, CancellationToken ct)
    {
        var tanValue = tan.Trim();
        if (tanValue.Length == 0) throw new ConnectorException("No TAN was entered for the Baader portal.");
        if (_pageDriven)
        {
            var (entered, reason) = await browser.RunAsync(EnterTanScript, tanValue, ct);
            _log.LogInformation("Baader portal: TAN entered in the page{Reason}.", entered ? "" : $" failed ({reason})");
            if (entered)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                return;
            }
        }
        _token ??= await TokenAsync(browser, ct);
        var fields = new Dictionary<string, string>(StringComparer.Ordinal) { ["tan"] = tanValue };
        if (_token != null) fields["token"] = _token;
        var response = await browser.PostMultipartAsync(new Uri(_base, "mfa/check"), fields, JsonHeaders, ct);
        EnsureSession(response, "checking the TAN");
        string shape;
        try
        {
            var json = JsonElement.Parse(response.Text);
            shape = json.ValueKind == JsonValueKind.Object ? string.Join(", ", json.EnumerateObject().Select(p => $"{p.Name}:{p.Value.ValueKind}")) : json.ValueKind.ToString();
        }
        catch (JsonException) { shape = "not JSON"; }
        _log.LogInformation("Baader portal: TAN check answered HTTP {Status} ({Shape}).", response.Status, shape);
        if (response.Status is 400 or 401 or 403 or 422)
            throw new ConnectorException("The Baader portal rejected the TAN.");
    }

    /// <summary>
    /// The document's PDF: the browser reads the download's redirect to the pre-signed storage URL (without following it)
    /// and the PDF is fetched from there at once, without cookies. Only the storage host is logged, never the URL.
    /// </summary>
    private async Task<byte[]?> DownloadAsync(IBrowserFetcher browser, string id, CancellationToken ct)
    {
        var url = new Uri(_base, "maildownload/" + id);
        var target = await browser.ResolveRedirectAsync(url, ct);
        byte[] bytes;
        if (target == null)
        {
            // no redirect: the portal answered itself (an error page, or the file)
            var direct = await browser.GetAsync(url, "application/pdf, */*", ct);
            EnsureSession(direct, "downloading documents");
            if (!direct.IsSuccess)
            {
                _log.LogWarning("Baader document download: no storage link (HTTP {Status}); it is retried at the next sync.", direct.Status);
                return null;
            }
            bytes = direct.Body;
        }
        else
        {
            if (IsSessionEnd(target))
                throw new ConnectorException("The Baader portal ended the session while downloading documents; the next sync signs in again.");
            if (target.Scheme != Uri.UriSchemeHttps && !target.IsLoopback)
            {
                _log.LogWarning("Baader document download: the storage link on {Host} is not https; skipped.", target.Host);
                return null;
            }
            using var request = new HttpRequestMessage(HttpMethod.Get, target);
            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
            {
                _log.LogWarning("Baader document download from {Host} failed: HTTP {Status}; it is retried at the next sync.", target.Host, (int)response.StatusCode);
                return null;
            }
            bytes = await response.Content.ReadAsByteArrayAsync(ct);
        }
        if (bytes.Length < 5 || bytes[0] != '%' || bytes[1] != 'P' || bytes[2] != 'D' || bytes[3] != 'F')
        {
            _log.LogWarning("Baader document download did not return a PDF; it is retried at the next sync.");
            return null;
        }
        return bytes;
    }

    /// <summary>True when the page's status call lists at least one customer (values are never logged).</summary>
    private async Task<bool> SignedInAsync(IBrowserFetcher browser, CancellationToken ct)
    {
        var response = await browser.GetAsync(new Uri(_base, "fstatus"), "application/json", ct);
        if (!response.IsSuccess || (response.Url != null && IsSessionEnd(response.Url)))
        {
            _log.LogInformation("Baader portal status: HTTP {Status} on {Path}; no session.", response.Status, response.Url?.AbsolutePath);
            return false;
        }
        try
        {
            var json = JsonElement.Parse(response.Text);
            var ok = json.ValueKind == JsonValueKind.Array && json.EnumerateArray().Any(c => c.ValueKind == JsonValueKind.Object && c.TryGetProperty("customer_id", out _));
            if (!ok) _log.LogInformation("Baader portal status: unexpected answer shape ({Kind}).", json.ValueKind);
            return ok;
        }
        catch (JsonException ex)
        {
            _log.LogInformation(ex, "Baader portal status: {Path} answered {Type}, not JSON.", response.Url?.AbsolutePath, response.ContentType);
            return false;
        }
    }

    /// <summary>The page token (<c>&lt;meta name="infotok"&gt;</c>) of the mail page (or the dashboard).</summary>
    private async Task<string?> TokenAsync(IBrowserFetcher browser, CancellationToken ct)
    {
        foreach (var page in new[] { "mail", "dashboard" })
        {
            var response = await browser.GetAsync(new Uri(_base, page), "text/html", ct);
            if (!response.IsSuccess) continue;
            var html = response.Text;
            if (html.Contains("mfaInputForm", StringComparison.Ordinal)) _tanRequired = true;
            if (InfoToken().Match(html) is { Success: true } m) return TokenOf(m);
        }
        _log.LogInformation("Baader portal: no page token found.");
        return null;
    }

    private static string TokenOf(Match m) => m.Groups["a"].Success ? m.Groups["a"].Value : m.Groups["b"].Value;

    private void EnsureSession(BrowserFetchResponse response, string what)
    {
        if (response.Status is 401 or 403 || (response.Url != null && IsSessionEnd(response.Url)))
            throw new ConnectorException($"The Baader portal ended the session while {what}; the next sync signs in again.");
    }

    /// <summary>The sign-in page, or the start page the portal sends a session it does not accept to.</summary>
    private bool IsSessionEnd(Uri uri) =>
        string.Equals(uri.Host, _base.Host, StringComparison.OrdinalIgnoreCase)
        && (uri.AbsolutePath.StartsWith("/login", StringComparison.OrdinalIgnoreCase)
            || uri.AbsolutePath == "/"
            || uri.Query.Contains("page=login", StringComparison.OrdinalIgnoreCase));

    [GeneratedRegex("^[A-Za-z0-9_-]{1,128}$", RegexOptions.None, 1000)]
    private static partial Regex DocumentId();

    [GeneratedRegex("<meta\\s+(?:content=\"(?<a>[^\"]*)\"\\s+name=\"infotok\"|name=\"infotok\"\\s+content=\"(?<b>[^\"]*)\")", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex InfoToken();
}
