#pragma warning disable S1075 // ING's web banking lives at fixed, published addresses
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OpenBudget.Connectors.Ing;

/// <summary>A loaded page: where the browser ended up (after redirects) and what it got.</summary>
internal sealed record IngPage(Uri Url, string Html);

/// <summary>
/// ING's web banking (banking.ing.de, an Apache Wicket application behind a JavaScript sign-in): the sign-in with access
/// number and PIN, confirmed in the "Banking to go" app, runs in a browser (a web login that keeps the browser); the
/// account overview, the Postbox and the transaction export then run inside that signed-in browser (its page and the
/// page's own fetch). Only what FinTS does not offer is done here (Postbox documents, web-only accounts such as VL
/// savings contracts and the Rahmenkredit).
/// Credentials go into the sign-in form and nowhere else; nothing of them, nor any URL token, is logged.
/// </summary>
internal sealed class IngWebClient
{
    public static readonly Uri DefaultBase = new("https://banking.ing.de/");
    public const string WebLoginReference = "ing-web-login";
    public const string PostboxIdPrefix = "ing-postbox:";

    /// <summary>The access number (alias) field of the sign-in page, however the page names it.</summary>
    internal const string UsernameSelector =
        "input[autocomplete=username]:not([type=hidden]), input[name*=alias i]:not([type=hidden]), input[id*=alias i]:not([type=hidden]), input[name*=zugang i]:not([type=hidden])";

    internal const string PasswordSelector = "input[type=password]";

    internal const string CredentialsErrorSelector = "[role=alert]";

    internal const string SubmitSelector = "form button[type=submit], form button:not([type]), form input[type=submit], button[type=submit]";

    /// <summary>
    /// How long a sign-in step is shown before it is submitted, like a person: ING ignores (and does not push the app
    /// confirmation for) a submit sent right after the page loaded.
    /// </summary>
    public static readonly TimeSpan DefaultPause = TimeSpan.FromMilliseconds(1500);

    /// <summary>Waits until the page has been open for <paramref name="pause"/> before the sign-in form is submitted (the argument is not used).</summary>
    internal static string PaceScript(TimeSpan pause) =>
        "async () => { const wait = " + Ms(pause)
        + " - performance.now(); if (wait > 0) await new Promise(r => setTimeout(r, wait)); return { ok: true, reason: '' }; }";

    private static string Ms(TimeSpan span) => ((long)span.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);

    private const string RangeField = "erweiterterFilter:zeitraum:zeitraum";
    private const string DateRange = "DATUM";
    private const string PostboxFromField = "erweiterterFilter:zeitraum:dateFields:von:datePanel:dateInput";
    private const string PostboxToField = "erweiterterFilter:zeitraum:dateFields:bis:datePanel:dateInput";
    private const string PostboxFilterButton = "erweiterterFilter:filtern";
    private const string FromField = "filterAnzeige:datum:von:input";
    private const string ToField = "filterAnzeige:datum:bis:input";
    private const string FilterButton = "filterAnzeige:ergebnisseAnzeigenButton";
    private const string ExportTypeField = "content-container:content:exportTypes";
    private const string DeviceCookie = "openbudget_ing_device";

    /// <summary>
    /// The structure of the page for the log (JavaScript): path, route, title, headings and button labels, digits masked.
    /// No field values.
    /// </summary>
    private const string ShapeJs = """
        const shape = () => {
          const m = s => (s || '').replace(/\s+/g, ' ').trim().slice(0, 60).replace(/[0-9]/g, '#');
          const all = (sel, n) => Array.from(document.querySelectorAll(sel)).map(e => m(e.innerText || e.textContent)).filter(t => t).slice(0, n).join(' | ');
          return 'path=' + location.pathname.replace(/[0-9]/g, '#') + (location.hash ? ' route=' + m(location.hash.split('?')[0]) : '')
            + '; title=' + m(document.title) + '; headings=' + all('h1, h2, h3', 4) + '; buttons=' + all('button, [role=button], input[type=submit]', 6);
        };
        """;

