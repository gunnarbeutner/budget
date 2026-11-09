using System.Net;

namespace OpenBudget.Connectors;

/// <summary>
/// HTTP access for connectors (web-based banks): a client with a cookie jar whose state can be
/// exported into the persisted <see cref="ConnectorSession"/> and restored at the next login.
/// </summary>
public interface IBankHttpClient
{
    Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct);

    Task<string> GetStringAsync(Uri url, CancellationToken ct);

    Task<HttpResponseMessage> PostFormAsync(Uri url, IEnumerable<KeyValuePair<string, string>> fields, CancellationToken ct);

    IReadOnlyList<CookieState> ExportCookies();

    void ImportCookies(IEnumerable<CookieState> cookies);
}

/// <summary><see cref="IBankHttpClient"/> over a private <see cref="HttpClient"/> with its own <see cref="CookieContainer"/>.</summary>
public sealed class BankHttpClient : IBankHttpClient, IDisposable
{
    private readonly CookieContainer _jar = new();
    private readonly HttpClient _http;

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
            handlerFactory?.Invoke(_jar) ?? new SocketsHttpHandler { CookieContainer = _jar, UseCookies = true, AllowAutoRedirect = true },
            disposeHandler: true);
#pragma warning restore CA2000
        _http.Timeout = TimeSpan.FromSeconds(60);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; OpenBudget)");
    }

    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => _http.SendAsync(request, ct);

    public Task<string> GetStringAsync(Uri url, CancellationToken ct) => _http.GetStringAsync(url, ct);

    public async Task<HttpResponseMessage> PostFormAsync(Uri url, IEnumerable<KeyValuePair<string, string>> fields, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(fields);
        return await _http.PostAsync(url, content, ct);
    }

    public IReadOnlyList<CookieState> ExportCookies() =>
        _jar.GetAllCookies()
            .Where(c => !c.Expired)
            .Select(c => new CookieState(
                c.Name, c.Value, c.Domain, c.Path,
                c.Expires == DateTime.MinValue ? null : c.Expires.ToUniversalTime(),
                c.Secure, c.HttpOnly))
            .ToList();

    public void ImportCookies(IEnumerable<CookieState> cookies)
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
        }
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>What a connector gets when it is created for a sync.</summary>
public sealed record ConnectorContext(IBankHttpClient Http, TimeProvider Clock, ILogger Logger);
