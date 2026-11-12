using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenBudget.Auth;
using OpenBudget.Services;
using Xunit;

// Raw, deliberately malformed URL strings are the point of the return-url theory.
#pragma warning disable CA1054

namespace OpenBudget.Tests;

/// <summary>
/// The login requirement and setup state, end to end over HTTP against the real middleware pipeline.
/// </summary>
public sealed class AuthHttpTests : IDisposable
{
    private readonly AppTestHost _host = new();

    public void Dispose() => _host.Dispose();

    private static readonly string[] AppPages =
    {
        "/", "/sources", "/projects/1/overview", "/projects/1/browse", "/projects/1/budgets",
        "/projects/1/cashflow", "/projects/1/settings", "/projects/1/rebalance", "/projects/1/portfolio", "/budgets/1/edit",
        "/sources/1/depots", "/sources/1/tax", "/Account",
    };

    [Fact]
    public async Task Logged_out_pages_redirect_to_the_login_page()
    {
        await _host.StartAsync();
        await _host.AddUserAsync("alice", admin: true);
        using var client = _host.CreateClient();

        foreach (var path in AppPages)
        {
            using var response = await client.GetAsync(new Uri(path, UriKind.Relative));
            Assert.True(response.StatusCode == HttpStatusCode.Redirect, $"{path} -> {(int)response.StatusCode}");
            Assert.Equal("/Account/Login", new Uri(new Uri("https://localhost"), response.Headers.Location!).AbsolutePath);
        }
    }

    [Fact]
    public async Task Login_page_is_reachable_logged_out_and_static_assets_are_public()
    {
        await _host.StartAsync();
        await _host.AddUserAsync("alice");
        using var client = _host.CreateClient();

        var (status, body) = await AppTestHost.GetAsync(client, "/Account/Login");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("name=\"Password\"", body, StringComparison.Ordinal);
        Assert.Contains("passkey-login", body, StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.OK, (await AppTestHost.GetAsync(client, "/css/site.css")).Status);
        Assert.Equal(HttpStatusCode.OK, (await AppTestHost.GetAsync(client, "/js/passkeys.js")).Status);
        Assert.Equal(HttpStatusCode.OK, (await AppTestHost.GetAsync(client, "/healthz")).Status);
    }

