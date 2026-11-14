using System.Security.Claims;
using OpenBudget.Auth;

namespace OpenBudget.Connectors.OneDrive;

public static class OneDriveRegistration
{
    /// <summary>The settings page that hosts the OneDrive section; the sign-in returns there with ?onedrive=&lt;outcome&gt;.</summary>
    public const string SettingsPath = "/portfolio-performance";

    /// <summary>The "return" value of the connect URL that brings the sign-in back to Get started's Portfolio Performance step.</summary>
    public const string GetStartedReturn = "get-started";

    /// <summary>The "return" value of the connect URL that asks for mail access and brings the sign-in back to the receipts page.</summary>
    public const string ReceiptsReturn = "receipts";

    /// <summary>The receipts settings page (mail access, non-food category, read receipts).</summary>
    public const string ReceiptsPath = "/receipts";

    private const string GetStartedPath = "/get-started?step=3";
    private const string ReturnCookie = "OpenBudget.OneDriveReturn";

    /// <summary>Registers the OneDrive connection, reader, change detection and the PortfolioPerformance consumer.</summary>
    public static IServiceCollection AddOneDrive(this IServiceCollection services, string contentRoot)
    {
        services.AddHttpClient(OneDriveOptions.HttpClientName, c => c.Timeout = TimeSpan.FromMinutes(2));
        services.AddSingleton(sp => OneDriveOptions.FromConfiguration(sp.GetRequiredService<IConfiguration>()));
        services.AddSingleton<MicrosoftIdentityClient>();
        services.AddSingleton<OneDriveService>();
        services.AddSingleton<ICloudFileReader>(sp => sp.GetRequiredService<OneDriveService>());
        services.AddSingleton(new CloudFileStaging(Path.Combine(OpenBudgetPaths.DataDir(contentRoot), "uploads")));
        services.AddSingleton<ICloudFileConsumer, PortfolioPerformanceCloudFileConsumer>();
        services.AddSingleton<CloudFileSyncCoordinator>();
        return services;
    }

    /// <summary>
    /// GET /connect/onedrive starts the Microsoft sign-in of the signed-in user; GET /connect/onedrive/callback
    /// completes it and returns to the settings page (or to Get started when the sign-in started there). Both need a signed-in OpenBudget user (fallback policy).
    /// </summary>
    public static void MapOneDriveEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(OneDriveOptions.ConnectPath, async (HttpContext http, OneDriveService oneDrive, CancellationToken ct) =>
        {
            if (!oneDrive.IsEnabled || http.User.FindFirstValue(ClaimTypes.NameIdentifier) is not { Length: > 0 } userId)
                return Results.NotFound();
            // Where the callback leads back to (only Get started, the receipts page or the settings page).
            var back = http.Request.Query["return"].ToString();
            if (back is GetStartedReturn or ReceiptsReturn)
                http.Response.Cookies.Append(ReturnCookie, back, new CookieOptions { HttpOnly = true, Secure = http.Request.IsHttps, SameSite = SameSiteMode.Lax, MaxAge = TimeSpan.FromMinutes(30) });
            else
                http.Response.Cookies.Delete(ReturnCookie);
            // Mail access is asked for from the receipts page, and kept when reconnecting a connection that has it.
            var mail = back == ReceiptsReturn || await oneDrive.HasMailAccessAsync(userId, ct);
            return Results.Redirect(oneDrive.BeginConnect(userId, RedirectUri(http.Request), mail).AbsoluteUri);
        });

        app.MapGet(OneDriveOptions.CallbackPath, async (HttpContext http, OneDriveService oneDrive, CancellationToken ct) =>
        {
            if (!oneDrive.IsEnabled || http.User.FindFirstValue(ClaimTypes.NameIdentifier) is not { Length: > 0 } userId)
                return Results.NotFound();
            var q = http.Request.Query;
            var outcome = await oneDrive.CompleteConnectAsync(userId, q["state"], q["code"], q["error"], ct);
            var back = http.Request.Cookies[ReturnCookie];
            http.Response.Cookies.Delete(ReturnCookie);
            return Results.Redirect(back switch
            {
                GetStartedReturn => $"{GetStartedPath}&onedrive={OutcomeName(outcome)}",
                ReceiptsReturn => $"{ReceiptsPath}?onedrive={OutcomeName(outcome)}",
                _ => $"{SettingsPath}?onedrive={OutcomeName(outcome)}#onedrive",
            });
        });
    }

    /// <summary>The public callback URL (behind the proxy the forwarded scheme and host apply).</summary>
    public static Uri RedirectUri(HttpRequest request) =>
        new($"{request.Scheme}://{request.Host}{request.PathBase}{OneDriveOptions.CallbackPath}");

    /// <summary>The value of the settings page's "onedrive" query parameter for a sign-in outcome.</summary>
    public static string OutcomeName(ConnectOutcome outcome) => outcome switch
    {
        ConnectOutcome.Connected => "connected",
        ConnectOutcome.Denied => "denied",
        ConnectOutcome.InvalidState => "expired",
        ConnectOutcome.Failed => "failed",
        _ => "failed",
    };
}
