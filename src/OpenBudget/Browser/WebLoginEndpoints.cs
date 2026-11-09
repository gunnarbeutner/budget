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
        return app;
    }

    internal static bool SameOrigin(string origin, HttpRequest request) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var o)
        && string.Equals(o.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase);
}
