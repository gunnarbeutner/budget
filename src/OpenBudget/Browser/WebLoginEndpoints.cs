using System.Security.Claims;

namespace OpenBudget.Browser;

public static class WebLoginEndpoints
{
    /// <summary>Maps the browser sidecar's stream (see <see cref="ServeStreamAsync"/>).</summary>
    public static IEndpointRouteBuilder MapWebLoginEndpoints(this IEndpointRouteBuilder app)
    {
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
