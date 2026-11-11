#pragma warning disable CA2000 // test doubles: lifetimes are owned by the HTTP client
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenBudget.Connectors;
using OpenBudget.Connectors.FakeBank;

namespace OpenBudget.Tests.Connectors;

/// <summary>
/// A made-up Baader customer portal: the observed endpoints and answer shapes (fstatus, mailrows per document type and
/// period, maildownload redirecting to a storage host) with invented customers and documents.
/// </summary>
internal sealed partial class FakeBaaderPortal : HttpMessageHandler
{
    public const string Host = "konto.baaderbank.de";
    public const string StorageHost = "storage.example.test";
    public const string CustomerId = "4711";
    public const string SessionCookie = "PHPSESSID";
    public const string PageToken = "page-token-1";

    private readonly Lock _lock = new();
    private readonly HashSet<string> _sessions = [];
    private int _counter;

    public sealed record Doc(string Section, DateOnly Date, string Title, string Id);

    public List<Doc> Documents { get; } = [];

    public List<string> Requests { get; } = [];

    /// <summary>The form bodies of the list requests, in order.</summary>
    public List<Dictionary<string, string>> ListRequests { get; } = [];

    public Dictionary<string, int> Downloads { get; } = new(StringComparer.Ordinal);

    /// <summary>Sections answered with a real (not "no documents") error.</summary>
    public HashSet<string> FailingSections { get; } = [];

    /// <summary>Documents whose download serves an HTML page instead of a PDF.</summary>
    public HashSet<string> BrokenDownloads { get; } = [];

    public BankHttpClient CreateClient() => new(jar => new CookieRedirectHandler(jar, this));

    /// <summary>Like the real portal: a session works only from the browser that signed in (requests carrying the browser's fetch headers).</summary>
    public bool BrowserOnly { get; set; }

    /// <summary>Portal requests that came from the (fake) browser.</summary>
    public int BrowserRequests { get; private set; }

    /// <summary>A signed-in browser kept after the web login: its requests carry the browser's fetch headers and its own session.</summary>
    public FakePortalBrowser SignInBrowser()
    {
        var http = CreateClient();
        http.ImportCookies([SignIn()]);
        return new FakePortalBrowser(http);
    }

    internal sealed class FakePortalBrowser : IBrowserFetcher, IDisposable
    {
        private readonly BankHttpClient _http;

        public FakePortalBrowser(BankHttpClient http) => _http = http;

        public bool IsOpen { get; set; } = true;

        public int Resolved { get; private set; }

        /// <summary>When set, downloads (redirects to the storage host) are not readable by the page, as for a storage without CORS.</summary>
        public bool StorageBlocked { get; set; }

        public async Task<BrowserFetchResponse> SendAsync(BrowserFetchRequest request, CancellationToken ct)
        {
            if (!IsOpen) throw new ConnectorException("closed");
            if (StorageBlocked && request.Url.AbsolutePath.StartsWith("/maildownload/", StringComparison.Ordinal))
                return new BrowserFetchResponse(0, null, null, []);
            using var message = new HttpRequestMessage(new HttpMethod(request.Method), request.Url);
            if (request.Body != null)
                message.Content = new StringContent(request.Body, Encoding.UTF8, "application/x-www-form-urlencoded");
            foreach (var (k, v) in request.Headers)
            {
                if (!k.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) message.Headers.TryAddWithoutValidation(k, v);
            }
            message.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
            using var response = await _http.SendAsync(message, ct);
            return new BrowserFetchResponse((int)response.StatusCode, response.RequestMessage?.RequestUri, response.Content.Headers.ContentType?.MediaType,
                await response.Content.ReadAsByteArrayAsync(ct));
        }

        public Task<Uri?> ResolveRedirectAsync(Uri url, CancellationToken ct)
        {
            Resolved++;
            var id = url.AbsolutePath["/maildownload/".Length..];
            return Task.FromResult<Uri?>(new Uri($"https://{StorageHost}/bucket/{id}.pdf?X-Amz-Signature=sig"));
        }

        public void Dispose() => _http.Dispose();
    }

    /// <summary>What a sign-in in the browser leaves behind.</summary>
    public CookieState SignIn()
    {
        lock (_lock)
        {
            var id = $"sess-{++_counter}-secretvalue";
            _sessions.Add(id);
            return new CookieState(SessionCookie, id, Host, "/", null, true, true, "Lax");
        }
    }

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
        Requests.Add($"{request.Method} {uri.Host}{uri.AbsolutePath}");
        if (uri.Host == StorageHost) return Storage(uri);

