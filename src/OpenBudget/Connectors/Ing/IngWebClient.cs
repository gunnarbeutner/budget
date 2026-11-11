#pragma warning disable S1075 // ING's web banking lives at fixed, published addresses
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using OpenBudget.Connectors.FinTs;

namespace OpenBudget.Connectors.Ing;

/// <summary>A loaded page: where the browser ended up (after redirects) and what it got.</summary>
internal sealed record IngPage(Uri Url, string Html);

/// <summary>
/// ING's web banking (banking.ing.de, an Apache Wicket application) over plain HTTP: the sign-in with access number
/// and PIN on access.ing.de, confirmed in the "Banking to go" app, the account overview, the Postbox and the
/// transaction export. Only what FinTS does not offer is done here (Postbox documents, web-only accounts such as
/// VL savings contracts and the Rahmenkredit).
/// Credentials go into the sign-in form and nowhere else; nothing of them, nor any URL token, is logged.
/// </summary>
internal sealed class IngWebClient
{
    public static readonly Uri BankingBase = new("https://banking.ing.de/app/");
    public const string ApprovalReference = "ing-web-approval";
    public const string DeviceReference = "ing-web-device";
    public const string PostboxIdPrefix = "ing-postbox:";

    private const string AliasField = "view:alias:alias";
    private const string SecretField = "view:secret:secret";
    private const string LoginButton = "view:next-inline";
    private const string DeviceField = "view:auth:deviceSelection:device:device";
    private const string DeviceButton = "buttons:next";
    private const string RangeField = "erweiterterFilter:zeitraum:zeitraum";
    private const string DateRange = "DATUM";
    private const string PostboxFromField = "erweiterterFilter:zeitraum:dateFields:von:datePanel:dateInput";
    private const string PostboxToField = "erweiterterFilter:zeitraum:dateFields:bis:datePanel:dateInput";
    private const string PostboxFilterButton = "erweiterterFilter:filtern";
    private const string FromField = "filterAnzeige:datum:von:input";
    private const string ToField = "filterAnzeige:datum:bis:input";
    private const string FilterButton = "filterAnzeige:ergebnisseAnzeigenButton";
    private const string ExportTypeField = "content-container:content:exportTypes";
    private const int MaxHops = 15;

    private readonly IBankHttpClient _http;
    private readonly TimeProvider _clock;
    private readonly ILogger _log;
    private IngPage? _overview;

    public IngWebClient(IBankHttpClient http, TimeProvider clock, ILogger log)
    {
        _http = http;
        _clock = clock;
        _log = log;
    }

    public bool LoggedIn => _overview != null;

    /// <summary>The label of the app device the user picked last time (offered first, skipped when it is still there).</summary>
    public string? PreferredDevice { get; set; }

    // ---------------------------------------------------------------- sign-in

