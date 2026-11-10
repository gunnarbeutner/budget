#pragma warning disable CA2000 // test doubles: lifetimes are owned by the HTTP client
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using OpenBudget.Connectors;

namespace OpenBudget.Tests.Connectors;

/// <summary>
/// A made-up ING web banking (banking.ing.de / access.ing.de): the page structures and Wicket mechanics of the real
/// one (sign-in form, device choice, app confirmation polled through a Wicket timer, JavaScript-forwarding post-login
/// pages, overview, Postbox tabs with an Ajax time range, CSV export dialog) with invented people and IBANs.
/// </summary>
internal sealed class FakeIng : HttpMessageHandler
{
    public const string Alias = "testuser";
    public const string VlIban = "DE00000000000000000077";
    public const string CreditIban = "DE00000000000000000099";
    public const string GiroIban = "DE00000000000000000011"; // = SyntheticBank.Iban (the FinTS side)
    public const string SessionCookie = "ing_session";
    private const string Banking = "https://banking.ing.de";

    private readonly Lock _lock = new();
    private readonly HashSet<string> _sessions = [];
    private int _counter;
    private int _polls;

    public string Pin { get; set; } = "654321PIN";

    public List<string> Devices { get; set; } = ["phone / Example Phone", "tablet / Example Tablet"];

    public int PendingPolls { get; set; } = 2;

    public bool Reject { get; set; }

    public string? ChosenDevice { get; private set; }

    /// <summary>How long the sign-in pages ignore submits after loading (ING's RequestBlocker; 1000 ms there).</summary>
    public int RequestBlockMs { get; set; } = 100;

    /// <summary>
    /// Push notifications sent to the app. Like ING, the confirmation is created by the submit that names the device
    /// (the device choice, or the sign-in itself with a single device), but pushed only when that submit did not come
    /// faster than the page lets a person click; status polls never push.
    /// </summary>
    public int Pushes { get; private set; }

    /// <summary>Confirmations created without a push (visible only when the app is opened).</summary>
    public int SilentApprovals { get; private set; }

    private DateTime _pageShownAt;

    public List<string> Requests { get; } = [];

    public List<string> Bodies { get; } = [];

    public List<string> Ranges { get; } = [];

    public List<(string From, string To)> Filters { get; } = [];

    public Dictionary<string, int> Downloads { get; } = new(StringComparer.Ordinal);

    public sealed record Doc(string Id, int Box, string Type, string Subject, DateOnly Date, bool Broken = false);

    public List<Doc> Documents { get; } =
    [
        new("giro-nov", 1, "Kontoauszug", "Girokonto 1234567 / DE00 0000 0000 0000 0000 11", new DateOnly(2026, 11, 2)),
        new("vl-oct", 1, "Kontoauszug", "VL-Sparen 7777777 / DE00 0000 0000 0000 0000 77", new DateOnly(2026, 10, 30)),
        new("tax", 1, "Jahressteuerbescheinigung", "", new DateOnly(2026, 3, 1)),
        new("old", 1, "Depotauszug", "Depot 0000000001", new DateOnly(2020, 1, 15)),
        new("joint", 2, "Information", "Example letter &amp; notes", new DateOnly(2026, 10, 1)),
        new("broken", 2, "Information", "Broken document", new DateOnly(2026, 9, 1), Broken: true),
    ];

    public string Csv { get; set; } =
        "Umsatzanzeige;Datei erstellt am: 09.11.2026 12:00\n\nIBAN;DE00 0000 0000 0000 0000 77\nKontoname;VL-Sparen\nBank;ING\nKunde;Alex Example\n"
        + "Zeitraum;01.10.2025 - 09.11.2026\nSaldo;1.040,00;EUR\n\nSortierung;Datum absteigend\n\nIn der CSV-Datei finden Sie alle bereits gebuchten Umsätze.\n\n"
        + "Buchung;Wertstellungsdatum;Auftraggeber/Empfänger;Buchungstext;Verwendungszweck;Referenz;Saldo;Währung;Betrag;Währung\n"
        + "02.11.2026;02.11.2026;Example Employer GmbH;Gutschrift;VL Rate November;REF-2;1.040,00;EUR;40,00;EUR\n"
        + "01.10.2026;01.10.2026;Example Employer GmbH;Gutschrift;\"VL Rate; Oktober\";;1.000,00;EUR;1.000,00;EUR\n";