    /// <summary>The overview has loaded (the landing page after the sign-in): the account rows or the Postbox link are there.</summary>
    internal const string SuccessScript = "async () => {" + ShapeJs + """
          const ok = () => /^\/app\/obligo/.test(location.pathname) && !!document.querySelector('.g2p-account__row, .quicklinks__postbox');
          for (let i = 0; i < 20 && !ok(); i++) await new Promise(r => setTimeout(r, 250));
          return ok() ? { ok: true, reason: '' } : { ok: false, reason: 'no overview: ' + shape() };
        }
        """;

    /// <summary>
    /// What happens between the sign-in form and the overview (see <see cref="WebLoginSpec.WaitingScript"/>): a device
    /// choice is submitted when there is one device or the remembered one (the argument, JSON <c>{device}</c>) is
    /// offered, after a person's pause; otherwise the user picks in the page (the choice is noted in a short-lived cookie
    /// for <see cref="ChosenDeviceScript"/>). The confirmation in the app is waited for; a rejected or expired one ends the
    /// sign-in; a page that is none of these for a while is shown to the user, its shape logged.
    /// </summary>
    internal const string WaitingScript = "async (arg) => {" + ShapeJs + """
          const a = (() => { try { return JSON.parse(arg || '{}'); } catch { return {}; } })();
          const route = location.pathname + location.hash;
          const st = window.__openbudgetIng || (window.__openbudgetIng = { route, since: Date.now() });
          if (st.route !== route) { st.route = route; st.since = Date.now(); }
          const age = Date.now() - st.since;
          const vis = e => !!e && e.getClientRects().length > 0;
          if (/^\/app\/obligo/.test(location.pathname)) return { ok: true, reason: '' };
          if (Array.from(document.querySelectorAll('input[type=password]')).some(vis)) return { ok: true, reason: '' };
          const text = (document.body && document.body.innerText) || '';
          const radios = Array.from(document.querySelectorAll('input[type=radio]')).filter(r => vis(r) || vis(r.closest('label')) || vis(r.parentElement));
          if (radios.length > 0) {
            const label = r => ((r.closest('label') || (r.id && document.querySelector('label[for="' + r.id + '"]')) || r.parentElement || {}).innerText || '').replace(/\s+/g, ' ').trim();
            const domain = /(^|\.)ing\.de$/.test(location.hostname) ? '; domain=ing.de' : '';
            const remember = r => { document.cookie = '__COOKIE__=' + encodeURIComponent(label(r)) + '; path=/; max-age=900' + domain + (location.protocol === 'https:' ? '; secure' : ''); };
            if (!st.listening) {
              st.listening = true;
              document.addEventListener('change', e => { if (e.target && e.target.type === 'radio') remember(e.target); }, true);
            }
            if (st.submitted === route) return age > 15000 ? { ok: false, reason: 'the device choice did not go through' } : { ok: true, reason: '' };
            const pick = radios.length === 1 ? radios[0] : radios.find(r => a.device && label(r) === a.device);
            if (!pick) return { ok: false, reason: 'device choice: ' + radios.length + ' devices, none remembered' };
            if (age < __PAUSE__) return { ok: true, reason: '' };
            const form = pick.closest('form');
            const buttons = Array.from((form || document).querySelectorAll('button, input[type=submit], [role=button]')).filter(vis);
            const button = buttons.find(b => (b.type || '').toLowerCase() === 'submit') || buttons.find(b => /weiter|bestätigen|senden|auswählen/i.test(b.innerText || b.value || ''));
            if (!button) return { ok: false, reason: 'device choice without a button' };
            pick.click();
            remember(pick);
            st.submitted = route;
            st.since = Date.now();
            setTimeout(() => button.click(), 100);
            return { ok: true, reason: 'device chosen (' + radios.length + ' offered)' };
          }
          if (/abgelehnt|abgebrochen|abgelaufen|zeitüberschreitung|nicht bestätigt/i.test(text))
            return { ok: false, reason: 'end: ING did not confirm the sign-in (rejected in the app or timed out).' };
          if (/banking to go|in (der|ihrer) app|app.{0,60}bestätig|bestätig.{0,60}app/i.test(text)) return { ok: true, reason: '' };
          if (age < 20000) return { ok: true, reason: '' };
          return { ok: false, reason: 'unexpected page: ' + shape() };
        }
        """;

