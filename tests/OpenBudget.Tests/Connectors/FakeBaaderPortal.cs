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
/// A made-up Baader customer portal with the observed protocol: a session that only works from the browser that signed in
/// (requests carrying the browser's Sec-Fetch-Mode header), <c>POST /mailrows</c> with the recent documents by default,
/// a session filter (<c>POST /mail</c>) whose extended listing stays empty until an SMS-TAN went to <c>/mfa/check</c>, and
/// downloads redirecting to a pre-signed storage URL on another host. Customers and documents are invented.
/// </summary>
internal sealed partial class FakeBaaderPortal : HttpMessageHandler
{
    public const string Host = "konto.baaderbank.de";
    public const string StorageHost = "storage.example.test";
    public const string CustomerId = "4711";
    public const string SessionCookie = "PHPSESSID";
    public const string PageToken = "page-token-1";
    public const string Tan = "246810";
    public const string Signature = "sig-secret-1";

    /// <summary>How many documents the default listing shows.</summary>
    public const int RecentCount = 3;

    private readonly Lock _lock = new();
    private readonly Dictionary<string, SessionState> _sessions = new(StringComparer.Ordinal);
    private int _counter;

    private sealed class SessionState
    {
        public int? Period { get; set; }

        public bool TanPassed { get; set; }
    }

    public sealed record Doc(DateOnly Date, string Title, string Id);

    public List<Doc> Documents { get; } = [];

    public List<string> Requests { get; } = [];

    /// <summary>The fields of each filter form post, in order.</summary>
    public List<Dictionary<string, string>> FilterPosts { get; } = [];

    /// <summary>The fields of each TAN check, in order.</summary>
    public List<Dictionary<string, string>> TanChecks { get; } = [];

    public int ListRequests { get; private set; }

    public Dictionary<string, int> Downloads { get; } = new(StringComparer.Ordinal);

    /// <summary>Documents whose download serves an HTML page instead of a PDF.</summary>
    public HashSet<string> BrokenDownloads { get; } = [];

    /// <summary>The client the connector downloads storage links with (no portal session needed there).</summary>
    public BankHttpClient CreateClient() => new(jar => new CookieRedirectHandler(jar, this));

    public DateOnly Today { get; set; } = new(2026, 11, 10);

    /// <summary>A signed-in browser kept after the web login.</summary>
    public FakePortalBrowser SignInBrowser()
    {
        string id;
        lock (_lock)
        {
            id = $"sess-{++_counter}-secretvalue";
            _sessions[id] = new SessionState();
        }
        return new FakePortalBrowser(this, id);
    }

    public void ExpireSessions()
    {
        lock (_lock) _sessions.Clear();
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (_lock) return Task.FromResult(Handle(request, ReadForm(request)));
    }

