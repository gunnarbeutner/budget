using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using System.Web;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using OpenBudget.Auth;
using OpenBudget.Models;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests;

/// <summary>
/// A stand-in for Microsoft Entra ID: signs id tokens for the OpenID Connect sign-in (served from a fake token endpoint)
/// and access tokens for the MCP API, with its own key, so the app's real handlers validate them offline.
/// </summary>
internal sealed class FakeEntra : IDisposable
{
    public const string Tenant = "7d7a24f9-c6dc-4b10-b20d-413e1d4ce0d1";
    public const string OtherTenant = "11111111-2222-3333-4444-555555555555";
    public const string ClientId = "3b673f22-ba30-4dc9-affc-5f946fe10de8";
    public const string GatewayClient = "c406b8b1-3f73-41cd-bf9b-e10e42514418";
    public const string AliceOid = "aaaaaaaa-0000-0000-0000-000000000001";
    public const string BobOid = "bbbbbbbb-0000-0000-0000-000000000002";
    public const string StrangerOid = "cccccccc-0000-0000-0000-000000000003";
    public static string IssuerV2(string tenant = Tenant) => $"https://login.microsoftonline.com/{tenant}/v2.0";
    public static string IssuerV1(string tenant = Tenant) => $"https://sts.windows.net/{tenant}/";

    private readonly RSA _rsa = RSA.Create(2048);
    private readonly RsaSecurityKey _key;
    private readonly RsaSecurityKey _otherKey = new(RSA.Create(2048)) { KeyId = "other" };

    public FakeEntra() => _key = new RsaSecurityKey(_rsa) { KeyId = "test-key" };

    /// <summary>The object id the next sign-in at the fake authorize page signs in as.</summary>
    public string NextOid { get; set; } = AliceOid;
    public string NextTenant { get; set; } = Tenant;

    /// <summary>Nonce of the last authorize request (echoed into the id token).</summary>
    public string? Nonce { get; set; }
    public bool SawPkceVerifier { get; private set; }

    public static (string Key, string Value)[] Settings(bool login = true, bool mcp = true, params (string, string)[] extra) =>
    [
        ("McpApi:Enabled", "true"),
        ("Auth:Microsoft:Enabled", login ? "true" : "false"),
        ("Auth:Microsoft:TenantId", Tenant),
        ("OPENBUDGET_ONEDRIVE_CLIENT_ID", ClientId),
        ("OPENBUDGET_ONEDRIVE_CLIENT_SECRET", "test-secret"),
        ("McpApi:Entra:Enabled", mcp ? "true" : "false"),
        ("McpApi:AllowedClientId", GatewayClient),
        .. extra,
    ];

    public void Configure(IServiceCollection services)
    {
        var config = new OpenIdConnectConfiguration
        {
            Issuer = IssuerV2(),
            AuthorizationEndpoint = "https://idp.test/authorize",
            TokenEndpoint = "https://idp.test/token",
        };
        config.SigningKeys.Add(_key);
        services.Configure<OpenIdConnectOptions>(MicrosoftSignIn.Scheme, o =>
        {
            o.Configuration = config;
            o.Backchannel = new HttpClient(new TokenEndpoint(this));
        });
        services.Configure<JwtBearerOptions>(MicrosoftSignIn.McpEntraScheme, o => o.Configuration = config);
    }