    /// <summary>The device the user picked in the page (noted by <see cref="WaitingScript"/>), as the reason; the note is removed.</summary>
    internal const string ChosenDeviceScript = """
        () => {
          const m = document.cookie.match(/(?:^|;\s*)__COOKIE__=([^;]*)/);
          if (!m) return { ok: false, reason: '' };
          const domain = /(^|\.)ing\.de$/.test(location.hostname) ? '; domain=ing.de' : '';
          document.cookie = '__COOKIE__=; path=/; max-age=0' + domain;
          return { ok: true, reason: decodeURIComponent(m[1]) };
        }
        """;


    private static readonly string ChosenDevice = ChosenDeviceScript.Replace("__COOKIE__", DeviceCookie, StringComparison.Ordinal);

    private readonly Uri _base;
    private readonly string _pace;
    private readonly string _waiting;
    private readonly TimeProvider _clock;
    private readonly ILogger _log;
    private IBrowserFetcher? _browser;
    private IngPage? _overview;

    /// <summary>The client; <paramref name="pause"/> is how long a sign-in step is shown before it is submitted (<see cref="DefaultPause"/> when null).</summary>
    public IngWebClient(Uri? baseUri, TimeProvider clock, ILogger log, TimeSpan? pause = null)
    {
        _base = baseUri ?? DefaultBase;
        var wait = pause ?? DefaultPause;
        _pace = PaceScript(wait);
        _waiting = WaitingScript.Replace("__COOKIE__", DeviceCookie, StringComparison.Ordinal)
            .Replace("__PAUSE__", Ms(wait), StringComparison.Ordinal);
        _clock = clock;
        _log = log;
    }

    private Uri BankingBase => new(_base, "app/");

    /// <summary>Signed in, with the browser still open.</summary>
    public bool LoggedIn => _overview != null && _browser is { IsOpen: true };

    /// <summary>The label of the app device the user picked last time (chosen in the page without asking when it is offered).</summary>
    public string? PreferredDevice { get; set; }

    // ---------------------------------------------------------------- sign-in

    /// <summary>
    /// The sign-in in a browser: ING's JavaScript sign-in page (access number and PIN on one page or two), a device choice
    /// when there are several app devices, the confirmation in the Banking to go app, then the overview. A still-valid
    /// stored session opens the overview directly. Whatever the app cannot handle is shown to the user.
    /// </summary>
    public WebLoginSpec WebLogin(ConnectorCredentials? credentials)
    {
        var cookieDomain = _base.Host.EndsWith("ing.de", StringComparison.OrdinalIgnoreCase) ? "ing.de" : _base.Host;
        var overview = new Uri(BankingBase, "obligo");
        return new WebLoginSpec(overview, [cookieDomain], successUrlPrefix: overview)
        {
            Autofill = credentials == null ? null : new WebLoginAutofill(credentials.Username, credentials.Secret,
            [
                // access number and PIN on one page, or the PIN on the next one
                new WebLoginStep([
                    new WebLoginField(UsernameSelector, WebLoginCredential.Username),
                    new WebLoginField(PasswordSelector, WebLoginCredential.Secret) { Optional = true },
                ], SubmitSelector) { EnterSubmits = true, Verify = new WebLoginVerify(_pace) },
                new WebLoginStep([new WebLoginField(PasswordSelector, WebLoginCredential.Secret)], SubmitSelector)
                {
                    SkipWhenEntered = true,
                    EnterSubmits = true,
                    Verify = new WebLoginVerify(_pace),
                },
            ]),
            // the sign-in page's error box after the app submitted the stored access number and PIN
            CredentialsError = new WebLoginError(CredentialsErrorSelector, ["falsch", "ungültig", "nicht korrekt", "nicht richtig"],
                "ING's web banking did not accept the access number or PIN."),
            SuccessCheck = SuccessScript,
            WaitingScript = _waiting,
            WaitingArgument = JsonSerializer.Serialize(new Dictionary<string, string?>(StringComparer.Ordinal) { ["device"] = PreferredDevice }),
            // the cookie banner over the sign-in form: necessary cookies only
            // (an <ing-button> inside the shadow DOM of <ing-cc-manager>)
            Dismiss = ["ing-cc-manager >>> ing-button.cc-l0__button__more", "ing-cc-manager >>> ing-button::-p-text(Optionale Cookies ablehnen)"],
            KeepBrowser = true,
            ResumeUrl = overview,
            ReportFailure = true,
        };
    }