    private static Dictionary<string, string> ReadForm(HttpRequestMessage request)
    {
        var form = new Dictionary<string, string>(StringComparer.Ordinal);
        switch (request.Content)
        {
            case MultipartFormDataContent multipart:
                foreach (var part in multipart)
                    form[part.Headers.ContentDisposition!.Name!.Trim('"')] = part.ReadAsStringAsync().GetAwaiter().GetResult();
                form["__multipart"] = "1";
                break;
            case { } content:
                foreach (var p in content.ReadAsStringAsync().GetAwaiter().GetResult().Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2)))
                    form[Uri.UnescapeDataString(p[0])] = Uri.UnescapeDataString(p.Length > 1 ? p[1].Replace('+', ' ') : "");
                break;
            default:
                break;
        }
        return form;
    }

    private HttpResponseMessage Handle(HttpRequestMessage request, Dictionary<string, string> form)
    {
        var uri = request.RequestUri!;
        Requests.Add($"{request.Method} {uri.Host}{uri.AbsolutePath}");
        if (uri.Host == StorageHost) return Storage(uri);

        var cookie = request.Headers.TryGetValues("Cookie", out var c) ? string.Join(';', c) : "";
        var m = SessionPattern().Match(cookie);
        var session = m.Success && request.Headers.Contains("Sec-Fetch-Mode") ? _sessions.GetValueOrDefault(m.Groups["v"].Value) : null;
        var path = uri.AbsolutePath;

        if (path is "/login" or "/") return Html("<form id=\"loginform\"><input name=\"user\"><input name=\"password\"></form>");
        if (session == null) return Redirect("/"); // a session the portal does not accept ends on its start page
        if (path == "/fstatus")
            return Json(new[] { new Dictionary<string, object>(StringComparer.Ordinal) { ["customer_id"] = int.Parse(CustomerId, CultureInfo.InvariantCulture), ["first_name"] = "Alice", ["last_name"] = "Example", ["amount"] = "0,00", ["currency"] = "EUR" } });
        if (path is "/dashboard" or "/mail" && request.Method == HttpMethod.Get) return Html(Page(""));
        if (path == "/mail" && request.Method == HttpMethod.Post)
        {
            FilterPosts.Add(form);
            if (form.GetValueOrDefault("filter") != "1" || form.GetValueOrDefault("token") != PageToken) return Html(Page(""));
            session.Period = int.Parse(form["periods"], CultureInfo.InvariantCulture);
            return Html(Page(session.TanPassed ? "" : "<form id=\"mfaInputForm\"><input name=\"tan\"></form>"));
        }
        if (path == "/mfa/check" && request.Method == HttpMethod.Post)
        {
            TanChecks.Add(form);
            if (form.GetValueOrDefault("__multipart") != "1" || form.GetValueOrDefault("token") != PageToken)
                return new HttpResponseMessage(HttpStatusCode.Forbidden);
            if (form.GetValueOrDefault("tan") != Tan) return Json(new { @do = "error", title = "TAN falsch" });
            session.TanPassed = true;
            return Json(new { @do = "success" });
        }
        if (path == "/mailrows" && request.Method == HttpMethod.Post)
        {
            ListRequests++;
            if (form.GetValueOrDefault("__multipart") != "1") return new HttpResponseMessage(HttpStatusCode.BadRequest);
            IEnumerable<Doc> docs;
            if (session.Period is { } days)
                docs = session.TanPassed ? Documents.Where(d => d.Date >= Today.AddDays(-days)) : [];
            else
                docs = Documents.OrderByDescending(d => d.Date).Take(RecentCount);
            var byDay = docs.OrderByDescending(d => d.Date).GroupBy(d => d.Date)
                .ToDictionary(g => g.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), g => g.Select(d => new object[] { 1, d.Title, d.Id }).ToArray(), StringComparer.Ordinal);
            return byDay.Count == 0 ? Json(Array.Empty<object>()) : Json(new[] { byDay });
        }
        if (path.StartsWith("/maildownload/", StringComparison.Ordinal))
        {
            var id = path["/maildownload/".Length..];
            if (Documents.All(d => d.Id != id)) return new HttpResponseMessage(HttpStatusCode.NotFound);
            var redirect = new HttpResponseMessage(HttpStatusCode.Found);
            redirect.Headers.Location = new Uri($"https://{StorageHost}/PDF/{id}.pdf?X-Amz-Signature={Signature}");
            return redirect;
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static string Page(string body) => $"<html><head><meta name=\"infotok\" content=\"{PageToken}\" /></head><body>{body}</body></html>";

    private HttpResponseMessage Storage(Uri uri)
    {
        var id = uri.AbsolutePath["/PDF/".Length..^".pdf".Length];
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

    /// <summary>
    /// The browser kept after the web login: its requests carry the session cookie and the browser's fetch headers. Like the
    /// real page, it cannot read the storage host's answer (no CORS), but it can read a redirect's address.
    /// </summary>
    internal sealed class FakePortalBrowser : IBrowserFetcher, IDisposable
    {
        private readonly string _session;
        private readonly HttpMessageInvoker _invoker;

        public FakePortalBrowser(FakeBaaderPortal portal, string session)
        {
            _session = session;
            _invoker = new HttpMessageInvoker(portal, disposeHandler: false);
        }

        public bool IsOpen { get; set; } = true;

        public int Resolved { get; private set; }

        private HttpRequestMessage Message(string method, Uri url)
        {
            var message = new HttpRequestMessage(new HttpMethod(method), url);
            message.Headers.TryAddWithoutValidation("Cookie", $"{SessionCookie}={_session}");
            message.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
            return message;
        }

        public async Task<BrowserFetchResponse> SendAsync(BrowserFetchRequest request, CancellationToken ct)
        {
            if (!IsOpen) throw new ConnectorException("closed");
            var url = request.Url;
            var method = request.Method;
            HttpContent? content = null;
            if (request.Form != null)
            {
                var multipart = new MultipartFormDataContent();
                foreach (var (k, v) in request.Form) multipart.Add(new StringContent(v), k);
                content = multipart;
            }
            else if (request.Body != null)
            {
                content = new StringContent(request.Body, Encoding.UTF8, "application/x-www-form-urlencoded");
            }
            for (var hop = 0; hop < 10; hop++)
            {
                using var message = Message(method, url);
                message.Content = content;
                foreach (var (k, v) in request.Headers)
                {
                    if (!k.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) message.Headers.TryAddWithoutValidation(k, v);
                }
                using var response = await _invoker.SendAsync(message, ct);
                if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
                {
                    url = new Uri(url, location);
                    if (url.Host != Host) return new BrowserFetchResponse(0, null, null, []); // another origin without CORS
                    method = "GET";
                    content = null;
                    continue;
                }
                return new BrowserFetchResponse((int)response.StatusCode, url, response.Content.Headers.ContentType?.MediaType,
                    await response.Content.ReadAsByteArrayAsync(ct));
            }
            throw new InvalidOperationException("too many redirects");
        }

        public async Task<Uri?> ResolveRedirectAsync(Uri url, CancellationToken ct)
        {
            if (!IsOpen) throw new ConnectorException("closed");
            Resolved++;
            using var message = Message("GET", url);
            using var response = await _invoker.SendAsync(message, ct);
            return (int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location ? new Uri(url, location) : null;
        }

        public void Dispose() => _invoker.Dispose();
    }
}