        var cookie = request.Headers.TryGetValues("Cookie", out var c) ? string.Join(';', c) : "";
        var m = SessionPattern().Match(cookie);
        var signedIn = m.Success && _sessions.Contains(m.Groups["v"].Value)
            && (!BrowserOnly || request.Headers.Contains("Sec-Fetch-Mode"));
        if (request.Headers.Contains("Sec-Fetch-Mode")) BrowserRequests++;
        var path = uri.AbsolutePath;

        if (path is "/login" or "/") return Html("<form action=\"/login\" method=\"post\" id=\"loginform\"><input name=\"user\"><input name=\"password\"></form>");
        if (!signedIn) return Redirect($"/login?referer={Uri.EscapeDataString(path)}");
        if (path == "/fstatus")
            return Json(new[] { new Dictionary<string, object>(StringComparer.Ordinal) { ["customer_id"] = int.Parse(CustomerId, CultureInfo.InvariantCulture), ["first_name"] = "Alice", ["last_name"] = "Example", ["amount"] = "0,00", ["currency"] = "EUR" } });
        if (path == "/dashboard") return Html($"<html><head><meta content=\"{PageToken}\" name=\"infotok\" /></head><body>Dashboard</body></html>");
        if (path == "/mailrows/" + CustomerId && request.Method == HttpMethod.Post) return MailRows(body);
        if (path.StartsWith("/maildownload/", StringComparison.Ordinal))
        {
            var id = path["/maildownload/".Length..];
            if (Documents.All(d => d.Id != id)) return new HttpResponseMessage(HttpStatusCode.NotFound);
            var redirect = new HttpResponseMessage(HttpStatusCode.Found);
            redirect.Headers.Location = new Uri($"https://{StorageHost}/bucket/{id}.pdf?X-Amz-Signature=sig");
            return redirect;
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private HttpResponseMessage MailRows(string body)
    {
        var form = body.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p.Length > 1 ? p[1].Replace('+', ' ') : ""), StringComparer.Ordinal);
        ListRequests.Add(form);
        var section = form.GetValueOrDefault("section") ?? "";
        var from = DateOnly.ParseExact(form["from"], "dd.MM.yyyy", CultureInfo.InvariantCulture);
        var to = DateOnly.ParseExact(form["to"], "dd.MM.yyyy", CultureInfo.InvariantCulture);
        if (FailingSections.Contains(section))
            return Json(new { @do = "error", title = "Der Dienst ist derzeit nicht verfügbar", detail = "Bitte später erneut versuchen." });
        var docs = Documents.Where(d => d.Section == section && d.Date >= from && d.Date <= to).ToList();
        if (docs.Count == 0)
        {
            return Json(new
            {
                @do = "error",
                title = $"Aktuell sind in Ihrem Postfach keine Dokumente hinterlegt für den ausgewählten Zeitraum {from:dd.MM.yyyy} - {to:dd.MM.yyyy}",
                detail = "Bitte passen Sie Ihre Filtereinstellungen an.",
            });
        }
        var byDay = docs.OrderByDescending(d => d.Date).GroupBy(d => d.Date)
            .ToDictionary(g => g.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), g => g.Select(d => new object[] { 1, d.Title, d.Id }).ToArray(), StringComparer.Ordinal);
        return Json(new[] { byDay });
    }

    private HttpResponseMessage Storage(Uri uri)
    {
        var id = uri.AbsolutePath["/bucket/".Length..^".pdf".Length];
        Downloads[id] = Downloads.GetValueOrDefault(id) + 1;
        if (BrokenDownloads.Contains(id)) return Html("<html>Access denied</html>");
        var content = new ByteArrayContent(FakePdf.Create("Document " + id));
        content.Headers.ContentType = new("application/octet-stream");
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static HttpResponseMessage Redirect(string location)
    {
        var r = new HttpResponseMessage(HttpStatusCode.Found);
        r.Headers.Location = new Uri(location, UriKind.Relative);
        return r;
    }

    private static HttpResponseMessage Html(string html) =>
        new(HttpStatusCode.OK) { Content = new StringContent(html, Encoding.UTF8, "text/html") };

    private static HttpResponseMessage Json(object value) =>
        new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };

    [GeneratedRegex(SessionCookie + "=(?<v>[^;]+)", RegexOptions.None, 2000)]
    private static partial Regex SessionPattern();
}
