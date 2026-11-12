using System.Net;

namespace OpenBudget.Connectors;

/// <summary>
/// HTTP access for connectors (web-based banks): a client with a cookie jar whose state can be
/// exported into the persisted <see cref="ConnectorSession"/> and restored at the next login.
/// </summary>
public interface IBankHttpClient
{
    Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct);

    /// <summary>
    /// Sends without the session: no cookies from the jar, no browser identity (protocols like FinTS that carry their
    /// own dialog state; a bank's firewall may drop requests that bring along a web-banking session).
    /// </summary>
    Task<HttpResponseMessage> SendWithoutSessionAsync(HttpRequestMessage request, CancellationToken ct);

    Task<string> GetStringAsync(Uri url, CancellationToken ct);

    Task<HttpResponseMessage> PostFormAsync(Uri url, IEnumerable<KeyValuePair<string, string>> fields, CancellationToken ct);

    IReadOnlyList<CookieState> ExportCookies();

    void ImportCookies(IEnumerable<CookieState> cookies);

    /// <summary>Sends <paramref name="userAgent"/> from now on (sessions some sites bind to the browser that signed in).</summary>
    void UseUserAgent(string userAgent);

    /// <summary>Drops every cookie matching <paramref name="match"/> from the jar.</summary>
    void RemoveCookies(Func<CookieState, bool> match);
}

/// <summary><see cref="IBankHttpClient"/> over a private <see cref="HttpClient"/> with its own <see cref="CookieContainer"/>.</summary>
public sealed class BankHttpClient : IBankHttpClient, IDisposable
{
    // Browser logins can have more than CookieContainer's default 20 cookies per domain.
    // Keep a browser-sized jar so importing a verified session does not silently evict its cookies.
    private readonly CookieContainer _jar = new(capacity: 3000, perDomainCapacity: 180, maxCookieSize: 4096);
    private readonly HttpClient _http;
    private readonly HttpClient _plain;

    public BankHttpClient() : this(handlerFactory: null)
    {
    }

    /// <summary>Test seam: when <paramref name="handler"/> is null a SocketsHttpHandler bound to this client's cookie jar is used.</summary>
    public BankHttpClient(HttpMessageHandler? handler) : this(handlerFactory: handler == null ? null : _ => handler)
    {
    }

