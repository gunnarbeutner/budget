using Microsoft.EntityFrameworkCore;
using OpenBudget.Compat;

namespace OpenBudget.Services;

public enum UploadAuthResult
{
    Authorized,
    Unauthorized,
    /// <summary>No token can authorize this request: no legacy token configured and the source has none.</summary>
    Disabled
}

/// <summary>
/// Bearer-token authorization for the upload/delete API. Two kinds of token are accepted for a
/// request naming source <c>{name}</c>: the legacy global token (<c>OPENBUDGET_UPLOAD_TOKEN</c>,
/// which keeps authorizing every source exactly as before, for the installed exporters), and that
/// source's own per-source token — which authorizes that one source only.
/// </summary>
public sealed class UploadAuthorizer
{
    private readonly SystemDbContextFactory _system;
    private readonly ILogger<UploadAuthorizer> _logger;
    private readonly IConfiguration _configuration;

    /// <summary>Creates the authorizer.</summary>
    /// <remarks>The tokens are read through <see cref="IConfiguration"/>, which carries the process environment in
    /// production (WebApplication.CreateBuilder adds all environment variables), so hosts in one process (tests) can
    /// each have their own token instead of sharing the process-wide environment.</remarks>
    public UploadAuthorizer(SystemDbContextFactory system, ILogger<UploadAuthorizer> logger, IConfiguration configuration)
    {
        _system = system;
        _logger = logger;
        _configuration = configuration;
    }

    public async Task<UploadAuthResult> AuthorizeAsync(string? authorizationHeader, string sourceName, CancellationToken ct = default)
    {
        var legacy = LegacyCompat.ResolveUploadToken(key => _configuration[key], _logger);
        if (UploadAuth.IsAuthorized(authorizationHeader, legacy)) return UploadAuthResult.Authorized;

        string? sourceToken;
        await using (var db = await _system.CreateDbContextAsync(ct))
        {
            sourceToken = await db.ImportSources.AsNoTracking()
                .Where(s => s.Name == sourceName)
                .Select(s => s.UploadToken)
                .FirstOrDefaultAsync(ct);
        }

        if (UploadAuth.IsAuthorized(authorizationHeader, sourceToken)) return UploadAuthResult.Authorized;
        return string.IsNullOrEmpty(legacy) && string.IsNullOrEmpty(sourceToken)
            ? UploadAuthResult.Disabled
            : UploadAuthResult.Unauthorized;
    }

    /// <summary>Maps a non-authorized result to the HTTP response the upload endpoints return.</summary>
    public static IResult? ToFailure(UploadAuthResult result) => result switch
    {
        UploadAuthResult.Authorized => null,
        UploadAuthResult.Unauthorized => Results.Unauthorized(),
        UploadAuthResult.Disabled => Results.Problem(
            $"Upload endpoint disabled: {LegacyCompat.UploadTokenVariable} is not set and the source has no upload token.",
            statusCode: StatusCodes.Status503ServiceUnavailable),
        _ => Results.Unauthorized(),
    };
}
