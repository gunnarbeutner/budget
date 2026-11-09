using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.Auth;

public static class AuthRegistration
{
    /// <summary>Environment variable (or config key) that overrides the passkey relying-party id.</summary>
    public const string RelyingPartyIdSetting = "OPENBUDGET_RP_ID";

    /// <summary>
    /// Database wiring. <see cref="IDbContextFactory{TContext}"/> resolves to the user-scoped factory
    /// (every context filters reads and guards writes for the current user); the unrestricted
    /// <see cref="SystemDbContextFactory"/> is a separate type used only where that is intended
    /// (token-authenticated uploads, the CLI, Identity's own stores).
    /// </summary>
    public static IServiceCollection AddOpenBudgetDatabase(this IServiceCollection services, string databasePath)
    {
        services.AddDbContextFactory<BudgetDbContext>(o => o.UseSqlite($"Data Source={databasePath}"));
        services.TryAddSingleton<SystemDbContextFactory>();
        // Identity's stores want a BudgetDbContext from DI; they get an unrestricted one (Identity's
        // tables carry no per-user filters — they are managed via UserManager/SignInManager only).
        services.AddScoped(sp => sp.GetRequiredService<SystemDbContextFactory>().CreateDbContext());
        services.Replace(ServiceDescriptor.Scoped<IDbContextFactory<BudgetDbContext>, ScopedDbContextFactory>());
        services.AddScoped<ICurrentUser, AuthStateCurrentUser>();
        services.AddScoped<AccessScopeProvider>();
        return services;
    }

    /// <summary>Identity core (users + passkeys on the same SQLite store); also what the CLI uses.</summary>
    public static IServiceCollection AddOpenBudgetIdentityCore(this IServiceCollection services)
    {
        services.AddIdentityCore<AppUser>(o =>
            {
                // Password hashing, password rules and lockout are Identity's defaults.
                o.User.RequireUniqueEmail = false;
                o.Stores.SchemaVersion = IdentitySchemaVersions.Version3; // adds the passkeys table
            })
            .AddEntityFrameworkStores<BudgetDbContext>()
            .AddSignInManager();

        // Passkeys (WebAuthn) use ASP.NET Core Identity's built-in support. With no relying-party id
        // configured, ServerDomain stays null so the id defaults to the request host and the origin
        // must match the request's; with one configured, both are pinned to it.
        // The setting is read when the options are first used (not at registration), so it reflects
        // the final host configuration including environment variables.
        services.AddOptions<IdentityPasskeyOptions>().Configure<IConfiguration>((o, cfg) =>
        {
            var rpId = (cfg[RelyingPartyIdSetting] ?? cfg["Passkeys:RelyingPartyId"])?.Trim();
            if (string.IsNullOrEmpty(rpId)) return;
            o.ServerDomain = rpId;
            o.ValidateOrigin = ctx => ValueTask.FromResult(IsOriginAllowed(ctx.Origin, ctx.CrossOrigin, rpId));
        });
        return services;
    }

    /// <summary>The origin a passkey ceremony ran on must be (a subdomain of) the relying-party id, over https.</summary>
    internal static bool IsOriginAllowed(string? origin, bool crossOrigin, string rpId)
    {
        if (crossOrigin || !Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
        var hostOk = uri.Host.Equals(rpId, StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith("." + rpId, StringComparison.OrdinalIgnoreCase);
        var schemeOk = uri.Scheme == Uri.UriSchemeHttps || uri.Host is "localhost" or "127.0.0.1";
        return hostOk && schemeOk;
    }

    public static IServiceCollection AddOpenBudgetAuth(this IServiceCollection services, IHostEnvironment env, string contentRoot)
    {
        // Data-protection keys protect the auth cookie and antiforgery tokens. They MUST persist on
        // the data volume, otherwise every redeploy would log everyone out. (Tests use ephemeral keys.)
        var dp = services.AddDataProtection().SetApplicationName("OpenBudget");
        if (!env.IsEnvironment("Test"))
        {
            var keysDir = OpenBudgetPaths.KeysDir(contentRoot);
            Directory.CreateDirectory(keysDir);
            dp.PersistKeysToFileSystem(new DirectoryInfo(keysDir));
        }

        services.AddOpenBudgetIdentityCore();

        services.AddAuthentication(IdentityConstants.ApplicationScheme)
            .AddIdentityCookies(b => b.ApplicationCookie?.Configure(o =>
            {
                o.Cookie.Name = "OpenBudget.Auth";
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Lax;
                // Secure exactly when the request is HTTPS. Behind the TLS proxy that is always the
                // case (X-Forwarded-Proto, see UseForwardedHeaders); over plain http (a local
                // container) an always-Secure cookie is dropped by Safari and login silently fails.
                o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                // 30-day session, renewed while in use.
                o.ExpireTimeSpan = TimeSpan.FromDays(30);
                o.SlidingExpiration = true;
                o.LoginPath = "/Account/Login";
                o.LogoutPath = "/Account/Logout";
                o.AccessDeniedPath = "/Account/Login";
                o.Events.OnRedirectToLogin = ctx => RedirectOrChallenge(ctx);
                o.Events.OnRedirectToAccessDenied = ctx => RedirectOrChallenge(ctx);
            }));
        // Re-check the security stamp (password change / user removal) every minute rather than every 30.
        services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.FromMinutes(1));

        // Everything requires a logged-in user unless an endpoint opts out with [AllowAnonymous]
        // (login page, health probe, token-authenticated upload API, static assets). This covers all
        // Razor pages, the Blazor hub and its negotiate endpoint.
        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

        // Circuit auth state revalidates against the user store so a removed user / changed password
        // ends open circuits too.
        services.AddScoped<AuthenticationStateProvider, RevalidatingAuthStateProvider>();
        services.AddSingleton<UserDirectory>();
        return services;
    }

    private static Task RedirectOrChallenge(RedirectContext<CookieAuthenticationOptions> ctx)
    {
        // SignalR/API callers can't follow an HTML redirect: refuse them outright.
        var path = ctx.Request.Path;
        if (path.StartsWithSegments("/_blazor", StringComparison.OrdinalIgnoreCase) || path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        }
        ctx.Response.Redirect(ctx.RedirectUri);
        return Task.CompletedTask;
    }
}
