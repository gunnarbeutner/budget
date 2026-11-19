using System.Security.Claims;

namespace OpenBudget.Browser;

public static class WebLoginEndpoints
{
    public const string SocketPath = "/browser/ws/";

    /// <summary>
    /// GET /browser/ws/{sessionId}: the WebSocket of a web login's remote view. Requires a signed-in user (the fallback
    /// authorization policy) who is the owner of that session; anyone else gets 404, exactly as for an unknown id. The id is
    /// 128 random bits, and a browser's cross-origin WebSocket attempt is refused by the Origin check.
    /// </summary>
    public static IEndpointRouteBuilder MapWebLoginEndpoints(this IEndpointRouteBuilder app)
    {
        app.Map(SocketPath + "{sessionId}", async (string sessionId, HttpContext http, IWebLoginService logins) =>
        {
            if (!http.WebSockets.IsWebSocketRequest) return Results.BadRequest();
            if (http.Request.Headers.Origin.ToString() is { Length: > 0 } origin && !SameOrigin(origin, http.Request))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            var userId = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId == null || logins.Find(sessionId, userId) is not { } session) return Results.NotFound();

            using var socket = await http.WebSockets.AcceptWebSocketAsync();
            await session.ServeAsync(socket, http.RequestAborted);
            return Results.Empty;
        });
        app.Map(RemoteBrowserOptions.StreamPath + "{**path}", ServeStreamAsync);
        return app;
    }

    /// <summary>
    /// GET /weblogin/desktop/...: the browser sidecar's stream (<see cref="DesktopProxy"/>), only for the signed-in owner
    /// of the web login on its screen and only while it is there (<see cref="RemoteScreen.Authorize"/>); anyone else, and
    /// everyone when the sidecar is not configured, gets 404. A cross-origin request is refused by the Origin check.
    /// </summary>
    private static async Task ServeStreamAsync(HttpContext http, IWebLoginService logins, IServiceProvider services)
    {
        var screen = services.GetService<RemoteScreen>();
        var options = services.GetService<RemoteBrowserOptions>();
        var userId = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (screen == null || options is not { IsEnabled: true } || userId == null
            || !screen.Authorize(userId, id => logins.Find(id, userId) != null, out var ended))
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        if (http.Request.Headers.Origin.ToString() is { Length: > 0 } origin && !SameOrigin(origin, http.Request))
        {
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        if (!DesktopProxy.IsAllowed(http.Request.Method, http.Request.Path.Value ?? ""))
        {
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        try
        {
            await DesktopProxy.ProxyAsync(http, options.StreamUrl!, services.GetRequiredService<IHttpClientFactory>(), ended);
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or System.Net.WebSockets.WebSocketException)
        {
            if (!http.Response.HasStarted) http.Response.StatusCode = StatusCodes.Status502BadGateway;
        }
    }

    internal static bool SameOrigin(string origin, HttpRequest request) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var o)
        && string.Equals(o.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase);
}