    /// <summary>
    /// Signs in unless the stored cookies still carry a web banking session, and returns the account overview.
    /// Flow (as observed): /app/obligo redirects through OAuth to access.ing.de's sign-in form (access number + PIN);
    /// then, with more than one app device, a device choice; then the bank waits for the confirmation in the app while
    /// the page polls a Wicket timer; once confirmed the timer answers with a redirect back through OAuth and the
    /// post-login checks (pages that forward with JavaScript) to the overview.
    /// </summary>
    public async Task<IngPage> LoginAsync(ConnectorCredentials credentials, IChallengeAsker ask, CancellationToken ct)
    {
        if (_overview != null) return _overview;
        var page = await FollowAsync(await GetAsync(new Uri(BankingBase, "obligo"), ct), ct);
        if (IngHtml.IsOverview(page.Html))
        {
            _log.LogInformation("ING web banking: reusing the stored session.");
            return _overview = page;
        }

        var shown = _clock.GetTimestamp();
        var form = IngHtml.FormWith(page.Html, SecretField);
        if (form == null)
        {
            _log.LogWarning("ING web banking: no sign-in form where one was expected: {Shape}", PageShape(page));
            throw new ConnectorException("ING's sign-in page looks different than expected; the web banking part cannot sign in.");
        }
        _log.LogInformation("ING web banking: submitting the sign-in form.");
        var fields = Without(form.Fields, AliasField, SecretField);
        fields.Add(new HtmlField(AliasField, credentials.Username));
        fields.Add(new HtmlField(SecretField, credentials.Secret));
        fields.Add(new HtmlField(LoginButton, ""));
        await PaceAsync(page, shown, ct);
        page = await PostAsync(Resolve(page.Url, form.Action), fields, ct, referrer: page.Url);
        shown = _clock.GetTimestamp();

        if (IngHtml.FormWith(page.Html, SecretField) != null)
            throw new FinTsException("ING's web banking did not accept the access number or PIN.", credentialsRejected: true);

        if (IngHtml.FormWith(page.Html, DeviceField) is { } deviceForm)
            page = await ChooseDeviceAsync(page, shown, deviceForm, ask, ct);

        var statusUrl = IngHtml.TimerUrl(page.Html)
            ?? throw new ConnectorException("ING asked for a sign-in step the connector does not know (only the confirmation in the Banking to go app is supported).");
        string? redirect;
        while (true)
        {
            await ask.AskAsync(ConnectorChallenge.Approval(
                "Please confirm the sign-in to ING's web banking in your Banking to go app.", reference: ApprovalReference));
            var response = await AjaxAsync(page.Url, statusUrl, null, ct);
            redirect = IngHtml.AjaxRedirect(response);
            if (redirect != null) break;
            statusUrl = IngHtml.TimerUrl(response)
                ?? throw new ConnectorException("ING did not confirm the sign-in (rejected in the app or timed out).");
        }
        _log.LogInformation("ING web banking: the sign-in was confirmed in the app.");
        page = await FollowAsync(await GetAsync(Resolve(page.Url, redirect), ct), ct);
        if (!IngHtml.IsOverview(page.Html))
        {
            // The post-login checks sometimes end elsewhere; the overview itself says whether we are in.
            page = await FollowAsync(await GetAsync(new Uri(BankingBase, "obligo/"), ct), ct);
        }
        if (!IngHtml.IsOverview(page.Html))
        {
            _log.LogWarning("ING web banking: after the sign-in the page is not the overview: {Shape}", PageShape(page));
            throw new ConnectorException("After the sign-in ING shows a page that needs your attention (a notice or new terms?). Please sign in once at banking.ing.de and confirm it.");
        }
        return _overview = page;
    }

    /// <summary>Asks for (or reuses) the device and submits the choice.</summary>
    /// <remarks>
    /// Submitting the device choice is what makes ING create the confirmation and push it to that device. The page
    /// ignores submits for a moment after loading (RequestBlocker); a submit sent right away still creates the confirmation, but ING sends no push for it, so it is paced like a person's click.
    /// </remarks>
    private async Task<IngPage> ChooseDeviceAsync(IngPage page, long shown, HtmlForm form, IChallengeAsker ask, CancellationToken ct)
    {
        var devices = IngHtml.Radios(form.Inner, DeviceField);
        if (devices.Count == 0) throw new ConnectorException("ING asks to pick a device for the confirmation but lists none.");
        var chosen = devices.Count == 1 ? devices[0].Value
            : devices.FirstOrDefault(d => string.Equals(d.Label, PreferredDevice, StringComparison.Ordinal)).Value;
        if (chosen == null)
        {
            var answer = await ask.AskAsync(ConnectorChallenge.Choice(
                "Which device should ING send the sign-in confirmation to?",
                devices.Select(d => new ChallengeOption(d.Value, d.Label)).ToList(), DeviceReference));
            chosen = devices.FirstOrDefault(d => string.Equals(d.Value, answer.Value, StringComparison.Ordinal)).Value
                ?? throw new ConnectorException("No device was chosen for the sign-in confirmation.");
        }
        PreferredDevice = devices.First(d => string.Equals(d.Value, chosen, StringComparison.Ordinal)).Label;
        _log.LogInformation("ING web banking: asking for the confirmation on one of {Count} app device(s).", devices.Count);
        var fields = Without(form.Fields, DeviceField);
        fields.Add(new HtmlField(DeviceField, chosen));
        fields.Add(new HtmlField(DeviceButton, ""));
        await PaceAsync(page, shown, ct);
        return await PostAsync(Resolve(page.Url, form.Action), fields, ct, referrer: page.Url);
    }

    // ---------------------------------------------------------------- overview