    /// <summary>Test seam: builds the handler around this client's cookie jar (so a fake bank can emulate cookies and redirects).</summary>
    public BankHttpClient(Func<CookieContainer, HttpMessageHandler>? handlerFactory)
    {
#pragma warning disable CA2000 // owned by the HttpClient (disposeHandler: true)
        _http = new HttpClient(
            handlerFactory?.Invoke(_jar) ?? new SocketsHttpHandler
            {
                CookieContainer = _jar,
                UseCookies = true,
                AllowAutoRedirect = true,
                AutomaticDecompression = DecompressionMethods.All,
            },
            disposeHandler: true);
#pragma warning restore CA2000
        _http.Timeout = TimeSpan.FromSeconds(60);
#pragma warning disable CA2000 // owned by the HttpClient (disposeHandler: true)
        // In tests the fake bank's handler (built around a jar of its own); otherwise a handler without cookies.
        _plain = handlerFactory != null
            ? new HttpClient(handlerFactory(new CookieContainer()), disposeHandler: true)
            : new HttpClient(new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = true, AutomaticDecompression = DecompressionMethods.All }, disposeHandler: true);
#pragma warning restore CA2000
        _plain.Timeout = TimeSpan.FromSeconds(60);
        // Look like the browser a person would use. Sign-in services with bot detection (Scalable's Auth0, for example)
        // put up a captcha for clients that announce themselves as bots, which a connector cannot solve.
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "de-DE,de;q=0.9,en;q=0.8");
    }

    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => _http.SendAsync(request, ct);

    public Task<HttpResponseMessage> SendWithoutSessionAsync(HttpRequestMessage request, CancellationToken ct) => _plain.SendAsync(request, ct);

    public Task<string> GetStringAsync(Uri url, CancellationToken ct) => _http.GetStringAsync(url, ct);

    /// <summary>Safari on macOS, the browser a person would sign in with.</summary>
    public const string BrowserUserAgent =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/26.1 Safari/605.1.15";

    public async Task<HttpResponseMessage> PostFormAsync(Uri url, IEnumerable<KeyValuePair<string, string>> fields, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(fields);
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        // A browser submitting a form sends where it came from; sign-in pages check these.
        request.Headers.TryAddWithoutValidation("Origin", url.GetLeftPart(UriPartial.Authority));
        request.Headers.Referrer = url;
        return await _http.SendAsync(request, ct);
    }

    // System.Net.Cookie has no SameSite; it is remembered on the side so a cookie that went through a real browser
    // keeps it when it is handed back to one.
    private readonly Dictionary<(string Name, string Domain, string Path), string> _sameSite = [];

    private static (string Name, string Domain, string Path) KeyOf(string name, string domain, string path) =>
        (name, domain.TrimStart('.').ToUpperInvariant(), path);

    public IReadOnlyList<CookieState> ExportCookies()
    {
        lock (_sameSite)
        {
            return _jar.GetAllCookies()
                .Where(c => !c.Expired)
                .Select(c => new CookieState(
                    c.Name, c.Value, c.Domain, c.Path,
                    c.Expires == DateTime.MinValue ? null : c.Expires.ToUniversalTime(),
                    c.Secure, c.HttpOnly,
                    _sameSite.GetValueOrDefault(KeyOf(c.Name, c.Domain, c.Path))))
                .ToList();
        }
    }

    public void UseUserAgent(string userAgent)
    {
        _http.DefaultRequestHeaders.Remove("User-Agent");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", userAgent);
    }

    public void ImportCookies(IEnumerable<CookieState> cookies)
    {
        lock (_sameSite)
        {
            foreach (var c in cookies)
            {
                if (c.ExpiresUtc is { } exp && exp <= DateTime.UtcNow) continue;
                var cookie = new Cookie(c.Name, c.Value, c.Path, c.Domain)
                {
                    Secure = c.Secure,
                    HttpOnly = c.HttpOnly,
                };
                if (c.ExpiresUtc is { } e) cookie.Expires = e;
                _jar.Add(cookie);
                if (c.SameSite != null) _sameSite[KeyOf(c.Name, c.Domain, c.Path)] = c.SameSite;
            }
        }
    }

    public void RemoveCookies(Func<CookieState, bool> match)
    {
        lock (_sameSite)
        {
            foreach (var c in _jar.GetAllCookies().ToList())
            {
                if (!match(new CookieState(c.Name, c.Value, c.Domain, c.Path, null, c.Secure, c.HttpOnly))) continue;
                c.Expired = true;
                _sameSite.Remove(KeyOf(c.Name, c.Domain, c.Path));
            }
        }
    }

    /// <summary>
    /// After a web login: drops every cookie of the spec's domains from the jar and imports the browser's cookies in their
    /// place (expired ones are skipped). Cookies of other domains stay.
    /// </summary>
    public void ReplaceCookies(WebLoginSpec spec, IEnumerable<CookieState> fromBrowser)
    {
        lock (_sameSite)
        {
            foreach (var c in _jar.GetAllCookies().ToList())
            {
                if (!spec.Matches(new CookieState(c.Name, c.Value, c.Domain, c.Path, null, c.Secure, c.HttpOnly))) continue;
                c.Expired = true;
                _sameSite.Remove(KeyOf(c.Name, c.Domain, c.Path));
            }
        }
        ImportCookies(fromBrowser.Where(spec.Matches));
    }

    public void Dispose()
    {
        _http.Dispose();
        _plain.Dispose();
    }
}

/// <summary>What a connector gets when it is created for a sync.</summary>
public sealed record ConnectorContext(IBankHttpClient Http, TimeProvider Clock, ILogger Logger)
{
    /// <summary>Updates the owner's current sync status during a longer bank-side operation; never include secrets.</summary>
    public Action<string>? Progress { get; init; }
}