    [Fact]
    public async Task Blazor_hub_negotiate_is_refused_when_logged_out()
    {
        await _host.StartAsync();
        await _host.AddUserAsync("alice");
        using var client = _host.CreateClient();

        using var negotiate = await client.PostAsync(new Uri("/_blazor/negotiate?negotiateVersion=1", UriKind.Relative), null);
        using var hub = await client.GetAsync(new Uri("/_blazor", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, negotiate.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, hub.StatusCode);
    }

    [Fact]
    public async Task Blazor_hub_negotiate_works_when_logged_in()
    {
        await _host.StartAsync();
        await _host.AddUserAsync("alice");
        using var client = await _host.LoggedInClientAsync("alice");

        using var negotiate = await client.PostAsync(new Uri("/_blazor/negotiate?negotiateVersion=1", UriKind.Relative), null);

        Assert.Equal(HttpStatusCode.OK, negotiate.StatusCode);
    }

    [Fact]
    public async Task Login_with_password_sets_a_persistent_hardened_cookie_and_opens_the_app()
    {
        await _host.StartAsync();
        await _host.AddUserAsync("alice");
        using var client = _host.CreateClient();

        using var login = await AppTestHost.LoginAsync(client, "alice");

        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Equal("/", login.Headers.Location!.OriginalString);
        var cookie = login.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("OpenBudget.Auth=", StringComparison.Ordinal));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        var expires = DateTimeOffset.Parse(
            cookie.Split(';').Single(p => p.Trim().StartsWith("expires=", StringComparison.OrdinalIgnoreCase)).Split('=', 2)[1],
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange((expires - DateTimeOffset.UtcNow).TotalDays, 29, 31);   // 30-day session

        var (status, body) = await AppTestHost.GetAsync(client, "/");
        Assert.True(status == HttpStatusCode.OK, string.Join("\n", _host.LoggedProblems));
        Assert.Contains("alice", body, StringComparison.Ordinal);   // the user menu
    }

    [Fact]
    public async Task Auth_cookie_is_secure_only_over_https()
    {
        await _host.StartAsync();
        await _host.AddUserAsync("alice");

        // https (behind the proxy via X-Forwarded-Proto): Secure.
        using (var https = _host.CreateClient())
        using (var login = await AppTestHost.LoginAsync(https, "alice"))
        {
            Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
            Assert.Contains("; secure", AuthCookie(login), StringComparison.OrdinalIgnoreCase);
        }

        // Plain http (a local container): not Secure, or Safari drops it and login silently fails.
        using (var http = _host.CreateClient("http://localhost"))
        using (var login = await AppTestHost.LoginAsync(http, "alice"))
        {
            Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
            Assert.DoesNotContain("; secure", AuthCookie(login), StringComparison.OrdinalIgnoreCase);
        }

        static string AuthCookie(HttpResponseMessage r) =>
            r.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("OpenBudget.Auth=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Wrong_password_is_rejected_and_repeated_failures_lock_the_account()
    {
        await _host.StartAsync();
        await _host.AddUserAsync("alice");
        using var client = _host.CreateClient();

        for (int i = 0; i < 5; i++)
        {
            using var bad = await AppTestHost.LoginAsync(client, "alice", "wrong-password");
            Assert.Equal(HttpStatusCode.OK, bad.StatusCode);   // the form again, with an error
        }
        // Locked out now: even the right password is refused.
        using var locked = await AppTestHost.LoginAsync(client, "alice");
        Assert.Equal(HttpStatusCode.OK, locked.StatusCode);
        var (status, _) = await AppTestHost.GetAsync(client, "/");
        Assert.Equal(HttpStatusCode.Redirect, status);
    }

    [Fact]
    public async Task Login_requires_the_antiforgery_token()
    {
        await _host.StartAsync();
        await _host.AddUserAsync("alice");
        using var client = _host.CreateClient();
        using var form = new FormUrlEncodedContent(new[] { KeyValuePair.Create("UserName", "alice"), KeyValuePair.Create("Password", AppTestHost.Password) });

        using var response = await client.PostAsync(new Uri("/Account/Login", UriKind.Relative), form);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("https://evil.example/steal")]
    [InlineData("//evil.example/steal")]
    [InlineData("/\\evil.example")]
    public async Task Login_does_not_redirect_to_foreign_return_urls(string returnUrl)
    {
        await _host.StartAsync();
        await _host.AddUserAsync("alice");
        using var client = _host.CreateClient();
        var token = await AppTestHost.FetchAntiforgeryTokenAsync(client, "/Account/Login");
        using var form = new FormUrlEncodedContent(new[] {
            KeyValuePair.Create("UserName", "alice"),
            KeyValuePair.Create("Password", AppTestHost.Password),
            KeyValuePair.Create("ReturnUrl", returnUrl),
            KeyValuePair.Create("__RequestVerificationToken", token),
        });

        using var response = await client.PostAsync(new Uri("/Account/Login", UriKind.Relative), form);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Login_honours_a_local_return_url()
    {
        await _host.StartAsync();
        await _host.AddUserAsync("alice");
        using var client = _host.CreateClient();
        var token = await AppTestHost.FetchAntiforgeryTokenAsync(client, "/Account/Login");
        using var form = new FormUrlEncodedContent(new[] {
            KeyValuePair.Create("UserName", "alice"),
            KeyValuePair.Create("Password", AppTestHost.Password),
            KeyValuePair.Create("ReturnUrl", "/projects/1/overview"),
            KeyValuePair.Create("__RequestVerificationToken", token),
        });

        using var response = await client.PostAsync(new Uri("/Account/Login", UriKind.Relative), form);

        Assert.Equal("/projects/1/overview", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Logout_is_a_post_with_antiforgery_and_ends_the_session()
    {
        await _host.StartAsync();
        await _host.AddUserAsync("alice");
        using var client = await _host.LoggedInClientAsync("alice");
        Assert.Equal(HttpStatusCode.OK, (await AppTestHost.GetAsync(client, "/")).Status);

        // GET only shows a confirmation and does not sign out.
        Assert.Equal(HttpStatusCode.OK, (await AppTestHost.GetAsync(client, "/Account/Logout")).Status);
        Assert.Equal(HttpStatusCode.OK, (await AppTestHost.GetAsync(client, "/")).Status);

        using var empty = new FormUrlEncodedContent(Array.Empty<KeyValuePair<string, string>>());
        using var noToken = await client.PostAsync(new Uri("/Account/Logout", UriKind.Relative), empty);
        Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);

        var token = await AppTestHost.FetchAntiforgeryTokenAsync(client, "/Account/Logout");
        using var confirm = new FormUrlEncodedContent(new[] { KeyValuePair.Create("__RequestVerificationToken", token) });
        using var out1 = await client.PostAsync(new Uri("/Account/Logout", UriKind.Relative), confirm);
        Assert.Equal(HttpStatusCode.Redirect, out1.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await AppTestHost.GetAsync(client, "/")).Status);
    }

    [Fact]
    public async Task Changing_the_password_requires_the_current_one_and_keeps_this_session()
    {
        await _host.StartAsync();
        await _host.AddUserAsync("alice");
        using var client = await _host.LoggedInClientAsync("alice");
        var token = await AppTestHost.FetchAntiforgeryTokenAsync(client, "/Account");

        async Task<HttpResponseMessage> Change(string current, string next) => await AppTestHost.PostFormAsync(
            client, "/Account?handler=ChangePassword",
            KeyValuePair.Create("currentPassword", current),
            KeyValuePair.Create("newPassword", next),
            KeyValuePair.Create("confirmPassword", next),
            KeyValuePair.Create("__RequestVerificationToken", token));

        using (var wrong = await Change("not-my-password", "Brand-New-Pass-1"))
            Assert.Equal(HttpStatusCode.OK, wrong.StatusCode);   // re-rendered with an error
        using (var ok = await Change(AppTestHost.Password, "Brand-New-Pass-1"))
            Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await AppTestHost.GetAsync(client, "/")).Status);
        using var fresh = _host.CreateClient();
        using var oldLogin = await AppTestHost.LoginAsync(fresh, "alice");
        Assert.Equal(HttpStatusCode.OK, oldLogin.StatusCode);   // old password no longer works
        using var newLogin = await AppTestHost.LoginAsync(fresh, "alice", "Brand-New-Pass-1");
        Assert.Equal(HttpStatusCode.Redirect, newLogin.StatusCode);
    }

    // ---- Passkeys: option endpoints (the WebAuthn ceremony itself needs a browser authenticator) ----

    [Fact]
    public async Task Passkey_creation_options_are_issued_to_the_logged_in_user_only()
    {
        await _host.StartAsync();
        await _host.AddUserAsync("alice");

        using var anonymous = _host.CreateClient();
        var anonToken = await AppTestHost.FetchAntiforgeryTokenAsync(anonymous, "/Account/Login");
        using var denied = await PostWithTokenAsync(anonymous, "/Account?handler=PasskeyCreationOptions", anonToken);
        Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);   // login redirect

        using var client = await _host.LoggedInClientAsync("alice");
        var token = await AppTestHost.FetchAntiforgeryTokenAsync(client, "/Account");
        using var response = await PostWithTokenAsync(client, "/Account?handler=PasskeyCreationOptions", token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(string.IsNullOrEmpty(json.RootElement.GetProperty("challenge").GetString()));
        Assert.Equal("alice", json.RootElement.GetProperty("user").GetProperty("name").GetString());
        Assert.Equal("localhost", json.RootElement.GetProperty("rp").GetProperty("id").GetString());   // defaults to the request host
    }

    [Fact]
    public async Task Passkey_creation_options_require_the_antiforgery_token()
    {
        await _host.StartAsync();
        await _host.AddUserAsync("alice");
        using var client = await _host.LoggedInClientAsync("alice");

        using var response = await client.PostAsync(new Uri("/Account?handler=PasskeyCreationOptions", UriKind.Relative), null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Passkey_assertion_options_are_available_on_the_login_page()
    {
        await _host.StartAsync();
        await _host.AddUserAsync("alice");
        using var client = _host.CreateClient();
        var token = await AppTestHost.FetchAntiforgeryTokenAsync(client, "/Account/Login");

        using var response = await PostWithTokenAsync(client, "/Account/Login?handler=PasskeyOptions", token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(string.IsNullOrEmpty(json.RootElement.GetProperty("challenge").GetString()));
        Assert.Equal("localhost", json.RootElement.GetProperty("rpId").GetString());
    }

    [Fact]
    public async Task A_bogus_passkey_assertion_does_not_log_anyone_in()
    {
        await _host.StartAsync();
        await _host.AddUserAsync("alice");
        using var client = _host.CreateClient();
        var token = await AppTestHost.FetchAntiforgeryTokenAsync(client, "/Account/Login");
        using var options = await PostWithTokenAsync(client, "/Account/Login?handler=PasskeyOptions", token);   // starts the ceremony

        using var form = new FormUrlEncodedContent(new[] {
            KeyValuePair.Create("credentialJson", """{"id":"AAAA","rawId":"AAAA","type":"public-key","response":{"clientDataJSON":"e30","authenticatorData":"AAAA","signature":"AAAA","userHandle":"AAAA"},"clientExtensionResults":{}}"""),
            KeyValuePair.Create("__RequestVerificationToken", token),
        });
        using var response = await client.PostAsync(new Uri("/Account/Login?handler=PasskeyLogin", UriKind.Relative), form);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);   // login form again, with the error
        Assert.Equal(HttpStatusCode.Redirect, (await AppTestHost.GetAsync(client, "/")).Status);

        // The password form must still post to the login page itself, not back to ?handler=PasskeyLogin.
        var body = await response.Content.ReadAsStringAsync();
        var start = body.IndexOf("id=\"login-form\"", StringComparison.Ordinal);
        var loginForm = body[start..body.IndexOf("</form>", start, StringComparison.Ordinal)];
        Assert.Contains("action=\"/Account/Login\"", loginForm, StringComparison.Ordinal);
        Assert.Contains("__RequestVerificationToken", loginForm, StringComparison.Ordinal);   // still posts successfully
    }

    [Fact]
    public async Task Configured_relying_party_id_is_used_for_passkeys()
    {
        using var host = new AppTestHost(("OPENBUDGET_RP_ID", "budget.example.org"));
        await host.StartAsync();
        await host.AddUserAsync("alice");
        using var client = host.CreateClient();
        var token = await AppTestHost.FetchAntiforgeryTokenAsync(client, "/Account/Login");

        using var response = await PostWithTokenAsync(client, "/Account/Login?handler=PasskeyOptions", token);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("budget.example.org", json.RootElement.GetProperty("rpId").GetString());
    }

    [Theory]
    [InlineData("https://budget.example.org", false, true)]
    [InlineData("https://app.budget.example.org", false, true)]
    [InlineData("https://evil.example.org", false, false)]
    [InlineData("https://notbudget.example.org", false, false)]
    [InlineData("http://budget.example.org", false, false)]
    [InlineData("https://budget.example.org", true, false)]
    [InlineData("not a url", false, false)]
    public void Origin_check_is_pinned_to_the_relying_party_id(string origin, bool crossOrigin, bool expected)
    {
        Assert.Equal(expected, AuthRegistration.IsOriginAllowed(origin, crossOrigin, "budget.example.org"));
    }

    [Fact]
    public async Task Account_page_lists_and_removes_stored_passkeys()
    {
        await _host.StartAsync();
        var user = await _host.AddUserAsync("alice");
        var credentialId = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        await using (var db = await _host.System.CreateDbContextAsync())
        {
            var created = DateTimeOffset.UtcNow;
            db.Set<Microsoft.AspNetCore.Identity.IdentityUserPasskey<string>>().Add(new Microsoft.AspNetCore.Identity.IdentityUserPasskey<string>
            {
                UserId = user.Id,
                CredentialId = credentialId,
                Data = new Microsoft.AspNetCore.Identity.IdentityPasskeyData
                {
                    PublicKey = new byte[] { 9, 9 },
                    Name = "My iPhone",
                    CreatedAt = created,
                    Transports = [],
                    AttestationObject = [],
                    ClientDataJson = [],
                },
            });
            await db.SaveChangesAsync();
        }
        using var client = await _host.LoggedInClientAsync("alice");

        var (_, page) = await AppTestHost.GetAsync(client, "/Account");
        Assert.Contains("My iPhone", page, StringComparison.Ordinal);

        var token = await AppTestHost.FetchAntiforgeryTokenAsync(client, "/Account");
        using var removed = await AppTestHost.PostFormAsync(
            client, "/Account?handler=DeletePasskey",
            KeyValuePair.Create("credentialId", System.Buffers.Text.Base64Url.EncodeToString(credentialId)),
            KeyValuePair.Create("__RequestVerificationToken", token));
        Assert.Equal(HttpStatusCode.Redirect, removed.StatusCode);
        var (_, after) = await AppTestHost.GetAsync(client, "/Account");
        Assert.DoesNotContain("My iPhone", after, StringComparison.Ordinal);
        await using var check = await _host.System.CreateDbContextAsync();
        Assert.Empty(await check.Set<Microsoft.AspNetCore.Identity.IdentityUserPasskey<string>>().ToListAsync());
    }

    private static async Task<HttpResponseMessage> PostWithTokenAsync(HttpClient client, string path, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(path, UriKind.Relative));
        request.Headers.Add("RequestVerificationToken", token);
        return await client.SendAsync(request);
    }

    // ---- First start: no users ----

    [Fact]
    public async Task With_no_users_every_page_shows_only_the_setup_message()
    {
        using var client = _host.CreateClient();
        string[] paths = ["/", "/Account/Login", "/Account", "/projects/1/overview", "/sources", "/anything-else"];

        foreach (var path in paths)
        {
            var (status, body) = await AppTestHost.GetAsync(client, path);
            Assert.True(status == HttpStatusCode.ServiceUnavailable, $"{path} -> {(int)status}");
            Assert.Contains(NoUsersMiddleware.SetupCommand, WebUtility.HtmlDecode(body), StringComparison.Ordinal);
            Assert.Contains("users add", body, StringComparison.Ordinal);
            // Nothing of the app or the login form is rendered, not even briefly.
            Assert.DoesNotContain("blazor", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Password", body, StringComparison.Ordinal);
            Assert.DoesNotContain("<form", body, StringComparison.OrdinalIgnoreCase);
        }

        using var negotiate = await client.PostAsync(new Uri("/_blazor/negotiate?negotiateVersion=1", UriKind.Relative), null);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, negotiate.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await AppTestHost.GetAsync(client, "/healthz")).Status);
    }

    [Fact]
    public async Task The_setup_message_goes_away_once_a_user_exists()
    {
        using var client = _host.CreateClient();
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await AppTestHost.GetAsync(client, "/")).Status);

        await _host.AddUserAsync("alice", admin: true);

        var (status, _) = await AppTestHost.GetAsync(client, "/");
        Assert.Equal(HttpStatusCode.Redirect, status);   // now: login redirect
    }

    [Fact]
    public async Task A_user_removed_while_logged_in_loses_access_at_once()
    {
        await _host.StartAsync();
        var user = await _host.AddUserAsync("alice");
        using var client = await _host.LoggedInClientAsync("alice");
        Assert.Equal(HttpStatusCode.OK, (await AppTestHost.GetAsync(client, "/")).Status);

        await using (var db = await _host.System.CreateDbContextAsync())
        {
            db.Users.Remove(await db.Users.SingleAsync(u => u.Id == user.Id));
            await db.SaveChangesAsync();
        }
        await _host.AddUserAsync("someone-else");   // keep the instance out of the first-start state

        // The cookie is still valid, but the stamp check (every minute) / user lookup fail it.
        var scope = await AccessScopeProvider.ComputeAsync(_host.System, user.Id);
        Assert.False(scope.IsAuthenticated);
    }
}