    public string Sign(IDictionary<string, object> claims, bool otherKey = false) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Claims = claims,
            SigningCredentials = new SigningCredentials(otherKey ? _otherKey : _key, SecurityAlgorithms.RsaSha256),
            IssuedAt = DateTime.UtcNow,
            NotBefore = DateTime.UtcNow.AddMinutes(-1),
            Expires = DateTime.UtcNow.AddMinutes(30),
        });

    /// <summary>An MCP access token; each argument can be spoiled for the negative cases.</summary>
    public string AccessToken(string oid, string? aud = null, string scp = "mcp.access", string azp = GatewayClient,
        string tenant = Tenant, bool v1 = false, bool otherKey = false)
    {
        var claims = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["iss"] = v1 ? IssuerV1(tenant) : IssuerV2(tenant),
            ["aud"] = aud ?? (v1 ? "api://" + ClientId : ClientId),
            ["tid"] = tenant,
            ["oid"] = oid,
            ["sub"] = "pairwise-" + oid,
        };
        if (scp.Length > 0) claims["scp"] = scp;
        claims[v1 ? "appid" : "azp"] = azp;
        return Sign(claims, otherKey);
    }

    private sealed class TokenEndpoint(FakeEntra idp) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var form = HttpUtility.ParseQueryString(await request.Content!.ReadAsStringAsync(ct));
            idp.SawPkceVerifier = !string.IsNullOrEmpty(form["code_verifier"]);
            var idToken = idp.Sign(new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["iss"] = IssuerV2(idp.NextTenant),
                ["aud"] = ClientId,
                ["tid"] = idp.NextTenant,
                ["oid"] = idp.NextOid,
                ["sub"] = "pairwise-" + idp.NextOid,
                ["nonce"] = idp.Nonce!,
                ["name"] = "Test Person",
                ["preferred_username"] = "person@example.org",
            });
            var body = JsonSerializer.Serialize(new { access_token = "unused", token_type = "Bearer", expires_in = 3600, id_token = idToken });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        }
    }

    /// <summary>
    /// Runs the browser's part of the OpenID Connect round trip: start at <paramref name="startPath"/>, "sign in" at the
    /// fake authorize page, return to the callback, and follow to the app's completion. Returns where it finally leads.
    /// </summary>
    public async Task<string> RoundTripAsync(AppTestHost host, HttpClient client, string startPath)
    {
        using var start = await client.GetAsync(new Uri(startPath, UriKind.Relative));
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        var authorize = start.Headers.Location!;
        Assert.StartsWith("https://idp.test/authorize", authorize.AbsoluteUri, StringComparison.Ordinal);
        var q = HttpUtility.ParseQueryString(authorize.Query);
        Assert.Equal("openid profile", q["scope"]);
        Assert.Equal("S256", q["code_challenge_method"]);
        Assert.Equal(FakeEntra.ClientId, q["client_id"]);
        Assert.EndsWith(MicrosoftSignIn.CallbackPath, q["redirect_uri"], StringComparison.Ordinal);
        Nonce = q["nonce"];

        using var callback = await client.GetAsync(new Uri($"{MicrosoftSignIn.CallbackPath}?code=the-code&state={Uri.EscapeDataString(q["state"]!)}", UriKind.Relative));
        Assert.True(callback.StatusCode == HttpStatusCode.Redirect, $"callback -> {(int)callback.StatusCode}: {string.Join("\n", host.LoggedProblems)}");
        Assert.True(SawPkceVerifier);
        var complete = callback.Headers.Location!.OriginalString;
        if (!complete.StartsWith(MicrosoftSignIn.CompletePath, StringComparison.Ordinal)) return complete;
        using var done = await client.GetAsync(new Uri(complete, UriKind.Relative));
        Assert.Equal(HttpStatusCode.Redirect, done.StatusCode);
        return done.Headers.Location!.OriginalString;
    }

    public void Dispose()
    {
        _rsa.Dispose();
        _otherKey.Rsa?.Dispose();
    }
}

public sealed class MicrosoftSignInTests : IDisposable
{
    private readonly FakeEntra _idp = new();
    private readonly AppTestHost _host;

    public MicrosoftSignInTests()
    {
        _host = new AppTestHost(_idp.Configure, FakeEntra.Settings());
    }

    public void Dispose()
    {
        _host.Dispose();
        _idp.Dispose();
    }

    private async Task<string> UserIdAsync(string name)
    {
        await using var db = await _host.System.CreateDbContextAsync();
        return await db.Users.Where(u => u.UserName == name).Select(u => u.Id).SingleAsync();
    }

    private async Task LinkAsync(string userName, string oid, string tenant = FakeEntra.Tenant)
    {
        await using var db = await _host.System.CreateDbContextAsync();
        db.ExternalLogins.Add(new ExternalLogin { OwnerUserId = await UserIdAsync(userName), TenantId = tenant, ObjectId = oid });
        await db.SaveChangesAsync();
    }

    private static async Task<HttpResponseMessage> GetAsync(HttpClient client, string url) =>
        await client.GetAsync(new Uri(url, UriKind.RelativeOrAbsolute));

    private static async Task<bool> IsSignedInAsync(HttpClient client)
    {
        using var account = await GetAsync(client, "/Account");
        return account.StatusCode == HttpStatusCode.OK;
    }

