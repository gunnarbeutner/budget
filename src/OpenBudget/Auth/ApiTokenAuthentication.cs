using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenBudget.Services;

namespace OpenBudget.Auth;

/// <summary>
/// The bearer scheme of the read-only MCP API (<c>/api/mcp/v1/</c>): <c>Authorization: Bearer obk_…</c> resolves to the
/// token's user. It is never a default scheme; only the MCP endpoints' policy names it, so a token authenticates
/// nothing else (every other route only knows the login cookie). Tokens are compared by their SHA-256 hash and never
/// logged.
/// </summary>
public sealed class ApiTokenAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "OpenBudgetApiToken";

    /// <summary>Claim carrying the id of the token a request used.</summary>
    public const string TokenIdClaim = "openbudget:api_token_id";

    // LastUsedAt is written at most this often per token, so a busy client doesn't write on every request.
    private static readonly TimeSpan LastUsedResolution = TimeSpan.FromMinutes(1);

    private readonly SystemDbContextFactory _system;

    public ApiTokenAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, SystemDbContextFactory system)
        : base(options, logger, encoder) => _system = system;

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? header = Request.Headers.Authorization;
        if (string.IsNullOrEmpty(header)) return AuthenticateResult.NoResult();
        const string bearer = "Bearer ";
        if (!header.StartsWith(bearer, StringComparison.OrdinalIgnoreCase)) return AuthenticateResult.NoResult();
        var token = header[bearer.Length..].Trim();
        if (token.Length == 0 || token.Length > 200) return AuthenticateResult.Fail("Invalid token.");

        var hash = ApiTokenService.Hash(token);
        var now = TimeProvider.GetUtcNow().UtcDateTime;
        await using var db = await _system.CreateDbContextAsync(Context.RequestAborted);
        var row = await db.ApiTokens.AsNoTracking()
            .Where(t => t.TokenHash == hash)
            .Select(t => new { t.Id, t.OwnerUserId, t.RevokedAt, t.ExpiresAt, t.LastUsedAt })
            .FirstOrDefaultAsync(Context.RequestAborted);
        if (row == null || row.RevokedAt != null || (row.ExpiresAt is { } exp && exp <= now))
        {
            // Deliberately no token (or hash) in the log.
            Logger.LogInformation("API token rejected (unknown, revoked or expired)");
            return AuthenticateResult.Fail("Invalid token.");
        }
        var userExists = await db.Users.AnyAsync(u => u.Id == row.OwnerUserId, Context.RequestAborted);
        if (!userExists) return AuthenticateResult.Fail("Invalid token.");

        if (row.LastUsedAt is not { } last || now - last >= LastUsedResolution)
        {
            await db.ApiTokens.Where(t => t.Id == row.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.LastUsedAt, now), Context.RequestAborted);
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, row.OwnerUserId), new Claim(TokenIdClaim, row.Id.ToString(System.Globalization.CultureInfo.InvariantCulture))],
            SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer";
        await Response.WriteAsJsonAsync(new { error = "unauthorized", message = "A valid, unrevoked API token is required (Authorization: Bearer <token>)." },
            JsonSerializerOptions.Web, Context.RequestAborted);
    }

    protected override async Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        await Response.WriteAsJsonAsync(new { error = "forbidden", message = "This token may not access this resource." },
            JsonSerializerOptions.Web, Context.RequestAborted);
    }
}
