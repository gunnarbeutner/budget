using System.Security.Claims;

namespace OpenBudget.Importers.MoneyMoney;

/// <summary>
/// The chunked upload of the browser's MoneyMoney import (see <see cref="MoneyMoneyBrowserImport"/>), for the signed-in
/// user only. The page starts an upload through the Blazor circuit, then its script sends the file in chunks:
/// <list type="bullet">
/// <item><c>GET /api/moneymoney/uploads/{id}</c>: <c>{"received":n,"size":n}</c>, where to resume;</item>
/// <item><c>PUT /api/moneymoney/uploads/{id}?offset=n</c>: one chunk as the body (at most
/// <see cref="MoneyMoneyBrowserImport.MaxChunkBytes"/>); answers <c>{"received":n}</c>. A chunk at another offset than
/// the received count is not written; the answer tells the client where to go on.</item>
/// </list>
/// </summary>
internal static class MoneyMoneyUploadEndpoints
{
    public const string Route = "/api/moneymoney/uploads/{id}";

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet(Route, (string id, HttpContext http, MoneyMoneyBrowserImport uploads) =>
            User(http) is { } user && uploads.Received(user, id) is { } received
                ? Results.Ok(new { received, size = uploads.Size(user, id) })
                : Results.NotFound());

        app.MapPut(Route, async (string id, long offset, HttpContext http, MoneyMoneyBrowserImport uploads, CancellationToken ct) =>
        {
            if (User(http) is not { } user) return Results.NotFound();
            var sizeFeature = http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>();
            if (sizeFeature is { IsReadOnly: false }) sizeFeature.MaxRequestBodySize = MoneyMoneyBrowserImport.MaxChunkBytes;
            try
            {
                return await uploads.AppendAsync(user, id, offset, http.Request.Body, ct) is { } received
                    ? Results.Ok(new { received })
                    : Results.NotFound();
            }
            catch (InvalidDataException)
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }
        });
    }

    private static string? User(HttpContext http) => http.User.FindFirstValue(ClaimTypes.NameIdentifier) is { Length: > 0 } id ? id : null;
}