    /// <summary>Takes over the signed-in browser of a finished web login and reads the overview.</summary>
    public async Task AttachAsync(IBrowserFetcher browser, CancellationToken ct)
    {
        _browser = browser;
        _overview = null;
        var (picked, label) = await browser.RunAsync(ChosenDevice, "", ct);
        if (picked && label.Length > 0)
        {
            PreferredDevice = label;
            _log.LogInformation("ING web banking: the device picked in the page is remembered.");
        }
        await OverviewAsync(ct);
        _log.LogInformation("ING web banking: signed in.");
    }

    // ---------------------------------------------------------------- overview

    /// <summary>A fresh account overview, loaded in the signed-in page.</summary>
    public async Task<IngPage> OverviewAsync(CancellationToken ct)
    {
        var browser = Browser;
        await browser.NavigateAsync(new Uri(BankingBase, "obligo"), ct);
        var current = await browser.PageAsync(ct);
        var page = new IngPage(current.Url, current.Html);
        if (!IngHtml.IsOverview(page.Html))
        {
            _log.LogWarning("ING web banking: the overview did not open: {Shape}", PageShape(page));
            throw new ConnectorException("ING's web banking did not show the account overview; the next sync signs in again.");
        }
        return _overview = page;
    }

    public IReadOnlyList<IngOverviewAccount> Accounts => _overview == null ? [] : IngHtml.OverviewAccounts(_overview.Html);

    // ---------------------------------------------------------------- transactions (CSV export)

