namespace OpenBudget.Connectors;

/// <summary>
/// A request run by the signed-in page itself (<c>fetch</c> in the bank's page), for sites whose session only works from
/// the browser it was signed in with. Cookies of the page's own origin travel along; the body is text.
/// </summary>
public sealed record BrowserFetchRequest(string Method, Uri Url)
{
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public string? Body { get; init; }

    /// <summary>When set, the body is a multipart form (the page's <c>FormData</c>) with these fields; <see cref="Body"/> is ignored.</summary>
    public IReadOnlyDictionary<string, string>? Form { get; init; }
}

/// <summary>
/// The answer to a <see cref="BrowserFetchRequest"/> after redirects (followed in the browser). <see cref="Status"/> 0 means
/// the browser could not complete the request (network error, or a cross-origin redirect target that does not allow the
/// page to read it).
/// </summary>
public sealed record BrowserFetchResponse(int Status, Uri? Url, string? ContentType, byte[] Body)
{
    public bool IsSuccess => Status is >= 200 and < 300;

    public string Text => System.Text.Encoding.UTF8.GetString(Body);
}

/// <summary>
/// A browser that stays signed in after a web login (<see cref="WebLoginSpec.KeepBrowser"/>), handed to the connector with
/// the challenge's answer. It is closed when the sync step that asked for it ends, or after a lifetime limit; requests
/// after that fail with a <see cref="ConnectorException"/>. Requests run one at a time.
/// </summary>
public interface IBrowserFetcher
{
    /// <summary>False once the browser was closed.</summary>
    bool IsOpen { get; }

    Task<BrowserFetchResponse> SendAsync(BrowserFetchRequest request, CancellationToken ct);

    /// <summary>
    /// Requests <paramref name="url"/> from the signed-in page and returns the Location of its redirect without following
    /// it (a short-lived download link, say); null when it answers without a redirect.
    /// </summary>
    Task<Uri?> ResolveRedirectAsync(Uri url, CancellationToken ct);
}

public static class BrowserFetcherExtensions
{
    public static Task<BrowserFetchResponse> GetAsync(this IBrowserFetcher browser, Uri url, string? accept, CancellationToken ct) =>
        browser.SendAsync(new BrowserFetchRequest("GET", url)
        {
            Headers = accept == null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Accept"] = accept },
        }, ct);

    /// <summary>A POST with a multipart body built like the page's own <c>FormData</c>.</summary>
    public static Task<BrowserFetchResponse> PostMultipartAsync(
        this IBrowserFetcher browser, Uri url, IReadOnlyDictionary<string, string> fields, IReadOnlyDictionary<string, string> headers, CancellationToken ct) =>
        browser.SendAsync(new BrowserFetchRequest("POST", url) { Headers = headers, Form = fields }, ct);

    public static Task<BrowserFetchResponse> PostFormAsync(
        this IBrowserFetcher browser, Uri url, IEnumerable<KeyValuePair<string, string>> fields, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
    {
        var all = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase)
        {
            ["Content-Type"] = "application/x-www-form-urlencoded; charset=UTF-8",
        };
        var body = string.Join('&', fields.Select(f => Uri.EscapeDataString(f.Key) + "=" + Uri.EscapeDataString(f.Value)));
        return browser.SendAsync(new BrowserFetchRequest("POST", url) { Headers = all, Body = body }, ct);
    }
}
