using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using OpenBudget.Connectors.OneDrive;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.Auth;

/// <summary>
/// Microsoft (Entra ID) sign-in and the MCP API's Entra access tokens (docs/microsoft-signin.md, docs/mcp-api.md).
/// Both use OpenBudget's existing Entra registration (the one behind OneDrive and receipts mail: client id and secret
/// from <see cref="OneDriveOptions.ClientIdVariable"/> / <see cref="OneDriveOptions.ClientSecretVariable"/>) in one
/// configured tenant. A Microsoft account reaches OpenBudget only through an <see cref="ExternalLogin"/> link: it never
/// creates or auto-links a user.
/// </summary>
public static class MicrosoftSignIn
{
    public const string Scheme = "Microsoft";
    public const string McpEntraScheme = "McpEntra";
    /// <summary>The scheme the MCP API uses: picks the API-token or the Entra handler by the bearer's form.</summary>
    public const string McpBearerScheme = "McpBearer";

    public const string CallbackPath = "/signin-microsoft";
    public const string LoginPath = "/Account/Microsoft/Login";
    public const string LinkPath = "/Account/Microsoft/Link";
    public const string CompletePath = "/Account/Microsoft/Complete";

    public const string EnabledSetting = "Auth:Microsoft:Enabled";
    public const string TenantSetting = "Auth:Microsoft:TenantId";
    public const string LinksSection = "Auth:Microsoft:Links";
    public const string McpEnabledSetting = "McpApi:Entra:Enabled";
    public const string McpAudiencesSetting = "McpApi:Entra:Audiences";
    public const string McpAllowedClientSetting = "McpApi:AllowedClientId";
    public const string McpScope = "mcp.access";

    public const string AuthorityBase = "https://login.microsoftonline.com";
    private const string ForbiddenItem = "OpenBudget.McpForbidden";

    public static bool LoginEnabled(IConfiguration c) => c.GetValue<bool>(EnabledSetting);

    /// <summary>Password and passkey sign-in (and their management); default on.</summary>
    public const string LocalLoginSetting = "Auth:LocalLogin:Enabled";

    public static bool LocalLoginEnabled(IConfiguration c) => c.GetValue(LocalLoginSetting, true);
    public static bool McpEnabled(IConfiguration c) => c.GetValue<bool>(McpEnabledSetting);

    /// <summary>The single tenant: <c>Auth:Microsoft:TenantId</c>, else the OneDrive tenant when that is a tenant id.</summary>
    public static string? TenantId(IConfiguration c)
    {
        var raw = c[TenantSetting] is { Length: > 0 } t ? t : c[OneDriveOptions.TenantVariable];
        return Guid.TryParse(raw?.Trim(), out var g) ? g.ToString("D") : null;
    }

    public static string? ClientId(IConfiguration c) => c[OneDriveOptions.ClientIdVariable]?.Trim() is { Length: > 0 } id ? id : null;
    private static string? ClientSecret(IConfiguration c) => c[OneDriveOptions.ClientSecretVariable]?.Trim() is { Length: > 0 } s ? s : null;

    public static string[] McpAudiences(IConfiguration c)
    {
        var configured = (c[McpAudiencesSetting] ?? "").Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (configured.Length > 0) return configured;
        return ClientId(c) is { } id ? [id, "api://" + id] : [];
    }

    public static string? McpAllowedClient(IConfiguration c) => c[McpAllowedClientSetting]?.Trim() is { Length: > 0 } id ? id : null;

    /// <summary>Refuses to start with Microsoft sign-in or the MCP Entra tokens turned on but not configured.</summary>
    public static void ValidateConfiguration(IConfiguration c)
    {
        var problems = new List<string>();
        if (LoginEnabled(c) || McpEnabled(c))
        {
            if (TenantId(c) == null) problems.Add($"{TenantSetting} (a tenant id GUID) is required");
            if (ClientId(c) == null) problems.Add($"{OneDriveOptions.ClientIdVariable} is required");
        }
        if (LoginEnabled(c) && ClientSecret(c) == null) problems.Add($"{OneDriveOptions.ClientSecretVariable} is required for Microsoft sign-in");
        if (McpEnabled(c))
        {
            if (McpAllowedClient(c) == null) problems.Add($"{McpAllowedClientSetting} is required for MCP Entra tokens");
            if (McpAudiences(c).Length == 0) problems.Add($"{McpAudiencesSetting} is required for MCP Entra tokens");
        }
        if (!LocalLoginEnabled(c) && !LoginEnabled(c))
            problems.Add($"{LocalLoginSetting}=false requires {EnabledSetting}=true (otherwise nobody could sign in)");
        foreach (var link in c.GetSection(LinksSection).GetChildren())
            if (!Guid.TryParse(link.Key, out _) || string.IsNullOrWhiteSpace(link.Value))
                problems.Add($"{LinksSection}: '{link.Key}' must be an object id GUID mapped to a user name");
        if (problems.Count > 0)
            throw new InvalidOperationException("Microsoft sign-in configuration: " + string.Join("; ", problems) + ".");
    }