    /// <summary>
    /// Balance and booked transactions of the overview account with <paramref name="iban"/>: opens its transaction list,
    /// applies the date filter, then runs the CSV export (an Ajax dialog whose submit answers with a download URL).
    /// </summary>
    public async Task<AccountRefresh> TransactionsAsync(string iban, DateOnly? since, CancellationToken ct)
    {
        var overview = await OverviewAsync(ct);
        var row = IngHtml.OverviewAccounts(overview.Html).FirstOrDefault(a => string.Equals(a.Iban, iban, StringComparison.Ordinal))
            ?? throw new ConnectorException("ING's web banking no longer lists this account.");
        var page = await GetAsync(Resolve(overview.Url, row.Href), ct);

        if (IngHtml.FormWith(page.Html, FromField) is { } filter)
        {
            var today = Today();
            var first = since ?? today.AddYears(-10);
            var earliest = EarliestDate(filter.Inner);
            if (earliest is { } e && first < e) first = e;
            var fields = Without(filter.Fields, FromField, ToField);
            fields.Add(new HtmlField(FromField, first.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)));
            fields.Add(new HtmlField(ToField, today.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)));
            fields.Add(new HtmlField(FilterButton, ""));
            page = await PostAsync(Resolve(page.Url, filter.Action), fields, ct);
        }

        var exportId = IngHtml.LinkIdByText(page.Html, "Exportieren")
            ?? throw new ConnectorException("ING's transaction list offers no export.");
        var open = IngHtml.AjaxFor(page.Html, exportId) ?? throw new ConnectorException("ING's export link looks different than expected.");
        var dialog = await AjaxAsync(page.Url, open.Url, open.Post ? [] : null, ct);

        var submit = IngHtml.AjaxCalls(dialog).FirstOrDefault(c => c.FormId != null && (c.SubmittingName ?? "").Contains("export", StringComparison.OrdinalIgnoreCase))
            ?? throw new ConnectorException("ING's export dialog looks different than expected.");
        var form = IngHtml.Forms(dialog).FirstOrDefault(f => string.Equals(f.Id, submit.FormId, StringComparison.Ordinal))
            ?? throw new ConnectorException("ING's export dialog has no form.");
        var csv = IngHtml.Radios(form.Inner, ExportTypeField).FirstOrDefault(r => r.Label.Contains("CSV", StringComparison.OrdinalIgnoreCase));
        if (csv.Value == null) throw new ConnectorException("ING's export dialog offers no CSV format.");
        var exportFields = Without(form.Fields, ExportTypeField);
        exportFields.Add(new HtmlField(ExportTypeField, csv.Value));
        exportFields.Add(new HtmlField(submit.SubmittingName!, "1"));
        var started = await AjaxAsync(page.Url, submit.Url, exportFields, ct);
        var download = IngHtml.AjaxDownloadUrl(started) ?? throw new ConnectorException("ING did not start the CSV export.");

        var response = await Browser.GetAsync(Resolve(page.Url, download), "text/csv, */*", ct);
        EnsureSession(response, "exporting transactions");
        if (!response.IsSuccess) throw new ConnectorException($"ING's CSV export answered HTTP {response.Status}.");
        var parsed = IngHtml.ParseCsv(IngHtml.DecodeCsv(response.Body));
        _log.LogInformation("ING web banking: the export has {Count} booked transaction(s).", parsed.Transactions.Count);
        return new AccountRefresh(parsed.Balance ?? row.Balance, parsed.Transactions, []);
    }

    private static DateOnly? EarliestDate(string formInner, string field = FromField)
    {
        foreach (var tag in formInner.Split('<').Where(t => t.StartsWith("input", StringComparison.OrdinalIgnoreCase)))
        {
            if (!tag.Contains(field, StringComparison.Ordinal)) continue;
            var text = IngHtml.Attr(" " + tag, "data-date-start-date");
            if (DateOnly.TryParseExact(text, "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
        }
        return null;
    }

    // ---------------------------------------------------------------- Postbox

    /// <summary>A document listed in the Postbox, with the stable identifier it is deduplicated by.</summary>
    public sealed record PostboxEntry(string Identifier, IngPostboxRow Row, Uri Download);

    /// <summary>
    /// Every document of every postbox tab (own and joint customer relationships). ING lists at most
    /// <see cref="PostboxListingCap"/> documents per listing (the newest of the range) and offers no paging, so the whole
    /// ten years (<paramref name="full"/>) are read a year at a time through the "Freier Zeitraum" filter, a window that
    /// still comes back full being split further; otherwise the last year (the "1 Jahr" range, re-rendered through
    /// Wicket Ajax).
    /// </summary>
    public async Task<IReadOnlyList<PostboxEntry>> ListPostboxAsync(bool full, CancellationToken ct)
    {
        var overview = _overview ?? throw new ConnectorException("Not signed in to ING's web banking.");
        var link = IngHtml.LinkWithClass(overview.Html, "quicklinks__postbox") ?? "postbox";
        var page = await GetAsync(Resolve(overview.Url, link), ct);
        var tabs = IngHtml.PostboxTabs(page.Html);
        var labels = tabs.Count == 0 ? [null] : tabs.Select(t => (string?)t.Label).ToList();
        var entries = new List<PostboxEntry>();
        var latest = page.Html;
        foreach (var label in labels)
        {
            if (label != null)
            {
                var tab = IngHtml.PostboxTabs(latest).FirstOrDefault(t => string.Equals(t.Label, label, StringComparison.Ordinal))
                    ?? throw new ConnectorException("ING's Postbox tabs changed while reading them.");
                page = await GetAsync(Resolve(page.Url, tab.Href), ct);
                latest = page.Html;
            }
            var listings = new List<(Uri Url, IReadOnlyList<IngPostboxRow> Rows)>();
            if (label == labels[0]) ListingTruncated = false;
            if (full && await ListByDateAsync(page, listings, ct) is { } last && listings.Sum(l => l.Rows.Count) > 0)
            {
                page = last;
                latest = last.Html;
            }
            else
            {
                // the year-by-year listing found nothing (or isn't offered): read the plain range instead
                if (full && listings.Count > 0) { _log.LogWarning("ING Postbox: the listing by date found nothing; using the 10-year listing."); listings.Clear(); }
                var listing = page.Html;
                var range = full ? "MONATE120" : "MONATE12";
                if (IngHtml.Select(page.Html, RangeField) is { Id: { } selectId, Options: var options }
                    && options.Any(o => string.Equals(o.Value, range, StringComparison.Ordinal))
                    && IngHtml.AjaxFor(page.Html, selectId) is { } change)
                {
                    listing = await AjaxAsync(page.Url, change.Url, [new HtmlField(RangeField, range)], ct);
                    latest = listing;
                }
                var rows = IngHtml.PostboxRows(listing);
                if (rows.Count >= PostboxListingCap)
                {
                    ListingTruncated = true;
                    _log.LogWarning("ING Postbox: a listing returned {Count} documents, ING's limit; older ones may be missing.", rows.Count);
                }
                listings.Add((page.Url, rows));
            }
            // A day never spans two listings, so the per-day counter keeps identifiers stable however the range is cut.
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var (url, rows) in listings)
            {
                foreach (var row in rows)
                {
                    var key = string.Join('\n', label ?? "", row.Type, row.Subject, row.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                    var n = seen[key] = seen.GetValueOrDefault(key) + 1;
                    var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key + "\n" + n.ToString(CultureInfo.InvariantCulture))))[..32];
                    entries.Add(new PostboxEntry(PostboxIdPrefix + hash, row, Resolve(url, row.DownloadHref)));
                }
            }
        }
        _log.LogInformation("ING Postbox: {Count} document(s) listed in {Tabs} postbox(es) {Range}.",
            entries.Count, labels.Count, full ? "over all years" : "for the last year");
        return entries;
    }

    /// <summary>Whether the last Postbox listing hit ING's limit, so a full pass must not count as complete.</summary>
    public bool ListingTruncated { get; private set; }

    /// <summary>ING's listing shows at most this many documents (the newest of the range).</summary>
    internal const int PostboxListingCap = 500;

    /// <summary>
    /// The postbox read window by window with the "Freier Zeitraum" filter, newest first; null (nothing read) when the
    /// page has no such filter. Returns the last page shown.
    /// </summary>
    private async Task<IngPage?> ListByDateAsync(IngPage page, List<(Uri Url, IReadOnlyList<IngPostboxRow> Rows)> listings, CancellationToken ct)
    {
        if (IngHtml.Select(page.Html, RangeField) is not { Id: { } selectId, Options: var options }
            || !options.Any(o => string.Equals(o.Value, DateRange, StringComparison.Ordinal))
            || IngHtml.AjaxFor(page.Html, selectId) is not { } change
            || IngHtml.FormWith(page.Html, PostboxFilterButton) is not { } first)
            return null;
        // Choosing "Freier Zeitraum" enables the date fields on the server; the form is then submitted per window.
        await AjaxAsync(page.Url, change.Url, [new HtmlField(RangeField, DateRange)], ct);
        var today = Today();
        var earliest = EarliestDate(first.Inner, PostboxFromField) ?? today.AddYears(-10);
        var found = new List<(DateOnly From, Uri Url, IReadOnlyList<IngPostboxRow> Rows)>();
        for (var to = today; to >= earliest; to = to.AddYears(-1))
        {
            var from = to.AddYears(-1).AddDays(1);
            await ReadWindowAsync(from < earliest ? earliest : from, to);
        }
        listings.AddRange(found.OrderByDescending(f => f.From).Select(f => (f.Url, f.Rows)));
        return page;

        async Task ReadWindowAsync(DateOnly from, DateOnly to)
        {
            var form = IngHtml.FormWith(page.Html, PostboxFilterButton)
                ?? throw new ConnectorException("ING's Postbox filter disappeared while reading it.");
            var fields = Without(form.Fields, RangeField, PostboxFromField, PostboxToField, PostboxFilterButton);
            fields.Add(new HtmlField(RangeField, DateRange));
            fields.Add(new HtmlField(PostboxFromField, from.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)));
            fields.Add(new HtmlField(PostboxToField, to.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)));
            fields.Add(new HtmlField(PostboxFilterButton, "1"));
            // "Filter anwenden" is a Wicket Ajax button: it posts the whole form to its behaviour and the answer replaces
            // the list in place (a plain form submit gives back the unfiltered page).
            var id = ButtonId(page.Html, PostboxFilterButton);
            string listing;
            if (id.Length > 0 && IngHtml.AjaxFor(page.Html, id) is { } filter)
                listing = await AjaxAsync(page.Url, filter.Url, fields, ct);
            else
            {
                page = await PostAsync(Resolve(page.Url, form.Action), fields, ct);
                listing = page.Html;
            }
            var rows = IngHtml.PostboxRows(listing);
            if (rows.Count >= PostboxListingCap && from < to)
            {
                // Full: ING may have cut it short. Read both halves on their own.
                var mid = from.AddDays((to.DayNumber - from.DayNumber) / 2);
                await ReadWindowAsync(mid.AddDays(1), to);
                await ReadWindowAsync(from, mid);
                return;
            }
            if (rows.Count >= PostboxListingCap)
                _log.LogWarning("ING Postbox: {Count} documents on one day, ING's limit; some may be missing.", rows.Count);
            found.Add((from, page.Url, rows.Where(r => r.Date >= from && r.Date <= to).ToList()));
        }
    }

    /// <summary>The document's bytes, or null when ING answered with something that is not a PDF.</summary>
    public async Task<byte[]?> DownloadAsync(Uri url, CancellationToken ct)
    {
        var response = await Browser.GetAsync(url, "application/pdf, */*", ct);
        EnsureSession(response, "downloading documents");
        if (!response.IsSuccess) return null;
        var bytes = response.Body;
        return bytes.Length > 4 && bytes[0] == '%' && bytes[1] == 'P' && bytes[2] == 'D' && bytes[3] == 'F' ? bytes : null;
    }

    // ---------------------------------------------------------------- requests in the signed-in page

    private IBrowserFetcher Browser =>
        _browser is { IsOpen: true } b ? b : throw new ConnectorException("ING's web banking session has ended; the next sync signs in again.");

    private DateOnly Today()
    {
        var now = _clock.GetUtcNow();
        try { now = TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin")); }
        catch (TimeZoneNotFoundException) { /* UTC is close enough */ }
        return DateOnly.FromDateTime(now.DateTime);
    }

    /// <summary>The id attribute of the button named <paramref name="name"/>, or "" (plain string search, no regex).</summary>
    private static string ButtonId(string html, string name)
    {
        var at = html.IndexOf("name=\"" + name + "\"", StringComparison.Ordinal);
        if (at < 0) return "";
        var start = html.LastIndexOf("<button", at, StringComparison.Ordinal);
        var rel = html.AsSpan(at).IndexOf('>');
        var end = rel < 0 ? -1 : at + rel;
        if (start < 0 || end < 0) return "";
        var tag = html[start..end];
        var idAt = tag.IndexOf(" id=\"", StringComparison.Ordinal);
        if (idAt < 0) return "";
        var from = idAt + 5;
        var relClose = tag.AsSpan(from).IndexOf('"');
        var close = relClose < 0 ? -1 : from + relClose;
        return close < 0 ? "" : tag[from..close];
    }

    private static List<HtmlField> Without(IEnumerable<HtmlField> fields, params string[] names) =>
        fields.Where(f => !names.Contains(f.Name, StringComparer.Ordinal)).ToList();

    private static Uri Resolve(Uri page, string href) => new(page, WebUtility.HtmlDecode(href));

    /// <summary>
    /// A request that ended on the sign-in (or could not be completed by the page) means ING ended the session. Only the
    /// path is logged.
    /// </summary>
    private void EnsureSession(BrowserFetchResponse response, string what)
    {
        var ended = response.Status == 0
            || (response.Url is { } url && (!string.Equals(url.Host, _base.Host, StringComparison.OrdinalIgnoreCase)
                || url.AbsolutePath.StartsWith("/app/login", StringComparison.OrdinalIgnoreCase)
                || url.AbsolutePath.StartsWith("/delogin", StringComparison.OrdinalIgnoreCase)));
        if (ended)
        {
            _log.LogWarning("ING web banking: the session ended while {What} (HTTP {Status} on {Path}).", what, response.Status, response.Url?.AbsolutePath);
            _overview = null;
            throw new ConnectorException($"ING ended the web banking session while {what}; the next sync signs in again.");
        }
        if (response.Status >= 500) throw new ConnectorException($"ING's web banking answered HTTP {response.Status}.");
    }

    private async Task<IngPage> GetAsync(Uri url, CancellationToken ct)
    {
        var response = await Browser.GetAsync(url, "text/html, application/xhtml+xml, */*", ct);
        EnsureSession(response, "opening a page");
        return new IngPage(response.Url ?? url, response.Text);
    }

    private async Task<IngPage> PostAsync(Uri url, IEnumerable<HtmlField> fields, CancellationToken ct)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Accept"] = "text/html, application/xhtml+xml, */*" };
        var response = await Browser.PostFormAsync(url, fields.Select(f => new KeyValuePair<string, string>(f.Name, f.Value)), headers, ct);
        EnsureSession(response, "submitting a form");
        return new IngPage(response.Url ?? url, response.Text);
    }

    /// <summary>
    /// A Wicket Ajax request as the page's script would send it (GET without <paramref name="fields"/>, POST with them),
    /// relative to <paramref name="page"/>; returns the &lt;ajax-response&gt; XML.
    /// </summary>
    private async Task<string> AjaxAsync(Uri page, string relative, IReadOnlyList<HtmlField>? fields, CancellationToken ct)
    {
        var target = Resolve(page, relative);
        var stamp = _clock.GetUtcNow().ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        var url = new Uri(target + (string.IsNullOrEmpty(target.Query) ? "?" : "&") + "_=" + stamp);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Wicket-Ajax"] = "true",
            ["Wicket-Ajax-BaseURL"] = page.Segments[^1] + page.Query,
            ["X-Requested-With"] = "XMLHttpRequest",
            ["Accept"] = "text/xml, application/xml, */*; q=0.01",
        };
        var response = fields == null
            ? await Browser.SendAsync(new BrowserFetchRequest("GET", url) { Headers = headers }, ct)
            : await Browser.PostFormAsync(url, fields.Select(f => new KeyValuePair<string, string>(f.Name, f.Value)), headers, ct);
        EnsureSession(response, "updating a page");
        return response.Text;
    }

    /// <summary>
    /// The structure of an unexpected page for the log: path, title, headings, form ids and button labels, digits masked.
    /// No field values and no body text.
    /// </summary>
    internal static string PageShape(IngPage page)
    {
        static string Clean(string text) =>
            System.Text.RegularExpressions.Regex.Replace(
                System.Text.RegularExpressions.Regex.Replace(WebUtility.HtmlDecode(text), "<[^>]{0,500}>", " ", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1)),
                @"\s+", " ", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1)).Trim();
        static IEnumerable<string> All(string html, string pattern) =>
            System.Text.RegularExpressions.Regex.Matches(html, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Singleline, TimeSpan.FromSeconds(1))
                .Select(m => Clean(m.Groups[1].Value)).Where(t => t.Length > 0).Select(t => t.Length > 80 ? t[..80] : t);
        var html = page.Html;
        var parts = new List<string>
        {
            "path=" + page.Url.AbsolutePath,
            "title=" + string.Join(" / ", All(html, "<title[^>]{0,200}>(.{0,300}?)</title>")),
            "headings=" + string.Join(" | ", All(html, "<h[1-3][^>]{0,300}>(.{0,400}?)</h[1-3]>").Take(6)),
            "forms=" + string.Join(",", All(html, "<form[^>]{0,500}?\\sid=\"([^\"]{1,80})\"").Take(6)),
            "buttons=" + string.Join(" | ", All(html, "<(?:button|a)[^>]{0,500}class=\"[^\"]{0,200}(?:button|btn)[^\"]{0,200}\"[^>]{0,500}>(.{0,300}?)</(?:button|a)>").Take(10)),
        };
        return System.Text.RegularExpressions.Regex.Replace(string.Join("; ", parts), "[0-9]", "#", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
    }
}
