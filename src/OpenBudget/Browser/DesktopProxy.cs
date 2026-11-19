using System.Net.WebSockets;

namespace OpenBudget.Browser;

/// <summary>
/// Reverse proxy for the browser sidecar's stream (selkies) under <see cref="RemoteBrowserOptions.StreamPath"/>, at the
/// same path selkies serves it (SELKIES_SUBFOLDER). It only passes the stream client's pages and its WebSocket: GET
/// requests, of which only an allow-listed part of selkies' API; never the control API (tokens, sessions, files). Only
/// selkies' own cookie travels, never the app's sign-in cookie. Who may use it is decided by the caller
/// (<see cref="WebLoginEndpoints"/>).
/// </summary>
public static class DesktopProxy
{
    public const string TokenCookie = "selkies_token";
    public const string HttpClientName = "browser-stream";

    /// <summary>selkies API paths (below the stream path) the stream client needs: its WebSocket and status probes.</summary>
    private static readonly string[] AllowedApi = ["api/websockets", "api/health", "api/status"];

    private static readonly string[] ForwardedRequestHeaders = ["Authorization", "Range", "If-None-Match", "If-Modified-Since"];

    private static readonly string[] ForwardedResponseHeaders = ["Content-Security-Policy", "ETag", "Cache-Control", "Last-Modified"];

    /// <summary>Whether a request with <paramref name="method"/> to <paramref name="path"/> may reach selkies.</summary>
    public static bool IsAllowed(string method, string path)
    {
        if (!HttpMethods.IsGet(method)) return false;
        if (!path.StartsWith(RemoteBrowserOptions.StreamPath, StringComparison.Ordinal)) return false;
        var rest = path[RemoteBrowserOptions.StreamPath.Length..];
        // no way out of the stream path, encoded or not
        if (rest.Contains("..", StringComparison.Ordinal) || rest.Contains('%', StringComparison.Ordinal)
            || rest.Contains('\\', StringComparison.Ordinal) || rest.Contains("//", StringComparison.Ordinal) || rest.StartsWith('/', StringComparison.Ordinal)) return false;
        if (rest.Equals("api", StringComparison.OrdinalIgnoreCase) || rest.StartsWith("api/", StringComparison.OrdinalIgnoreCase))
            return AllowedApi.Any(a => rest.Equals(a, StringComparison.Ordinal) || rest.StartsWith(a + "/", StringComparison.Ordinal));
        return true;
    }

    /// <summary>The value of the Cookie header sent to selkies: only its own token cookie, or null.</summary>
    public static string? UpstreamCookie(IRequestCookieCollection cookies)
    {
        ArgumentNullException.ThrowIfNull(cookies);
        return cookies.TryGetValue(TokenCookie, out var token) ? TokenCookie + "=" + Uri.EscapeDataString(token) : null;
    }

    /// <summary>Passes the request to selkies until it completes, the client leaves or <paramref name="ended"/> fires.</summary>
    public static async Task ProxyAsync(HttpContext context, Uri streamUrl, IHttpClientFactory clients, CancellationToken ended)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(streamUrl);
        var target = new Uri(streamUrl, context.Request.Path.Value + context.Request.QueryString);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, ended);
        if (context.WebSockets.IsWebSocketRequest)
        {
            await ProxySocketAsync(context, target, cancel.Token);
            return;
        }
        using var request = new HttpRequestMessage(HttpMethod.Get, target);
        foreach (var header in ForwardedRequestHeaders)
            if (context.Request.Headers.TryGetValue(header, out var value)) request.Headers.TryAddWithoutValidation(header, value.ToArray());
        if (UpstreamCookie(context.Request.Cookies) is { } cookie) request.Headers.Add("Cookie", cookie);
        using var response = await clients.CreateClient(HttpClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel.Token);
        context.Response.StatusCode = (int)response.StatusCode;
        foreach (var header in response.Content.Headers) context.Response.Headers[header.Key] = header.Value.ToArray();
        foreach (var header in response.Headers.Where(h => ForwardedResponseHeaders.Contains(h.Key, StringComparer.OrdinalIgnoreCase)))
            context.Response.Headers[header.Key] = header.Value.ToArray();
        if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            foreach (var set in cookies.Where(c => c.StartsWith(TokenCookie + "=", StringComparison.Ordinal)))
                context.Response.Headers.Append("Set-Cookie", set);
        }
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["X-Frame-Options"] = "SAMEORIGIN";
        await response.Content.CopyToAsync(context.Response.Body, cancel.Token);
    }

    private static async Task ProxySocketAsync(HttpContext context, Uri target, CancellationToken ct)
    {
        using var upstream = new ClientWebSocket();
        if (UpstreamCookie(context.Request.Cookies) is { } cookie) upstream.Options.SetRequestHeader("Cookie", cookie);
        if (context.Request.Headers.TryGetValue("Authorization", out var authorization))
            upstream.Options.SetRequestHeader("Authorization", authorization.ToString());
        foreach (var protocol in context.WebSockets.WebSocketRequestedProtocols) upstream.Options.AddSubProtocol(protocol);
        // selkies checks the origin against SELKIES_ALLOWED_ORIGINS: the app's own (the endpoint refused any other)
        upstream.Options.SetRequestHeader("Origin", $"{context.Request.Scheme}://{context.Request.Host}");
        await upstream.ConnectAsync(new UriBuilder(target) { Scheme = target.Scheme == Uri.UriSchemeHttps ? "wss" : "ws" }.Uri, ct);
        using var downstream = await context.WebSockets.AcceptWebSocketAsync(upstream.SubProtocol);
        await RelayAsync(downstream, upstream, ct);
    }

    /// <summary>Copies messages both ways until one side closes or <paramref name="ct"/> fires, then aborts both.</summary>
    private static async Task RelayAsync(WebSocket client, WebSocket server, CancellationToken ct)
    {
        void AbortBoth()
        {
            client.Abort();
            server.Abort();
        }
        await using var registration = ct.Register(AbortBoth);
        var outgoing = PumpAsync(client, server, ct);
        var incoming = PumpAsync(server, client, ct);
        try
        {
            await Task.WhenAny(outgoing, incoming);
        }
        finally
        {
            // one direction ended (or the view did): end the other one as well
            AbortBoth();
            try { await Task.WhenAll(outgoing, incoming); }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException) { /* aborted */ }
        }
    }

    private static async Task PumpAsync(WebSocket from, WebSocket to, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        while (!ct.IsCancellationRequested && from.State == WebSocketState.Open && to.State == WebSocketState.Open)
        {
            var received = await from.ReceiveAsync(buffer, ct);
            if (received.MessageType == WebSocketMessageType.Close) break;
            await to.SendAsync(buffer.AsMemory(0, received.Count), received.MessageType, received.EndOfMessage, ct);
        }
    }
}