    /// <summary>Registers the OpenID Connect sign-in, the MCP Entra bearer scheme and the MCP scheme selector.</summary>
    public static AuthenticationBuilder AddMicrosoftSignIn(this AuthenticationBuilder auth)
    {
        auth.AddOpenIdConnect(Scheme, o =>
        {
            o.SignInScheme = IdentityConstants.ExternalScheme;
            o.CallbackPath = CallbackPath;
            o.ResponseType = OpenIdConnectResponseType.Code;
            o.UsePkce = true;
            // The code comes back as a GET (works with the Lax correlation/nonce cookies; no cross-site POST).
            o.ResponseMode = OpenIdConnectResponseMode.Query;
            o.Scope.Clear();
            o.Scope.Add("openid");
            o.Scope.Add("profile");
            o.MapInboundClaims = false;
            o.SaveTokens = false;
            o.GetClaimsFromUserInfoEndpoint = false;
            o.Events.OnRemoteFailure = ctx =>
            {
                ctx.Response.Redirect("/Account/Login?microsoft=failed");
                ctx.HandleResponse();
                return Task.CompletedTask;
            };
        });
        auth.Services.AddOptions<OpenIdConnectOptions>(Scheme).Configure<IConfiguration>((o, c) =>
        {
            var tenant = TenantId(c) ?? "invalid-tenant";
            o.Authority = $"{AuthorityBase}/{tenant}/v2.0";
            o.ClientId = ClientId(c) ?? "not-configured";
            o.ClientSecret = ClientSecret(c);
            o.TokenValidationParameters.ValidIssuer = $"{AuthorityBase}/{tenant}/v2.0";
        });

        auth.AddJwtBearer(McpEntraScheme, o =>
        {
            o.MapInboundClaims = false;
            o.IncludeErrorDetails = false;
            o.Events = new JwtBearerEvents
            {
                // obk_ tokens belong to the API-token handler (the selector never sends them here; belt and braces).
                OnMessageReceived = ctx =>
                {
                    if (ctx.Request.Headers.Authorization.ToString() is var h && h.Contains(" " + Services.ApiTokenService.TokenPrefix, StringComparison.Ordinal))
                        ctx.NoResult();
                    return Task.CompletedTask;
                },
                OnTokenValidated = ValidateMcpTokenAsync,
                OnAuthenticationFailed = ctx =>
                {
                    ctx.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("OpenBudget.McpAuth")
                        .LogInformation("MCP Entra token rejected ({Kind})", ctx.Exception.GetType().Name);
                    return Task.CompletedTask;
                },
                OnChallenge = async ctx =>
                {
                    ctx.HandleResponse();
                    await WriteMcpChallengeAsync(ctx.HttpContext);
                },
            };
        });
        auth.Services.AddOptions<JwtBearerOptions>(McpEntraScheme).Configure<IConfiguration>((o, c) =>
        {
            var tenant = TenantId(c) ?? "invalid-tenant";
            o.Authority = $"{AuthorityBase}/{tenant}/v2.0";
            o.TokenValidationParameters.ValidIssuers = [$"{AuthorityBase}/{tenant}/v2.0", $"https://sts.windows.net/{tenant}/"];
            o.TokenValidationParameters.ValidAudiences = McpAudiences(c);
            o.TokenValidationParameters.ClockSkew = TimeSpan.FromMinutes(2);
        });

        auth.AddPolicyScheme(McpBearerScheme, null, o => o.ForwardDefaultSelector = ctx =>
        {
            var header = ctx.Request.Headers.Authorization.ToString();
            var isApiToken = header.StartsWith("Bearer " + Services.ApiTokenService.TokenPrefix, StringComparison.OrdinalIgnoreCase);
            return !isApiToken && McpEnabled(ctx.RequestServices.GetRequiredService<IConfiguration>())
                ? McpEntraScheme
                : ApiTokenAuthenticationHandler.SchemeName;
        });
        return auth;
    }

