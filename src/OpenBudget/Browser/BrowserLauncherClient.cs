using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenBudget.Browser;

/// <summary>A Chrome the sidecar's launcher started: its id there and the port its DevTools endpoint is reachable on.</summary>
public sealed record RemoteChrome(string Id, int Port);

/// <summary>Starts and stops the Chromes web logins run in, one per login, and chooses which one the shared screen shows.</summary>
public interface IBrowserLauncher
{
    /// <summary>Starts a Chrome on the user-data directory packed in <paramref name="profile"/> (a zip), or on an empty one.</summary>
    Task<RemoteChrome> StartAsync(byte[]? profile, CancellationToken ct);

    /// <summary>
    /// Stops the Chrome <paramref name="id"/> (it may have closed already) and deletes its user-data directory; with
    /// <paramref name="returnProfile"/> the directory comes back as a zip first (null when there is none).
    /// </summary>
    Task<byte[]?> StopAsync(string id, bool returnProfile, CancellationToken ct);

    /// <summary>Shows the windows of Chrome <paramref name="id"/> on the shared screen and hides all others; null hides all.</summary>
    Task ShowAsync(string? id, CancellationToken ct);

    /// <summary>Stops every Chrome of the launcher (those of an earlier run of the app that nobody stops any more).</summary>
    Task ResetAsync(CancellationToken ct);

    /// <summary>The DevTools WebSocket of Chrome <paramref name="chrome"/>, as reachable from the app.</summary>
    Task<Uri> DevToolsEndpointAsync(RemoteChrome chrome, CancellationToken ct);
}

/// <summary>
/// Client of the browser sidecar's launcher (docker/browser/browser-launcher). Every request carries the shared token.
/// </summary>
public sealed class BrowserLauncherClient : IBrowserLauncher
{
    private readonly HttpClient _http;
    private readonly Uri _launcher;
    private readonly string _token;

    public BrowserLauncherClient(HttpClient http, RemoteBrowserOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.IsEnabled) throw new ArgumentException("The remote browser is not configured.", nameof(options));
        _http = http;
        _launcher = options.LauncherUrl!;
        _token = options.Token!;
    }

    /// <summary>Starts a Chrome on the user-data directory packed in <paramref name="profile"/> (a zip), or on an empty one.</summary>
    public async Task<RemoteChrome> StartAsync(byte[]? profile, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Post, "browsers");
        request.Content = new ByteArrayContent(profile ?? []);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        using var response = await SendAsync(request, ct);
        var started = await response.Content.ReadFromJsonAsync<StartResponse>(ct)
            ?? throw new BrowserActionException("The browser sidecar sent no answer.");
        if (string.IsNullOrEmpty(started.Id) || started.Port is <= 0 or > 65535)
            throw new BrowserActionException("The browser sidecar sent an invalid answer.");
        return new RemoteChrome(started.Id, started.Port);
    }

    /// <summary>
    /// Stops the Chrome <paramref name="id"/> (it may have closed already) and deletes its user-data directory; with
    /// <paramref name="returnProfile"/> the directory comes back as a zip first (null when there is none).
    /// </summary>
    public async Task<byte[]?> StopAsync(string id, bool returnProfile, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Delete, "browsers/" + Uri.EscapeDataString(id) + (returnProfile ? "?profile=1" : ""));
        using var response = await SendAsync(request, ct, allowNotFound: true);
        if (!returnProfile || response.StatusCode != HttpStatusCode.OK) return null;
        var zip = await response.Content.ReadAsByteArrayAsync(ct);
        return zip.Length > 0 ? zip : null;
    }

    /// <summary>Shows the windows of Chrome <paramref name="id"/> on the shared screen and hides all others; null hides all.</summary>
    public async Task ShowAsync(string? id, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Post, "front");
        // a sized body: the launcher reads Content-Length and does not take chunked requests
        request.Content = new StringContent(JsonSerializer.Serialize(new FrontRequest(id)), System.Text.Encoding.UTF8, "application/json");
        using var response = await SendAsync(request, ct);
    }

    /// <summary>Stops every Chrome of the launcher (those of an earlier run of the app that nobody stops any more).</summary>
    public async Task ResetAsync(CancellationToken ct)
    {
        using var request = Request(HttpMethod.Post, "reset");
        using var response = await SendAsync(request, ct);
    }

    /// <summary>
    /// The DevTools WebSocket of Chrome <paramref name="chrome"/>, addressed by the sidecar's IP address: Chrome refuses
    /// requests whose Host header is a name other than localhost.
    /// </summary>
    public async Task<Uri> DevToolsEndpointAsync(RemoteChrome chrome, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(chrome);
        var host = await ResolveAsync(_launcher.Host, ct);
        var version = new UriBuilder(Uri.UriSchemeHttp, host, chrome.Port, "/json/version").Uri;
        const int Attempts = 5;
        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            try
            {
                using var response = await _http.GetAsync(version, ct);
                response.EnsureSuccessStatusCode();
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                var url = json.RootElement.GetProperty("webSocketDebuggerUrl").GetString();
                if (!Uri.TryCreate(url, UriKind.Absolute, out var debugger))
                    throw new BrowserActionException("The sidecar's Chrome reported no DevTools address.");
                return RewriteEndpoint(debugger, host, chrome.Port);
            }
            catch (HttpRequestException ex)
            {
                if (attempt == Attempts) throw new BrowserActionException("The sidecar's Chrome cannot be reached.", ex);
                // the forwarder to the new Chrome may still be starting
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), ct);
            }
        }
        throw new BrowserActionException("The sidecar's Chrome cannot be reached.");
    }

    /// <summary>
    /// <paramref name="debugger"/> (as Chrome reports it: its own loopback address) with the host and port it is reached
    /// at from here; path and query (the browser's id) stay.
    /// </summary>
    public static Uri RewriteEndpoint(Uri debugger, string host, int port)
    {
        ArgumentNullException.ThrowIfNull(debugger);
        if (debugger.Scheme is not ("ws" or "wss")) throw new BrowserActionException("The DevTools address is not a WebSocket.");
        return new UriBuilder(debugger) { Host = host, Port = port }.Uri;
    }

    /// <summary>An IP address of <paramref name="host"/> (IPv4 first); an address stays as it is.</summary>
    internal static async Task<string> ResolveAsync(string host, CancellationToken ct)
    {
        if (IPAddress.TryParse(host.Trim('[', ']'), out _)) return host;
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return host;
        var addresses = await Dns.GetHostAddressesAsync(host, ct);
        var address = addresses.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork) ?? addresses.FirstOrDefault()
            ?? throw new BrowserActionException("The browser sidecar's address could not be resolved.");
        return address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? "[" + address + "]" : address.ToString();
    }

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, new Uri(_launcher, path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct, bool allowNotFound = false)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new BrowserActionException("The browser sidecar cannot be reached.", ex);
        }
        if (response.IsSuccessStatusCode || (allowNotFound && response.StatusCode == HttpStatusCode.NotFound)) return response;
        var status = (int)response.StatusCode;
        response.Dispose();
        throw new BrowserActionException(status == 503
            ? "Too many browsers are open on the browser sidecar; try again in a few minutes."
            : $"The browser sidecar refused the request ({status}).");
    }

    private sealed record StartResponse([property: JsonPropertyName("id")] string Id, [property: JsonPropertyName("port")] int Port);

    private sealed record FrontRequest([property: JsonPropertyName("id")] string? Id);
}