    /// <summary>A fresh account overview (the session must be signed in).</summary>
    public async Task<IngPage> OverviewAsync(CancellationToken ct)
    {
        var page = await FollowAsync(await GetAsync(new Uri(BankingBase, "obligo/"), ct), ct);
        if (!IngHtml.IsOverview(page.Html)) throw new ConnectorException("ING ended the web banking session.");
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
        var page = await FollowAsync(await GetAsync(Resolve(overview.Url, row.Href), ct), ct);

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
            page = await FollowAsync(await PostAsync(Resolve(page.Url, filter.Action), fields, ct), ct);
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

        using var response = await SendAsync(new HttpRequestMessage(HttpMethod.Get, Resolve(page.Url, download)), ct);
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        var parsed = IngHtml.ParseCsv(IngHtml.DecodeCsv(bytes));
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
        var page = await FollowAsync(await GetAsync(Resolve(overview.Url, link), ct), ct);
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
                page = await FollowAsync(await GetAsync(Resolve(page.Url, tab.Href), ct), ct);
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
                page = await FollowAsync(await PostAsync(Resolve(page.Url, form.Action), fields, ct), ct);
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
        using var response = await SendAsync(new HttpRequestMessage(HttpMethod.Get, url), ct);
        if (!response.IsSuccessStatusCode) return null;
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        return bytes.Length > 4 && bytes[0] == '%' && bytes[1] == 'P' && bytes[2] == 'D' && bytes[3] == 'F' ? bytes : null;
    }

    // ---------------------------------------------------------------- HTTP

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

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            var response = await _http.SendAsync(request, ct);
            if ((int)response.StatusCode >= 500)
            {
                var code = (int)response.StatusCode;
                response.Dispose();
                throw new ConnectorException($"ING's web banking answered HTTP {code}.");
            }
            return response;
        }
        catch (HttpRequestException ex)
        {
            throw new ConnectorException("ING's web banking could not be reached.", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new ConnectorException("ING's web banking did not answer in time.", ex);
        }
        finally
        {
            request.Dispose();
        }
    }

    private static async Task<IngPage> ReadAsync(HttpResponseMessage response, Uri requested, CancellationToken ct)
    {
        using (response)
        {
            var url = response.RequestMessage?.RequestUri ?? requested;
            return new IngPage(url, await response.Content.ReadAsStringAsync(ct));
        }
    }

    private async Task<IngPage> GetAsync(Uri url, CancellationToken ct) =>
        await ReadAsync(await SendAsync(new HttpRequestMessage(HttpMethod.Get, url), ct), url, ct);

    /// <summary>
    /// Waits until a person could have submitted <paramref name="page"/> (shown at <paramref name="shown"/>): half again
    /// as long as the page blocks submits after loading.
    /// </summary>
    private async Task PaceAsync(IngPage page, long shown, CancellationToken ct)
    {
        var wait = IngHtml.RequestBlock(page.Html) * 1.5 - _clock.GetElapsedTime(shown);
        if (wait > TimeSpan.Zero) await Task.Delay(wait, _clock, ct);
    }

    private async Task<IngPage> PostAsync(Uri url, IEnumerable<HtmlField> fields, CancellationToken ct, Uri? referrer = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(fields.Select(f => new KeyValuePair<string, string>(f.Name, f.Value))),
        };
        request.Headers.TryAddWithoutValidation("Origin", url.GetLeftPart(UriPartial.Authority));
        request.Headers.Referrer = referrer ?? url;
        return await ReadAsync(await SendAsync(request, ct), url, ct);
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
        var request = new HttpRequestMessage(fields == null ? HttpMethod.Get : HttpMethod.Post, url);
        if (fields != null)
            request.Content = new FormUrlEncodedContent(fields.Select(f => new KeyValuePair<string, string>(f.Name, f.Value)));
        request.Headers.TryAddWithoutValidation("Wicket-Ajax", "true");
        request.Headers.TryAddWithoutValidation("Wicket-Ajax-BaseURL", page.Segments[^1] + page.Query);
        request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        request.Headers.Remove("Accept");
        request.Headers.TryAddWithoutValidation("Accept", "text/xml, application/xml, */*; q=0.01");
        request.Headers.Referrer = page;
        using var response = await SendAsync(request, ct);
        return await response.Content.ReadAsStringAsync(ct);
    }

    /// <summary>Follows the post-login check pages, which only forward with JavaScript, until a real page.</summary>
    private async Task<IngPage> FollowAsync(IngPage page, CancellationToken ct)
    {
        for (var hop = 0; hop < MaxHops; hop++)
        {
            if (IngHtml.IsOverview(page.Html) || !page.Url.AbsolutePath.Contains("/postlogin", StringComparison.Ordinal)) return page;
            var next = IngHtml.ScriptRedirect(page.Html);
            if (next == null) return page;
            page = await GetAsync(Resolve(page.Url, next), ct);
        }
        throw new ConnectorException("ING's web banking kept forwarding without reaching a page.");
    }

    /// <summary>
    /// The structure of an unexpected page for the log: path, title, headings, form ids and button labels, digits masked.
    /// No field values and no body text.
    /// </summary>
    private static string PageShape(IngPage page)
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