    /// <summary>A validated Entra access token: right tenant, mcp.access scope, the MCP gateway as client, a linked user.</summary>
    private static async Task ValidateMcpTokenAsync(Microsoft.AspNetCore.Authentication.JwtBearer.TokenValidatedContext ctx)
    {
        var config = ctx.HttpContext.RequestServices.GetRequiredService<IConfiguration>();
        var log = ctx.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("OpenBudget.McpAuth");
        var principal = ctx.Principal!;
        string? Claim(string type) => principal.FindFirst(type)?.Value;

        void Forbid(string code, string message)
        {
            log.LogInformation("MCP Entra token refused: {Code}", code);
            ctx.HttpContext.Items[ForbiddenItem] = (code, message);
            ctx.Fail(code);
        }

        if (!string.Equals(Claim("tid"), TenantId(config), StringComparison.OrdinalIgnoreCase))
        {
            ctx.Fail("tenant");
            return;
        }
        var scopes = (Claim("scp") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (!scopes.Contains(McpScope, StringComparer.Ordinal))
        {
            Forbid("insufficient_scope", $"The token lacks the '{McpScope}' scope.");
            return;
        }
        var client = Claim("azp") ?? Claim("appid");
        if (!string.Equals(client, McpAllowedClient(config), StringComparison.OrdinalIgnoreCase))
        {
            Forbid("client_not_allowed", "The token was not issued to the allowed client.");
            return;
        }
        var userId = await FindLinkedUserAsync(ctx.HttpContext.RequestServices.GetRequiredService<SystemDbContextFactory>(),
            Claim("tid"), Claim("oid"), ctx.HttpContext.RequestAborted);
        if (userId == null)
        {
            Forbid("user_not_linked", "This Microsoft account is not linked to an OpenBudget user.");
            return;
        }
        log.LogInformation("MCP request authenticated via Entra access token");
        ctx.Principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, userId), new Claim(ApiTokenAuthenticationHandler.MethodClaim, "entra")], McpEntraScheme));
    }

    private static async Task WriteMcpChallengeAsync(HttpContext http)
    {
        if (http.Items[ForbiddenItem] is ValueTuple<string, string>(var code, var message))
        {
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            await http.Response.WriteAsJsonAsync(new { error = code, message }, JsonSerializerOptions.Web, http.RequestAborted);
            return;
        }
        http.Response.StatusCode = StatusCodes.Status401Unauthorized;
        http.Response.Headers.WWWAuthenticate = "Bearer";
        await http.Response.WriteAsJsonAsync(new { error = "unauthorized", message = "A valid access token or API token is required." },
            JsonSerializerOptions.Web, http.RequestAborted);
    }

    /// <summary>The user a Microsoft account (tenant id, object id) is linked to, if that user still exists and isn't locked out.</summary>
    public static async Task<string?> FindLinkedUserAsync(SystemDbContextFactory system, string? tenantId, string? objectId, CancellationToken ct)
    {
        if (!Guid.TryParse(tenantId, out var tid) || !Guid.TryParse(objectId, out var oid)) return null;
        var t = tid.ToString("D");
        var o = oid.ToString("D");
        await using var db = await system.CreateDbContextAsync(ct);
        var user = await db.ExternalLogins.AsNoTracking()
            .Where(l => l.Provider == ExternalLogin.MicrosoftProvider && l.TenantId == t && l.ObjectId == o)
            .Join(db.Users, l => l.OwnerUserId, u => u.Id, (l, u) => new { u.Id, u.LockoutEnd })
            .FirstOrDefaultAsync(ct);
        if (user == null || (user.LockoutEnd is { } end && end > DateTimeOffset.UtcNow)) return null;
        return user.Id;
    }

    /// <summary>
    /// Applies <c>Auth:Microsoft:Links</c> (object id → user name, in the configured tenant): adds the missing links,
    /// leaves existing ones, never moves a link from one user to another. Idempotent; runs at startup.
    /// </summary>
    public static async Task SeedLinksAsync(IServiceProvider services, CancellationToken ct = default)
    {
        var config = services.GetRequiredService<IConfiguration>();
        var links = config.GetSection(LinksSection).GetChildren().ToList();
        if (links.Count == 0) return;
        var log = services.GetRequiredService<ILoggerFactory>().CreateLogger("OpenBudget.MicrosoftSignIn");
        if (TenantId(config) is not { } tenant)
        {
            log.LogWarning("{Section} is set but no tenant id is configured; links not applied", LinksSection);
            return;
        }
        await using var db = await services.GetRequiredService<SystemDbContextFactory>().CreateDbContextAsync(ct);
        foreach (var link in links)
        {
            if (!Guid.TryParse(link.Key, out var oidGuid) || string.IsNullOrWhiteSpace(link.Value)) continue;
            var oid = oidGuid.ToString("D");
            var normalized = link.Value.Trim().ToUpperInvariant();
            var userId = await db.Users.Where(u => u.NormalizedUserName == normalized).Select(u => u.Id).FirstOrDefaultAsync(ct);
            if (userId == null)
            {
                log.LogWarning("Microsoft link for object id {ObjectId}: user '{User}' does not exist", oid, link.Value.Trim());
                continue;
            }
            var existing = await db.ExternalLogins.FirstOrDefaultAsync(
                l => l.Provider == ExternalLogin.MicrosoftProvider && l.TenantId == tenant && l.ObjectId == oid, ct);
            if (existing != null)
            {
                if (existing.OwnerUserId != userId)
                    log.LogWarning("Microsoft link for object id {ObjectId} belongs to another user; left unchanged", oid);
                continue;
            }
            db.ExternalLogins.Add(new ExternalLogin { OwnerUserId = userId, TenantId = tenant, ObjectId = oid, DisplayName = "(configured)" });
            log.LogInformation("Microsoft link for object id {ObjectId} added from configuration", oid);
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The sign-in and link endpoints (the account page unlinks).</summary>
    public static void MapMicrosoftSignInEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(LoginPath, (HttpContext http, IConfiguration config, string? returnUrl) =>
        {
            if (!LoginEnabled(config)) return Results.NotFound();
            var props = new AuthenticationProperties { RedirectUri = CompletePath };
            props.Items["mode"] = "login";
            if (IsLocal(returnUrl)) props.Items["returnUrl"] = returnUrl;
            return Results.Challenge(props, [Scheme]);
        }).AllowAnonymous();

        app.MapGet(LinkPath, (HttpContext http, IConfiguration config) =>
        {
            if (!LoginEnabled(config) || http.User.FindFirstValue(ClaimTypes.NameIdentifier) is not { Length: > 0 } userId) return Results.NotFound();
            var props = new AuthenticationProperties { RedirectUri = CompletePath };
            props.Items["mode"] = "link";
            props.Items["user"] = userId;
            return Results.Challenge(props, [Scheme]);
        });

        app.MapGet(CompletePath, async (HttpContext http, IConfiguration config, SystemDbContextFactory system,
            SignInManager<AppUser> signIn, CancellationToken ct) =>
        {
            if (!LoginEnabled(config)) return Results.NotFound();
            var external = await http.AuthenticateAsync(IdentityConstants.ExternalScheme);
            await http.SignOutAsync(IdentityConstants.ExternalScheme);
            if (!external.Succeeded || external.Principal is not { } p) return Results.Redirect("/Account/Login?microsoft=failed");
            var tid = p.FindFirstValue("tid");
            var oid = p.FindFirstValue("oid");
            var tenant = TenantId(config);
            if (!Guid.TryParse(tid, out var tidGuid) || !Guid.TryParse(oid, out var oidGuid) || tidGuid.ToString("D") != tenant)
                return Results.Redirect("/Account/Login?microsoft=failed");
            var mode = external.Properties?.Items.TryGetValue("mode", out var m) == true ? m : "login";

            if (mode == "link")
            {
                var current = await http.AuthenticateAsync(IdentityConstants.ApplicationScheme);
                var userId = current.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
                if (userId == null || Item(external.Properties, "user") != userId)
                    return Results.Redirect("/Account/Login");
                var oidText = oidGuid.ToString("D");
                await using var db = await system.CreateDbContextAsync(ct);
                var existing = await db.ExternalLogins.FirstOrDefaultAsync(
                    l => l.Provider == ExternalLogin.MicrosoftProvider && l.TenantId == tenant && l.ObjectId == oidText, ct);
                if (existing != null)
                    return Results.Redirect(existing.OwnerUserId == userId ? "/Account?microsoft=linked" : "/Account?microsoft=taken");
                db.ExternalLogins.Add(new ExternalLogin
                {
                    OwnerUserId = userId,
                    TenantId = tenant!,
                    ObjectId = oidText,
                    DisplayName = p.FindFirstValue("preferred_username") ?? p.FindFirstValue("name"),
                });
                await db.SaveChangesAsync(ct);
                return Results.Redirect("/Account?microsoft=linked");
            }

            var linked = await FindLinkedUserAsync(system, tid, oid, ct);
            if (linked == null) return Results.Redirect("/Account/Login?microsoft=not_linked");
            var user = await signIn.UserManager.FindByIdAsync(linked);
            if (user == null) return Results.Redirect("/Account/Login?microsoft=not_linked");
            await signIn.SignInAsync(user, isPersistent: true, authenticationMethod: Scheme);
            var back = Item(external.Properties, "returnUrl");
            return Results.Redirect(IsLocal(back) ? back! : "/");
        }).AllowAnonymous();
    }

    private static string? Item(AuthenticationProperties? props, string key) =>
        props != null && props.Items.TryGetValue(key, out var v) ? v : null;

    private static bool IsLocal(string? url) =>
        !string.IsNullOrEmpty(url) && url[0] == '/' && (url.Length == 1 || (url[1] != '/' && url[1] != '\\'));
}
