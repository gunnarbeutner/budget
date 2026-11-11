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
/// <item><c>GET /fstatus</c>: JSON array of the signed-in customers (<c>customer_id</c>, ...); anything else means no session.</item>
/// <item>Sign-in: a form at <c>/login</c>, sometimes followed by an SMS-TAN, then <c>/dashboard</c>.</item>
/// <item>Two kinds of mailbox: the general one (page <c>/mail</c>, list <c>/mailrows</c>) and one per customer (page
/// <c>/mail/{customer_id}</c>, list <c>/mailrows/{customer_id}</c>). A list is a <c>POST</c> with an empty multipart form
/// answering JSON <c>[{"yyyy-MM-dd": [[read, description, fileid], ...], ...}]</c>, by default only the recent documents.</item>
/// <item>Older documents: a filter set by the mailbox page's full-page form (<c>filter=1</c>, the page token <c>infotok</c>,
/// either <c>timeframe=fromuntil</c> with <c>from</c>/<c>until</c> or <c>timeframe=period</c> with <c>periods=720</c>, ...).
/// The extended listing needs an SMS-TAN: until it was entered, the list answers empty and the page shows the
/// <c>mfaInputForm</c> dialog; the TAN goes as a multipart form (<c>tan</c>, <c>token</c>) to <c>POST /mfa/check</c>.</item>
/// <item><c>GET /maildownload/{fileid}</c>: a redirect to a pre-signed storage URL (another origin, no CORS) that serves
/// the PDF; the browser reads the redirect's address and the PDF is fetched from it directly, without cookies.</item>
/// </list>
/// A mailbox gets the long listing (a date range from the earliest date the form allows, else the longest period) while a
/// requested account holds no documents; a customer mailbox also once until that ran. Other syncs use the default listing
/// of recent documents, which needs no TAN. File ids are unique across mailboxes; a document listed twice is fetched once.
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

    /// <summary>Prefix of the login's session values that record a customer mailbox whose long listing ran (followed by the customer id).</summary>
    internal const string HistoryDoneKeyPrefix = "baader-portal:history-done:";

    /// <summary>How far back the date-range filter reaches when the page names no earliest date.</summary>
    internal const int FallbackRangeYears = 10;

    private readonly IBankHttpClient _http;
    private readonly ILogger _log;
    private readonly Uri _base;
    private readonly IDictionary<string, string>? _state;
    private readonly TimeProvider _clock;
    private readonly HashSet<string> _filtered = new(StringComparer.Ordinal);
    private readonly HashSet<string> _tanAnswered = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<PortalDocument>> _listed = new(StringComparer.Ordinal);
    private readonly HashSet<string> _historyListed = new(StringComparer.Ordinal);
    private IBrowserFetcher? _browser;
    private bool _checked;
    private bool _tanRequired;
    private bool _tanSubmitted;
    private string? _token;
    private string? _tanMailbox;
    private List<string> _customers = [];

    /// <summary>A portal client; <paramref name="state"/> is the login's durable session values (which customer mailboxes already had their long listing).</summary>
    public BaaderPortal(IBankHttpClient http, ILogger log, Uri? baseUri = null, IDictionary<string, string>? state = null, TimeProvider? clock = null)
    {
        _http = http;
        _log = log;
        _base = baseUri ?? DefaultBaseUri;
        _state = state;
        _clock = clock ?? TimeProvider.System;
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
    /// New documents for the wanted accounts, from the general mailbox (<c>/mail</c>) and each customer's own mailbox
    /// (<c>/mail/{customer_id}</c>), merged by file id. Without a signed-in browser it asks for a web login (which resumes a
    /// stored session without user interaction when it still works). A mailbox gets the long listing (filter set through
    /// its page, SMS-TAN when the page's TAN dialog opens) while a requested account holds no documents yet; a customer
    /// mailbox also until its long listing ran once (recorded in the login's session values).
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
        var accountsNeedHistory = !known.Any(k => k.StartsWith(IdentifierPrefix, StringComparison.Ordinal))
            || accounts.Any(a => !known.Contains(OpenBudget.Connectors.Sync.BankStatementStore.AccountHasDocumentsMarker + a.RemoteId));
        if (answer?.Challenge.Reference == TanReference)
        {
            await SubmitTanAsync(browser, answer.Value ?? "", ct);
            _tanSubmitted = true;
            _tanAnswered.Add(_tanMailbox ?? "");
            _tanMailbox = null;
        }

        foreach (var mailbox in Mailboxes())
        {
            if (_listed.ContainsKey(mailbox)) continue;
            var label = Label(mailbox);
            var history = accountsNeedHistory || (mailbox.Length > 0 && !HistoryDone(mailbox));
            if (history && _filtered.Add(mailbox))
            {
                _tanRequired = false;
                await SetHistoryFilterAsync(browser, mailbox, ct);
                // While the portal waits for the SMS-TAN, the document list answers like an ended session: ask for the TAN first.
                if (_tanRequired) return await AskTanAsync(browser, mailbox, ct);
            }

            List<PortalDocument> listing;
            var answered = _tanAnswered.Contains(mailbox);
            try
            {
                _tanRequired = false;
                listing = await ListAsync(browser, mailbox, ct);
            }
            catch (ConnectorException ex) when (history && answered && ex.Message.Contains("ended the session", StringComparison.Ordinal))
            {
                // A TAN the portal did not accept leaves the filter waiting, so the list still answers like an ended session.
                _log.LogWarning(ex, "Baader portal: the {Mailbox} still waits for a TAN after the TAN was sent; Baader probably did not accept it.", label);
                _listed[mailbox] = [];
                continue;
            }
            catch (ConnectorException ex) when (mailbox.Length > 0 && !ex.Message.Contains("ended the session", StringComparison.Ordinal))
            {
                // A customer mailbox the portal does not serve leaves the general one usable; it is tried again next sync.
                _log.LogWarning(ex, "Baader portal: the {Mailbox} could not be listed; skipped.", label);
                _listed[mailbox] = [];
                continue;
            }
            if (history && !answered && !_tanSubmitted && (_tanRequired || (mailbox.Length == 0 && listing.Count == 0)))
                return await AskTanAsync(browser, mailbox, ct);
            if (history && answered && listing.Count == 0)
                _log.LogWarning("Baader portal: no documents listed in the {Mailbox} after the TAN.", label);
            _listed[mailbox] = listing;
            if (history) _historyListed.Add(mailbox);
            _log.LogInformation("Baader portal: {Count} document(s) listed in the {Mailbox} ({Kind} listing).", listing.Count, label, history ? "long" : "recent");
        }

        var cash = accounts.FirstOrDefault(a => a.Type != RemoteAccountType.Depot)?.RemoteId;
        var depot = accounts.FirstOrDefault(a => a.Type == RemoteAccountType.Depot)?.RemoteId;
        var result = new List<RemoteStatement>();
        var incomplete = new HashSet<string>(StringComparer.Ordinal);
        int listed = 0, alreadyKnown = 0, unwanted = 0, notPdf = 0;
        foreach (var (mailbox, doc) in Merge(Mailboxes().Select(m => (m, (IReadOnlyList<PortalDocument>)_listed.GetValueOrDefault(m, [])))))
        {
            listed++;
            var identifier = IdentifierPrefix + doc.Id;
            if (known.Contains(identifier)) { alreadyKnown++; continue; }
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
            if (bytes == null) { notPdf++; incomplete.Add(mailbox); continue; }
            result.Add(new RemoteStatement(identifier, account, doc.Date, doc.Title, bytes));
        }
        // A customer mailbox whose long listing was read and downloaded completely does not get it again.
        if (_state != null)
        {
            foreach (var mailbox in _historyListed.Where(m => m.Length > 0 && !incomplete.Contains(m)))
                _state[HistoryDoneKeyPrefix + mailbox] = Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        _log.LogInformation("Baader portal: {Listed} document(s) listed; {Known} already known, {Unwanted} for accounts not requested or with statements off, {NotPdf} not a PDF; {Count} new.",
            listed, alreadyKnown, unwanted, notPdf, result.Count);
        return ConnectorStep.Done<IReadOnlyList<RemoteStatement>>(result);
    }

    /// <summary>The general mailbox (<c>""</c>) and the signed-in customers' own mailboxes (by customer id).</summary>
    private IEnumerable<string> Mailboxes() => _customers.Prepend("");

    /// <summary>A mailbox's name for the log, without the customer id.</summary>
    private string Label(string mailbox) => mailbox.Length == 0 ? "general mailbox" : $"customer mailbox {_customers.IndexOf(mailbox) + 1}";

    private bool HistoryDone(string mailbox) => _state?.ContainsKey(HistoryDoneKeyPrefix + mailbox) == true;

    /// <summary>The documents of several mailboxes, each file id once (from the first mailbox that lists it).</summary>
    internal static IEnumerable<(string Mailbox, PortalDocument Document)> Merge(IEnumerable<(string Mailbox, IReadOnlyList<PortalDocument> Documents)> mailboxes)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (mailbox, docs) in mailboxes)
        {
            foreach (var doc in docs)
            {
                if (ids.Add(doc.Id)) yield return (mailbox, doc);
            }
        }
    }

    private async Task<ConnectorStep<IReadOnlyList<RemoteStatement>>> AskTanAsync(IBrowserFetcher browser, string mailbox, CancellationToken ct)
    {
        _log.LogInformation("Baader portal: the long listing of the {Mailbox} needs an SMS-TAN.", Label(mailbox));
        _tanMailbox = mailbox;
        if (!_pageDriven) await RequestTanAsync(browser, ct);
        return ConnectorStep.Ask<IReadOnlyList<RemoteStatement>>(ConnectorChallenge.Code(
            "Enter the TAN Baader sent you by SMS (needed once to list the older documents).", numeric: true, reference: TanReference));
    }
    internal sealed record PortalDocument(string Id, DateOnly Date, string Title);

    /// <summary>Which account a document belongs to, from its description.</summary>
    internal static DocumentTarget TargetOf(string title)
    {
        static bool Has(string t, string s) => t.Contains(s, StringComparison.OrdinalIgnoreCase);
        if (Has(title, "Kontoauszug") || Has(title, "Zins") || Has(title, "Rechnungsabschluss")) return DocumentTarget.Cash;
        if (Has(title, "Wertpapier") || Has(title, "Abrechnung") || Has(title, "Kauf") || Has(title, "Verkauf") || Has(title, "Dividende")
            || Has(title, "Ertrag") || Has(title, "Depot") || Has(title, "Kapitalmaßnahme") || Has(title, "Ausschüttung")
            || Has(title, "Vorabpauschale") || Has(title, "Einbuchung") || Has(title, "Ausbuchung") || Has(title, "Umtausch")
            || Has(title, "Tilgung") || Has(title, "Fälligkeit") || Has(title, "Bezugsrecht") || Has(title, "Kapitalerhöhung"))
            return DocumentTarget.Depot;
        return DocumentTarget.Login;
    }

    private static readonly IReadOnlyDictionary<string, string> JsonHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["X-Requested-With"] = "XMLHttpRequest",
        ["Accept"] = "application/json, text/javascript, */*; q=0.01",
    };

    private static readonly IReadOnlyDictionary<string, string> NoFields = new Dictionary<string, string>(StringComparer.Ordinal);

    private static string MailPath(string mailbox) => mailbox.Length == 0 ? "mail" : "mail/" + mailbox;

    private static string RowsPath(string mailbox) => mailbox.Length == 0 ? "mailrows" : "mailrows/" + mailbox;

    private async Task<List<PortalDocument>> ListAsync(IBrowserFetcher browser, string mailbox, CancellationToken ct)
    {
        var response = await browser.PostMultipartAsync(new Uri(_base, RowsPath(mailbox)), NoFields, JsonHeaders, ct);
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

    /// <summary>
    /// The fields of the mail filter form (read and unread documents): the date range <paramref name="from"/> to
    /// <paramref name="until"/> when both are given, else the longest period.
    /// </summary>
    internal static IEnumerable<KeyValuePair<string, string>> HistoryFilterFields(string? token, DateOnly? from = null, DateOnly? until = null)
    {
        var range = from != null && until != null;
        yield return new("filter", "1");
        if (token != null) yield return new("token", token);
        yield return new("timeframe", range ? "fromuntil" : "period");
        yield return new("periods", HistoryPeriod);
        yield return new("from", range ? from!.Value.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) : "");
        yield return new("until", range ? until!.Value.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) : "");
        yield return new("isin", "");
        yield return new("wkn", "");
        yield return new("portfolio", "");
    }

    /// <summary>
    /// Sets the filter through the mail page's own form, so the portal's script opens its TAN dialog and sends the SMS.
    /// The argument is JSON: <c>mode</c> "range" (from the earliest date the from field allows, else <c>from</c>, until
    /// <c>until</c>; dates as yyyy-MM-dd) or "period" (<c>days</c>).
    /// </summary>
    private const string SubmitFilterScript = """
        (arg) => {
          const a = JSON.parse(arg);
          const f = document.getElementById('formFilterMail');
          if (!f) return { ok: false, reason: 'no filter form' };
          const fire = (e) => { e.dispatchEvent(new Event('input', { bubbles: true })); e.dispatchEvent(new Event('change', { bubbles: true })); };
          if (a.mode === 'range') {
            const radio = f.querySelector('input[name=timeframe][value=fromuntil]');
            const from = f.elements['from'], until = f.elements['until'];
            if (!radio || !from || !until) return { ok: false, reason: 'no date range' };
            const iso = /^\d{4}-\d{2}-\d{2}$/, de = /^\d{2}\.\d{2}\.\d{4}$/;
            const min = from.getAttribute('min') || from.dataset.min || from.dataset.minDate || from.dataset.dateStartDate || '';
            const start = iso.test(min) ? min : de.test(min) ? min.split('.').reverse().join('-') : a.from;
            const show = (d) => from.type === 'date' ? d : d.split('-').reverse().join('.');
            radio.checked = true; fire(radio);
            from.value = show(start); fire(from);
            until.value = show(a.until); fire(until);
          } else {
            const period = f.querySelector('input[name=timeframe][value=period]');
            if (period) { period.checked = true; fire(period); }
            if (f.elements['periods']) f.elements['periods'].value = a.days;
          }
          const unread = f.querySelector('input[name=unread]');
          if (unread) unread.checked = false;
          const button = document.getElementById('submitFilterMail');
          setTimeout(() => f.requestSubmit ? f.requestSubmit(button || undefined) : f.submit(), 50);
          return { ok: true, reason: a.mode };
        }
        """;

    /// <summary>True unless the page shows a form error after the filter went out.</summary>
    private const string FilterAcceptedScript = """
        () => {
          const e = Array.from(document.querySelectorAll('.alert-danger, .invalid-feedback, .is-invalid')).find(x => x.offsetParent !== null);
          return { ok: !e, reason: e ? 'error shown' : '' };
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

    private DateOnly Today => DateOnly.FromDateTime(_clock.GetLocalNow().DateTime);

    private string FilterArgument(string mode)
    {
        var today = Today;
        return JsonSerializer.Serialize(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["mode"] = mode,
            ["days"] = HistoryPeriod,
            ["from"] = today.AddYears(-FallbackRangeYears).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["until"] = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        });
    }

    /// <summary>
    /// Drives the mailbox's page itself: true when the filter went through the page's form (the page then handles the TAN).
    /// The date range is tried first; a page without one, or one that shows an error for it, gets the longest period.
    /// </summary>
    private async Task<bool> SetHistoryFilterInPageAsync(IBrowserFetcher browser, string mailbox, CancellationToken ct)
    {
        var label = Label(mailbox);
        var page = new Uri(_base, MailPath(mailbox));
        await browser.NavigateAsync(page, ct);
        await Task.Delay(TimeSpan.FromSeconds(2), ct);
        var (submitted, reason) = await browser.RunAsync(SubmitFilterScript, FilterArgument("range"), ct);
        var mode = "date range";
        if (submitted)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            var (dialogShown, _) = await browser.RunAsync(TanDialogScript, "", ct);
            var (accepted, _) = dialogShown ? (true, "") : await browser.RunAsync(FilterAcceptedScript, "", ct);
            if (!accepted)
            {
                _log.LogInformation("Baader portal: the {Mailbox} page rejected the date range; using the {Period}-day period.", label, HistoryPeriod);
                submitted = false;
                await browser.NavigateAsync(page, ct);
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
        else if (reason != "no filter form")
            _log.LogInformation("Baader portal: the {Mailbox} page offers no date range ({Reason}); using the {Period}-day period.", label, reason, HistoryPeriod);
        if (!submitted)
        {
            mode = HistoryPeriod + "-day period";
            (submitted, reason) = await browser.RunAsync(SubmitFilterScript, FilterArgument("period"), ct);
            if (!submitted)
            {
                _log.LogInformation("Baader portal: the {Mailbox} page's filter could not be used ({Reason}).", label, reason);
                return false;
            }
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        }
        var (dialog, _) = await browser.RunAsync(TanDialogScript, "", ct);
        _tanRequired = dialog;
        _log.LogInformation("Baader portal: {Mailbox} filter set in the page ({Mode}){Mfa}.", label, mode, dialog ? ", TAN dialog shown" : "");
        return true;
    }

    /// <summary>
    /// Sets the mailbox's session filter: through its page when the browser can drive it, else by posting the page's form
    /// (the date range from the from field's earliest date, falling back to the longest period when the portal rejects it).
    /// </summary>
    private async Task SetHistoryFilterAsync(IBrowserFetcher browser, string mailbox, CancellationToken ct)
    {
        if (await SetHistoryFilterInPageAsync(browser, mailbox, ct)) { _pageDriven = true; return; }
        _pageDriven = false;
        var label = Label(mailbox);
        var (token, min) = await MailPageAsync(browser, mailbox, ct);
        if (token != null) _token = token;
        _token ??= await TokenAsync(browser, ct);
        var page = new Uri(_base, MailPath(mailbox));
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Accept"] = "text/html" };
        var today = Today;
        var response = await browser.PostFormAsync(page, HistoryFilterFields(_token, min ?? today.AddYears(-FallbackRangeYears), today), headers, ct);
        EnsureSession(response, "setting the document filter");
        var mode = "date range";
        if (!response.IsSuccess || response.Text.Contains("alert-danger", StringComparison.Ordinal))
        {
            _log.LogInformation("Baader portal: the {Mailbox} rejected the date range (HTTP {Status}); using the {Period}-day period.", label, response.Status, HistoryPeriod);
            if (InfoToken().Match(response.Text) is { Success: true } t) _token = TokenOf(t);
            response = await browser.PostFormAsync(page, HistoryFilterFields(_token), headers, ct);
            EnsureSession(response, "setting the document filter");
            mode = HistoryPeriod + "-day period";
        }
        var html = response.Text;
        if (html.Contains("mfaInputForm", StringComparison.Ordinal)) _tanRequired = true;
        if (InfoToken().Match(html) is { Success: true } m) _token = TokenOf(m);
        _log.LogInformation("Baader portal: {Mailbox} filter set ({Mode}): HTTP {Status}{Mfa}.", label, mode, response.Status, _tanRequired ? ", TAN dialog shown" : "");
    }

    /// <summary>The page token and the earliest date the from field allows (its <c>min</c>), read from the mailbox's page.</summary>
    private async Task<(string? Token, DateOnly? Min)> MailPageAsync(IBrowserFetcher browser, string mailbox, CancellationToken ct)
    {
        var response = await browser.GetAsync(new Uri(_base, MailPath(mailbox)), "text/html", ct);
        EnsureSession(response, "opening the mailbox");
        if (!response.IsSuccess) return (null, null);
        var html = response.Text;
        var token = InfoToken().Match(html) is { Success: true } m ? TokenOf(m) : null;
        DateOnly? min = null;
        if (FromMin().Match(html) is { Success: true } d
            && DateOnly.TryParseExact(d.Groups["d"].Value, ["yyyy-MM-dd", "dd.MM.yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            min = parsed;
        return (token, min);
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
            var customers = CustomerIds(json);
            var ok = customers.Count > 0;
            if (ok) _customers = customers;
            else _log.LogInformation("Baader portal status: unexpected answer shape ({Kind}).", json.ValueKind);
            return ok;
        }
        catch (JsonException ex)
        {
            _log.LogInformation(ex, "Baader portal status: {Path} answered {Type}, not JSON.", response.Url?.AbsolutePath, response.ContentType);
            return false;
        }
    }

    /// <summary>The customer ids of a <c>/fstatus</c> answer (<c>[{"customer_id": …}, …]</c>), digits only, each once.</summary>
    internal static List<string> CustomerIds(JsonElement json)
    {
        var ids = new List<string>();
        if (json.ValueKind != JsonValueKind.Array) return ids;
        foreach (var c in json.EnumerateArray())
        {
            if (c.ValueKind != JsonValueKind.Object || !c.TryGetProperty("customer_id", out var v)) continue;
            var id = v.ValueKind == JsonValueKind.Number ? v.GetRawText() : v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            if (id != null && CustomerId().IsMatch(id) && !ids.Contains(id, StringComparer.Ordinal)) ids.Add(id);
        }
        return ids;
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

    [GeneratedRegex("^[0-9]{1,16}$", RegexOptions.None, 1000)]
    private static partial Regex CustomerId();

    /// <summary>The <c>min</c> attribute of the filter form's from field.</summary>
    [GeneratedRegex("<input\\b(?=[^>]*\\bname=\"from\")[^>]*\\bmin=\"(?<d>[^\"]*)\"", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex FromMin();

    [GeneratedRegex("<meta\\s+(?:content=\"(?<a>[^\"]*)\"\\s+name=\"infotok\"|name=\"infotok\"\\s+content=\"(?<b>[^\"]*)\")", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex InfoToken();
}