    /// <summary>The Rahmenkredit's export: ING shows the owed amount without a sign; drawings are negative, repayments positive.</summary>
    public string CreditCsv { get; set; } =
        "Umsatzanzeige;Datei erstellt am: 09.11.2026 12:00\n\nIBAN;DE00 0000 0000 0000 0000 99\nKontoname;Rahmenkredit\nBank;ING\nKunde;Alex Example\n"
        + "Zeitraum;01.10.2025 - 09.11.2026\nSaldo;1.500,00;EUR\n\nSortierung;Datum absteigend\n\n"
        + "Buchung;Wertstellungsdatum;Auftraggeber/Empfänger;Buchungstext;Verwendungszweck;Referenz;Saldo;Währung;Betrag;Währung\n"
        + "05.11.2026;05.11.2026;Alex Example;Rückzahlung;Tilgung;CR-2;-1.500,00;EUR;500,00;EUR\n"
        + "15.10.2026;15.10.2026;Alex Example;Auszahlung;Abruf Rahmenkredit;CR-1;-2.000,00;EUR;-2.000,00;EUR\n";

    private string _opened = "vl";

    public int Count(string needle) => Requests.Count(r => r.Contains(needle, StringComparison.Ordinal));

    public BankHttpClient CreateClient() => new(jar => new CookieRedirectHandler(jar, this));

    public void ExpireSessions()
    {
        lock (_lock) _sessions.Clear();
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content == null ? "" : request.Content.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
        lock (_lock) return Task.FromResult(Handle(request, body));
    }