    [Fact]
    public async Task A_linked_microsoft_account_signs_in_with_pkce_and_only_openid_profile()
    {
        await _host.SeedWorldAsync();
        await LinkAsync("alice", FakeEntra.AliceOid);
        using var client = _host.CreateClient();

        var target = await _idp.RoundTripAsync(_host, client, MicrosoftSignIn.LoginPath + "?returnUrl=%2Fabout");
        Assert.Equal("/about", target);
        var (status, html) = await AppTestHost.GetAsync(client, "/Account");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("alice", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unlinked_microsoft_account_is_refused_and_nothing_is_created()
    {
        await _host.SeedWorldAsync();
        _idp.NextOid = FakeEntra.StrangerOid;
        using var client = _host.CreateClient();
        int usersBefore;
        await using (var db = await _host.System.CreateDbContextAsync()) usersBefore = await db.Users.CountAsync();

        var target = await _idp.RoundTripAsync(_host, client, MicrosoftSignIn.LoginPath);
        Assert.Equal("/Account/Login?microsoft=not_linked", target);
        Assert.False(await IsSignedInAsync(client));
        var (_, login) = await AppTestHost.GetAsync(client, target);
        Assert.Contains("microsoft-message", login, StringComparison.Ordinal);

        await using (var db = await _host.System.CreateDbContextAsync())
        {
            Assert.Equal(usersBefore, await db.Users.CountAsync());
            Assert.Equal(0, await db.ExternalLogins.CountAsync());
        }
    }

    [Fact]
    public async Task An_account_from_another_tenant_is_refused()
    {
        await _host.SeedWorldAsync();
        await LinkAsync("alice", FakeEntra.AliceOid, FakeEntra.OtherTenant);
        _idp.NextTenant = FakeEntra.OtherTenant;
        using var client = _host.CreateClient();
        var target = await _idp.RoundTripAsync(_host, client, MicrosoftSignIn.LoginPath);
        Assert.StartsWith("/Account/Login?microsoft=failed", target, StringComparison.Ordinal);
        Assert.False(await IsSignedInAsync(client));
    }

    [Fact]
    public async Task A_signed_in_user_links_and_unlinks_their_microsoft_account()
    {
        await _host.SeedWorldAsync();
        using var alice = await _host.LoggedInClientAsync("alice");
        Assert.Equal("/Account?microsoft=linked", await _idp.RoundTripAsync(_host, alice, MicrosoftSignIn.LinkPath));

        int linkId;
        await using (var db = await _host.System.CreateDbContextAsync())
        {
            var link = await db.ExternalLogins.SingleAsync();
            Assert.Equal(await UserIdAsync("alice"), link.OwnerUserId);
            Assert.Equal(FakeEntra.Tenant, link.TenantId);
            Assert.Equal(FakeEntra.AliceOid, link.ObjectId);
            linkId = link.Id;
        }
        var (_, page) = await AppTestHost.GetAsync(alice, "/Account");
        Assert.Contains("person@example.org", page, StringComparison.Ordinal);

        // Now Microsoft signs alice in.
        using (var fresh = _host.CreateClient())
        {
            await _idp.RoundTripAsync(_host, fresh, MicrosoftSignIn.LoginPath);
            Assert.True(await IsSignedInAsync(fresh));
        }

        // Bob can't take the same Microsoft account.
        using (var bob = await _host.LoggedInClientAsync("bob"))
            Assert.Equal("/Account?microsoft=taken", await _idp.RoundTripAsync(_host, bob, MicrosoftSignIn.LinkPath));

        // Unlink through the account page's form.
        var token = await AppTestHost.FetchAntiforgeryTokenAsync(alice, "/Account");
        using (var unlink = await AppTestHost.PostFormAsync(alice, "/Account?handler=UnlinkMicrosoft",
                   KeyValuePair.Create("linkId", linkId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                   KeyValuePair.Create("__RequestVerificationToken", token)))
            Assert.Equal(HttpStatusCode.Redirect, unlink.StatusCode);
        await using (var db = await _host.System.CreateDbContextAsync())
            Assert.Equal(0, await db.ExternalLogins.CountAsync());

        using var after = _host.CreateClient();
        Assert.Equal("/Account/Login?microsoft=not_linked", await _idp.RoundTripAsync(_host, after, MicrosoftSignIn.LoginPath));
    }

    [Fact]
    public async Task Password_login_is_unchanged_and_the_login_page_offers_microsoft()
    {
        await _host.SeedWorldAsync();
        using var anonymous = _host.CreateClient();
        var (_, login) = await AppTestHost.GetAsync(anonymous, "/Account/Login");
        Assert.Contains("microsoft-login", login, StringComparison.Ordinal);
        Assert.Contains("passkey-login", login, StringComparison.Ordinal);

        using var alice = await _host.LoggedInClientAsync("alice");
        Assert.True(await IsSignedInAsync(alice));
        using var wrong = _host.CreateClient();
        using var refused = await AppTestHost.LoginAsync(wrong, "alice", "wrong-password");
        Assert.Equal(HttpStatusCode.OK, refused.StatusCode);   // the form again, with an error
        Assert.False(await IsSignedInAsync(wrong));
    }

    // ---- MCP API with Entra access tokens ----

    private HttpClient Bearer(string token)
    {
        var client = _host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<(HttpStatusCode Status, JsonElement Json)> McpAsync(string token, string path = "/api/mcp/v1/accounts")
    {
        using var client = Bearer(token);
        var (status, body) = await AppTestHost.GetAsync(client, path);
        Assert.True(status != HttpStatusCode.InternalServerError, string.Join("\n", _host.LoggedProblems));
        return (status, body.Length == 0 ? default : JsonElement.Parse(body));
    }

    [Fact]
    public async Task Entra_access_tokens_open_the_mcp_api_only_when_valid_and_linked()
    {
        await _host.SeedWorldAsync();
        await LinkAsync("alice", FakeEntra.AliceOid);

        Assert.Equal(HttpStatusCode.OK, (await McpAsync(_idp.AccessToken(FakeEntra.AliceOid))).Status);
        Assert.Equal(HttpStatusCode.OK, (await McpAsync(_idp.AccessToken(FakeEntra.AliceOid, v1: true))).Status);
        Assert.Equal(HttpStatusCode.OK, (await McpAsync(_idp.AccessToken(FakeEntra.AliceOid, scp: "other mcp.access"))).Status);

        Assert.Equal(HttpStatusCode.Unauthorized, (await McpAsync(_idp.AccessToken(FakeEntra.AliceOid, aud: "api://something-else"))).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await McpAsync(_idp.AccessToken(FakeEntra.AliceOid, tenant: FakeEntra.OtherTenant))).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await McpAsync(_idp.AccessToken(FakeEntra.AliceOid, otherKey: true))).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await McpAsync("not.a.jwt")).Status);

        var (azpStatus, azp) = await McpAsync(_idp.AccessToken(FakeEntra.AliceOid, azp: "00000000-0000-0000-0000-000000000009"));
        Assert.Equal(HttpStatusCode.Forbidden, azpStatus);
        Assert.Equal("client_not_allowed", azp.GetProperty("error").GetString());
        var (v1AzpStatus, _) = await McpAsync(_idp.AccessToken(FakeEntra.AliceOid, azp: "00000000-0000-0000-0000-000000000009", v1: true));
        Assert.Equal(HttpStatusCode.Forbidden, v1AzpStatus);

        var (scopeStatus, scope) = await McpAsync(_idp.AccessToken(FakeEntra.AliceOid, scp: "User.Read"));
        Assert.Equal(HttpStatusCode.Forbidden, scopeStatus);
        Assert.Equal("insufficient_scope", scope.GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await McpAsync(_idp.AccessToken(FakeEntra.AliceOid, scp: ""))).Status);

        var (unlinkedStatus, unlinked) = await McpAsync(_idp.AccessToken(FakeEntra.StrangerOid));
        Assert.Equal(HttpStatusCode.Forbidden, unlinkedStatus);
        Assert.Equal("user_not_linked", unlinked.GetProperty("error").GetString());

        // API tokens keep working alongside.
        var userId = await UserIdAsync("alice");
        await using var scopeSvc = _host.Services.CreateAsyncScope();
        scopeSvc.ServiceProvider.GetRequiredService<ActingUser>().Set(userId);
        var apiToken = await scopeSvc.ServiceProvider.GetRequiredService<ApiTokenService>().CreateAsync("t", null);
        Assert.Equal(HttpStatusCode.OK, (await McpAsync(apiToken.Token)).Status);
    }