    private HttpResponseMessage Handle(HttpRequestMessage request, string body)
    {
        var uri = request.RequestUri!;
        var x = HttpUtility.ParseQueryString(uri.Query)["x"] ?? "";
        Requests.Add($"{request.Method} {uri.Host}{uri.AbsolutePath}?x={x}");
        if (body.Length > 0) Bodies.Add(body);
        var cookie = request.Headers.TryGetValues("Cookie", out var c) ? string.Join(';', c) : "";
        var m = Regex.Match(cookie, SessionCookie + "=(?<v>[^;]+)", RegexOptions.None, TimeSpan.FromSeconds(1));
        var signedIn = m.Success && _sessions.Contains(m.Groups["v"].Value);
        var ajax = request.Headers.Contains("Wicket-Ajax");
        var form = HttpUtility.ParseQueryString(body);

        if (uri.Host == "access.ing.de") return Access(request.Method, x, form, ajax);
        if (uri.AbsolutePath == "/app/login/oauth2/code/ibbr")
        {
            var id = $"s{++_counter}-secretvalue";
            _sessions.Add(id);
            var r = Redirect(Banking + "/app/postlogin/wait?x=w1");
            r.Headers.Add("Set-Cookie", $"{SessionCookie}={id}; Path=/; Secure; HttpOnly");
            return r;
        }
        if (!signedIn) return Redirect("https://access.ing.de/delogin/w/login?t=T1&login_hint");

        return uri.AbsolutePath switch
        {
            "/app/obligo" or "/app/obligo/" => Redirect(Banking + "/app/obligo/obligo?x=ov"),
            "/app/postlogin/wait" => Html(Page("<p>Einen Moment bitte</p>",
                "Wicket.Event.add(window, \"load\", function(event) { window.location.replace('https://banking.ing.de/app/postlogin/process?x=p1');;});")),
            "/app/postlogin/process" => Redirect(Banking + "/app/obligo/obligo?x=ov"),
            "/app/obligo/obligo" => Html(Overview()),
            "/app/obligo/postbox" => Redirect(Banking + "/app/postbox/postbox?x=box1"),
            "/app/obligo/umsatzanzeige" => Umsatz(request.Method, x, form, ajax),
            "/app/postbox/postbox" => Postbox(request.Method, x, form, ajax),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
    }

    // ---- sign-in (access.ing.de, Wicket) ----

    private HttpResponseMessage Access(HttpMethod method, string x, System.Collections.Specialized.NameValueCollection form, bool ajax)
    {
        if (method == HttpMethod.Get && x.Length == 0) return Shown(LoginPage());
        if (method == HttpMethod.Post && x == "submit")
        {
            if (form["view:alias:alias"] != Alias || form["view:secret:secret"] != Pin || form["view:p"] != "p") return Html(LoginPage());
            _polls = 0;
            if (Devices.Count > 1) return Shown(DevicePage());
            CreateApproval();
            return Html(ApprovalPage());
        }
        if (method == HttpMethod.Post && x == "device")
        {
            var index = int.Parse(form["view:auth:deviceSelection:device:device"] ?? "-1", System.Globalization.CultureInfo.InvariantCulture);
            ChosenDevice = Devices[index];
            if (form["buttons:next"] == null) return new HttpResponseMessage(HttpStatusCode.BadRequest);
            CreateApproval();
            return Html(ApprovalPage());
        }
        if (x == "status" && ajax)
        {
            if (Reject) return Xml("<ajax-response><component id=\"id1\"><![CDATA[<p>Die Freigabe wurde abgelehnt.</p>]]></component></ajax-response>");
            if (++_polls <= PendingPolls) return Xml($"<?xml version=\"1.0\" encoding=\"UTF-8\"?><ajax-response><evaluate><![CDATA[{Timer()}]]></evaluate></ajax-response>");
            return Xml("<ajax-response><redirect><![CDATA[https://banking.ing.de/app/login/oauth2/code/ibbr?code=c1&state=s1]]></redirect></ajax-response>");
        }
        return new HttpResponseMessage(HttpStatusCode.BadRequest);
    }

    private HttpResponseMessage Shown(string html)
    {
        _pageShownAt = DateTime.UtcNow;
        return Html(html);
    }

    private void CreateApproval()
    {
        if (DateTime.UtcNow - _pageShownAt >= TimeSpan.FromMilliseconds(RequestBlockMs)) Pushes++;
        else SilentApprovals++;
    }

    private string Blocker() =>
        $"$(document).ready(function() {{ $(document).submit(function(event) {{ return RequestBlocker.checkEvent(event, {RequestBlockMs}); }}); }});";

    private static string Timer() =>
        "<script type=\"text/javascript\"> /*<![CDATA[*/ (function(){Wicket.Timer.set('id1e.0', function(){Wicket.Ajax.ajax({\"u\":\"./login?x=status&t=T1\",\"c\":\"id1e\"});}, 3000);})(); /*]]]]><![CDATA[>*/ </script>";

    private string LoginPage() => Page(
        "<form id=\"id9\" method=\"post\" action=\"./login?x=submit&amp;t=T1\" novalidate=\"novalidate\"><input type=\"hidden\" value=\"p\" name=\"view:p\"/>"
        + "<input class=\"input-field form-group--error-field\" type=\"text\" value=\"\" name=\"view:alias:alias\" id=\"id5\" autocomplete=\"username\"/>"
        + "<input class=\"input-field form-group--error-field\" type=\"password\" value=\"\" name=\"view:secret:secret\" id=\"id6\"/>"
        + "<button class=\"button orange\" role=\"button\" name=\"view:next-inline\" id=\"id8\">Weiter</button></form>", Blocker());

    private string DevicePage()
    {
        var radios = string.Concat(Devices.Select((d, i) =>
            $"<label class=\"form-group__controls-radio\"> <input class=\"input-field__radio\" type=\"radio\" id=\"idr{i}\" name=\"view:auth:deviceSelection:device:device\" value=\"{i}\"{(i == 0 ? " checked=\"checked\"" : "")}/> <span class=\"input-field__radio--button\"> <span class=\"input-field__radio--checked\"></span> </span> <span class=\"input-field__radio--label\">{d}</span> </label>"));
        return Page($"<form id=\"id9\" method=\"post\" action=\"./login?x=device&amp;t=T1\">{radios}<button class=\"button orange\" type=\"submit\" name=\"buttons:next\" id=\"id17\">Weiter</button><button type=\"submit\" name=\"buttons:cancel\" id=\"id1c\">Abbrechen</button></form>", Blocker());
    }

    private static string ApprovalPage() => Page(
        "<p>Bitte bestätigen Sie in Ihrer Banking to go App.</p><a class=\"link-standard--g2p\" id=\"id16\" href=\"#\">Probleme?</a>",
        "Wicket.Ajax.ajax({\"u\":\"./login?x=open&t=T1\",\"c\":\"id16\",\"e\":\"click\",\"pd\":true});" + Timer());

    // ---- overview ----

    private static string Row(string href, string headline, string? product, string iban, string amount) =>
        $"<div class=\"g2p-account__row-wrap\"> <a class=\"g2p-account__row\" href=\"{href}\" id=\"idr{iban[^2..]}\"> <span class=\"g2p-account__details\"> <span class=\"g2p-account__headline\"> <span>{headline}</span> "
        + (product == null ? "" : $"<span class=\"g2p-banking-header__account__type\"> (<span>{product}</span>) </span>")
        + $" </span> <span class=\"g2p-account__type\">Alex Example</span> <span class=\"g2p-account__iban\">{iban}</span> <ing-tooltip has-arrow> <button slot=\"invoker\" type=\"button\" class=\"g2p-iban-copy\"></button> </ing-tooltip> </span>"
        + $" <span class=\"u-visuallyhidden u-block\">Betrag {amount} Euro</span> <span class=\"g2p-account__balance\"> <span>0</span>€ </span> </a> </div>";

    private static string Overview() => Page(
        "<a class=\"quicklinks__postbox\" id=\"id233\" href=\"./postbox?x=pb\"> Postbox </a>"
        + Row("./umsatzanzeige?x=giro", "Alltag", "Girokonto", "DE00 0000 0000 0000 0000 11", "955,11")
        + Row("./umsatzanzeige?x=vl", "VL-Sparen", null, "DE00 0000 0000 0000 0000 77", "1.040,00")
        + Row("./umsatzanzeige?x=credit", "Rahmenkredit", null, "DE00 0000 0000 0000 0000 99", "1.500,00"),
        "window.addEventListener('ingde-sn:logout-clicked', function() { window.location.replace('https://banking.ing.de/app/logout'); });");

    // ---- transactions (CSV export) ----

    private HttpResponseMessage Umsatz(HttpMethod method, string x, System.Collections.Specialized.NameValueCollection form, bool ajax)
    {
        switch (x)
        {
            case "vl" or "vl2" or "credit" or "credit2" when method == HttpMethod.Get:
                _opened = x.TrimEnd('2');
                return Html(Page(
                    "<form class=\"u-print-hidden\" id=\"idf\" method=\"post\" action=\"./umsatzanzeige?x=filter\"><input type=\"search\" value=\"\" name=\"suchbegriff\"/>"
                    + "<input class=\"input-field picker\" type=\"text\" value=\"09.10.2026\" name=\"filterAnzeige:datum:von:input\" id=\"idvon\" data-date-start-date=\"15.10.2025\" data-date-end-date=\"09.11.2026\"/>"
                    + "<input class=\"input-field picker\" type=\"text\" value=\"09.11.2026\" name=\"filterAnzeige:datum:bis:input\" id=\"idbis\"/>"
                    + "<button type=\"submit\" name=\"filterAnzeige:ergebnisseAnzeigenButton\" id=\"idok\">Filter anwenden</button></form>"
                    + "<a class=\"button-text-indigo\" id=\"idexp\" href=\"#\">Exportieren</a>",
                    "Wicket.Ajax.ajax({\"u\":\"./umsatzanzeige?x=export\",\"c\":\"idexp\",\"e\":\"click\",\"pd\":true});"));
            case "filter" when method == HttpMethod.Post:
                Filters.Add((form["filterAnzeige:datum:von:input"] ?? "", form["filterAnzeige:datum:bis:input"] ?? ""));
                return Redirect(Banking + "/app/obligo/umsatzanzeige?x=" + _opened + "2");
            case "export" when ajax:
                return Xml("<?xml version=\"1.0\" encoding=\"UTF-8\"?><ajax-response><component id=\"idm\"><![CDATA[<div class=\"panel-wrap\"> <form class=\"panel panel--modal\" id=\"idform\" method=\"post\" action=\"./umsatzanzeige?x=modal\"><div id=\"idform_hf_0\" hidden=\"\" class=\"hidden-fields\"></div>"
                    + "<label class=\"label--radio\"> <input type=\"radio\" class=\"input-field__radio\" id=\"r7\" name=\"content-container:content:exportTypes\" value=\"radio7\" checked=\"checked\"> <span class=\"input-field__radio--label\">PDF</span> </label>"
                    + "<label class=\"label--radio\"> <input type=\"radio\" class=\"input-field__radio\" id=\"r8\" name=\"content-container:content:exportTypes\" value=\"radio8\"> <span class=\"input-field__radio--label\">CSV (Excel)</span> </label>"
                    + "<a id=\"idgo\" href=\"#\" class=\"button orange\" role=\"button\">Jetzt exportieren</a></form></div>]]></component><evaluate><![CDATA[<script>"
                    + "(function(){Wicket.Ajax.ajax({\"u\":\"./umsatzanzeige?x=close\",\"c\":\"idclose\",\"e\":\"click\",\"pd\":true});})();"
                    + "(function(){Wicket.Ajax.ajax({\"u\":\"./umsatzanzeige?x=doexport\",\"m\":\"POST\",\"c\":\"idgo\",\"f\":\"idform\",\"sc\":\"buttons:export\",\"e\":\"click\",\"pd\":true});})();</script>]]></evaluate></ajax-response>");
            case "doexport" when ajax && method == HttpMethod.Post:
                if (form["content-container:content:exportTypes"] != "radio8" || form["buttons:export"] != "1") return new HttpResponseMessage(HttpStatusCode.BadRequest);
                return Xml("<ajax-response><evaluate><![CDATA[<script>(function(){Wicket.AjaxDownload.initiate({\"attributes\":{\"u\":\"./umsatzanzeige?x=s1\"},\"name\":\"dl\",\"downloadUrl\":\"./umsatzanzeige?x=csv\",\"method\":\"blob\"});})();</script>]]></evaluate></ajax-response>");
            case "csv":
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.Latin1.GetBytes(_opened == "credit" ? CreditCsv : Csv)) };
            default:
                return new HttpResponseMessage(HttpStatusCode.BadRequest);
        }
    }

    // ---- Postbox ----

    private HttpResponseMessage Postbox(HttpMethod method, string x, System.Collections.Specialized.NameValueCollection form, bool ajax)
    {
        if (x.StartsWith("dl-", StringComparison.Ordinal))
        {
            var doc = Documents.First(d => d.Id == x[3..]);
            Downloads[doc.Id] = Downloads.GetValueOrDefault(doc.Id) + 1;
            if (doc.Broken) return Html(Page("<p>Fehler</p>"));
            var content = new ByteArrayContent(Encoding.ASCII.GetBytes("%PDF-1.4 example " + doc.Id));
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/pdf");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }
        if (x is "box1" or "box2" && method == HttpMethod.Get)
            return Html(PostboxPage(x == "box1" ? 1 : 2));
        if (x.StartsWith("range-", StringComparison.Ordinal) && ajax && method == HttpMethod.Post)
        {
            var range = form["erweiterterFilter:zeitraum:zeitraum"] ?? "";
            Ranges.Add(range);
            var months = range == "MONATE120" ? 120 : 12;
            var box = x == "range-box1" ? 1 : 2;
            _dateMode[box] = range == "DATUM";
            if (range == "DATUM") return Xml($"<?xml version=\"1.0\" encoding=\"UTF-8\"?><ajax-response><component id=\"idpf\"><![CDATA[{Filter(box, true)}]]></component></ajax-response>");
            return Xml($"<?xml version=\"1.0\" encoding=\"UTF-8\"?><ajax-response><component id=\"idt\"><![CDATA[{Chips(box)}{Table(box, months)}]]></component></ajax-response>");
        }
        if (x.StartsWith("search", StringComparison.Ordinal) && method == HttpMethod.Post)
        {
            // Like ING: a plain submit of the filter form is ignored; the page comes back unfiltered.
            var plain = x == "search1" ? 1 : 2;
            return Html(Page(Chips(plain) + Filter(plain, false) + Table(plain, 12), RangeScript(plain)));
        }
        if (x.StartsWith("filter-box", StringComparison.Ordinal) && ajax && method == HttpMethod.Post)
        {
            var box = x == "filter-box1" ? 1 : 2;
            if (form["erweiterterFilter:filtern"] == null || form["erweiterterFilter:zeitraum:zeitraum"] != "DATUM" || !_dateMode.GetValueOrDefault(box))
                return new HttpResponseMessage(HttpStatusCode.BadRequest); // Wicket ignores the date fields while they are disabled
            var from = DateOnly.ParseExact(form["erweiterterFilter:zeitraum:dateFields:von:datePanel:dateInput"] ?? "", "dd.MM.yyyy", System.Globalization.CultureInfo.InvariantCulture);
            var to = DateOnly.ParseExact(form["erweiterterFilter:zeitraum:dateFields:bis:datePanel:dateInput"] ?? "", "dd.MM.yyyy", System.Globalization.CultureInfo.InvariantCulture);
            Ranges.Add($"{from:yyyy-MM-dd}..{to:yyyy-MM-dd}");
            return Xml($"<?xml version=\"1.0\" encoding=\"UTF-8\"?><ajax-response><component id=\"idt\"><![CDATA[{Table(box, from, to)}]]></component></ajax-response>");
        }
        return new HttpResponseMessage(HttpStatusCode.BadRequest);
    }

    /// <summary>Like ING, a listing shows at most this many documents, the newest of the range, and has no paging.</summary>
    public int ListingCap { get; set; } = 500;

    /// <summary>Whether a postbox tab offers the "Freier Zeitraum" filter (older page versions did not).</summary>
    public bool DateFilter { get; set; } = true;

    private readonly Dictionary<int, bool> _dateMode = [];

    private string Filter(int box, bool dates)
    {
        var disabled = dates ? "" : " disabled=\"disabled\"";
        return $"<form id=\"idpf\" method=\"post\" action=\"./postbox?x=search{box}\"><select name=\"erweiterterFilter:zeitraum:zeitraum\" class=\"select\" id=\"idsel{box}\">"
            + $"<option value=\"MONATE3\">3 Monate</option><option{(dates ? "" : " selected=\"selected\"")} value=\"MONATE12\">1 Jahr</option><option value=\"MONATE120\">max. Zeitraum (10 Jahre)</option>"
            + (DateFilter ? $"<option{(dates ? " selected=\"selected\"" : "")} value=\"DATUM\">Freier Zeitraum</option>" : "") + "</select>"
            + (DateFilter
                ? $"<input class=\"input-field\" type=\"text\" value=\"09.11.2025\" name=\"erweiterterFilter:zeitraum:dateFields:von:datePanel:dateInput\"{disabled} data-date-start-date=\"09.11.2016\" data-date-end-date=\"09.11.2026\"/>"
                  + $"<input class=\"input-field\" type=\"text\" value=\"09.11.2026\" name=\"erweiterterFilter:zeitraum:dateFields:bis:datePanel:dateInput\"{disabled} data-date-start-date=\"09.11.2016\" data-date-end-date=\"09.11.2026\"/>"
                  + "<select name=\"erweiterterFilter:kontenFilter\"><option selected=\"selected\" value=\"0\">Alle</option></select>"
                  + $"<button type=\"submit\" class=\"button indigo\" name=\"erweiterterFilter:filtern\" id=\"idfb{box}\">Filter anwenden</button>"
                : "")
            + "</form>";
    }

    private static string RangeScript(int box) =>
        $"Wicket.Ajax.ajax({{\"u\":\"./postbox?x=range-box{box}\",\"m\":\"POST\",\"c\":\"idsel{box}\",\"e\":\"change\"}});"
        + $"Wicket.Ajax.ajax({{\"u\":\"./postbox?x=filter-box{box}\",\"m\":\"POST\",\"c\":\"idfb{box}\",\"e\":\"click\",\"f\":\"idpf\",\"sc\":\"erweiterterFilter:filtern\"}});";

    private static string Chips(int box) =>
        "<div class=\"chips-nav\">"
        + $" <a class=\"button outline chip font14{(box == 1 ? " selected" : "")}\" href=\"./postbox?x=box1\"> Alex Example </a>"
        + $" <a class=\"button outline chip font14{(box == 2 ? " selected" : "")}\" href=\"./postbox?x=box2\"> Alex Example &amp; Sam Example </a> </div>";

    private string Table(int box, int months) => Table(box, new DateOnly(2026, 11, 9).AddMonths(-months), DateOnly.MaxValue);

    private string Table(int box, DateOnly from, DateOnly to)
    {
        var rows = Documents.Where(d => d.Box == box && d.Date >= from && d.Date <= to).OrderByDescending(d => d.Date).Take(ListingCap).Select(d =>
            $"<div class=\"ibbr-table-row gs-span-20 postbox-message\" role=\"row\" id=\"idrow{d.Id}\"> <span role=\"cell\" class=\"ibbr-table-cell postbox-grid-left gs-span-6\"> <span class=\"postbox-indicator\" aria-label=\"Gelesen\"></span> <span>{d.Type}</span> </span>"
            + $" <span role=\"cell\" class=\"ibbr-table-cell postbox-grid-description gs-span-10\"> {d.Subject} </span> <span role=\"cell\" class=\"ibbr-table-cell postbox-grid-right gs-span-3\">{d.Date:dd.MM.yyyy}</span>"
            + $" <div class=\"action-bar--buttons\"> <span role=\"list\"> <a type=\"listitem\" id=\"iddl{d.Id}\" href=\"./postbox?x=dl-{d.Id}\" class=\"button outline indigo font14\" role=\"button\" data-no-busy-indicator>Download</a> <a type=\"listitem\" href=\"#\" class=\"button\">Archivieren</a> </span> </div> </div>");
        return "<div class=\"ibbr-table\" role=\"table\"><div class=\"ibbr-table-body\">" + string.Concat(rows) + "</div></div>";
    }

    private string PostboxPage(int box)
    {
        _dateMode[box] = false;
        return Page(Chips(box) + Filter(box, false) + Table(box, 12), RangeScript(box));
    }

    // ---- plumbing ----

    private static string Page(string body, string script = "") =>
        "<!DOCTYPE HTML> <html lang=\"de\"><head><title>ING</title></head><body>" + body
        + "<script type=\"text/javascript\"> /*<![CDATA[*/ " + script + " /*]]>*/ </script></body></html>";

    private static HttpResponseMessage Html(string html) =>
        new(HttpStatusCode.OK) { Content = new StringContent(html, Encoding.UTF8, "text/html") };

    private static HttpResponseMessage Xml(string xml) =>
        new(HttpStatusCode.OK) { Content = new StringContent(xml, Encoding.UTF8, "text/xml") };

    private static HttpResponseMessage Redirect(string to)
    {
        var r = new HttpResponseMessage(HttpStatusCode.Found);
        r.Headers.Location = new Uri(to);
        return r;
    }
}