    [Fact]
    public async Task Two_linked_users_see_only_their_own_data()
    {
        await _host.SeedWorldAsync(ProjectRole.Viewer);
        await LinkAsync("alice", FakeEntra.AliceOid);
        await LinkAsync("bob", FakeEntra.BobOid);

        static List<string> Names(JsonElement json) => json.GetProperty("accounts").EnumerateArray().Select(a => a.GetProperty("name").GetString()!).ToList();
        var alice = Names((await McpAsync(_idp.AccessToken(FakeEntra.AliceOid))).Json);
        var bob = Names((await McpAsync(_idp.AccessToken(FakeEntra.BobOid))).Json);
        Assert.Contains("Giro Private", alice);
        Assert.DoesNotContain("Bob Giro", alice);
        Assert.Contains("Bob Giro", bob);
        Assert.DoesNotContain("Giro Private", bob);

        var bobTx = (await McpAsync(_idp.AccessToken(FakeEntra.BobOid), "/api/mcp/v1/transactions")).Json.GetProperty("transactions");
        Assert.DoesNotContain("Payee Private", bobTx.EnumerateArray().Select(t => t.GetProperty("payee").GetString()));
    }

    [Fact]
    public async Task Entra_access_tokens_open_nothing_outside_the_mcp_api()
    {
        await _host.SeedWorldAsync();
        await LinkAsync("alice", FakeEntra.AliceOid);
        using var client = Bearer(_idp.AccessToken(FakeEntra.AliceOid));
        foreach (var path in new[] { "/", "/projects/1/browse", "/Account", "/api-tokens", "/statements/1", "/api/other", MicrosoftSignIn.LinkPath })
        {
            using var response = await GetAsync(client, path);
            Assert.True(response.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Unauthorized, $"{path} -> {(int)response.StatusCode}");
            if (response.StatusCode == HttpStatusCode.Redirect)
                Assert.Contains("/Account/Login", response.Headers.Location!.OriginalString, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Configured_links_are_seeded_idempotently()
    {
        await _host.SeedWorldAsync();
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            FakeEntra.Settings().Select(s => KeyValuePair.Create(s.Key, (string?)s.Value)).Concat(
            [
                KeyValuePair.Create($"Auth:Microsoft:Links:{FakeEntra.AliceOid}", (string?)"alice"),
                KeyValuePair.Create($"Auth:Microsoft:Links:{FakeEntra.StrangerOid}", (string?)"nobody"),
            ])).Build();
        var services = new ServiceCollection()
            .AddSingleton<Microsoft.Extensions.Configuration.IConfiguration>(config)
            .AddSingleton(_host.System)
            .AddLogging()
            .BuildServiceProvider();
        await MicrosoftSignIn.SeedLinksAsync(services);
        await MicrosoftSignIn.SeedLinksAsync(services);

        await using (var db = await _host.System.CreateDbContextAsync())
        {
            var link = await db.ExternalLogins.SingleAsync();
            Assert.Equal(await UserIdAsync("alice"), link.OwnerUserId);
            Assert.Equal(FakeEntra.Tenant, link.TenantId);
        }
        // The seeded link signs alice in.
        using var client = _host.CreateClient();
        await _idp.RoundTripAsync(_host, client, MicrosoftSignIn.LoginPath);
        Assert.True(await IsSignedInAsync(client));
    }
}

/// <summary>Microsoft sign-in and MCP Entra tokens switched off, and misconfiguration at startup.</summary>
public sealed class MicrosoftSignInDisabledTests
{
    [Fact]
    public async Task Disabled_features_offer_nothing_and_api_tokens_still_work()
    {
        using var idp = new FakeEntra();
        using var host = new AppTestHost(idp.Configure, FakeEntra.Settings(login: false, mcp: false));
        await host.SeedWorldAsync();
        using var anonymous = host.CreateClient();
        var (_, login) = await AppTestHost.GetAsync(anonymous, "/Account/Login");
        Assert.DoesNotContain("microsoft-login", login, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, (await AppTestHost.GetAsync(anonymous, MicrosoftSignIn.LoginPath)).Status);

        string userId;
        await using (var db = await host.System.CreateDbContextAsync())
        {
            userId = await db.Users.Where(u => u.UserName == "alice").Select(u => u.Id).SingleAsync();
            db.ExternalLogins.Add(new ExternalLogin { OwnerUserId = userId, TenantId = FakeEntra.Tenant, ObjectId = FakeEntra.AliceOid });
            await db.SaveChangesAsync();
        }
        using var jwt = host.CreateClient();
        jwt.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", idp.AccessToken(FakeEntra.AliceOid));
        Assert.Equal(HttpStatusCode.Unauthorized, (await AppTestHost.GetAsync(jwt, "/api/mcp/v1/accounts")).Status);

        await using var scope = host.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ActingUser>().Set(userId);
        var token = await scope.ServiceProvider.GetRequiredService<ApiTokenService>().CreateAsync("t", null);
        using var api = host.CreateClient();
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        Assert.Equal(HttpStatusCode.OK, (await AppTestHost.GetAsync(api, "/api/mcp/v1/accounts")).Status);
    }

    [Fact]
    public async Task Enabling_mcp_entra_tokens_without_their_settings_stops_the_start()
    {
        using var host = new AppTestHost(("McpApi:Entra:Enabled", "true"));
        var e = await Assert.ThrowsAnyAsync<Exception>(host.StartAsync);
        Assert.Contains("McpApi:AllowedClientId", e.ToString(), StringComparison.Ordinal);
    }
}
