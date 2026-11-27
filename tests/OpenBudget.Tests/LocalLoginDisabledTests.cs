using System.Net;
using System.Net.Http.Headers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBudget.Auth;
using OpenBudget.Models;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests;

/// <summary>
/// Auth:LocalLogin:Enabled=false: Microsoft is the only way in. Password and passkey sign-in and their management are
/// refused on the server, not only hidden; Microsoft sign-in, linking and the MCP credentials keep working.
/// </summary>
public sealed class LocalLoginDisabledTests : IDisposable
{
    private readonly FakeEntra _idp = new();
    private readonly AppTestHost _host;

    public LocalLoginDisabledTests()
    {
        _host = new AppTestHost(_idp.Configure, FakeEntra.Settings(true, true, (MicrosoftSignIn.LocalLoginSetting, "false")));
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

    private async Task LinkAsync(string userName, string oid)
    {
        await using var db = await _host.System.CreateDbContextAsync();
        db.ExternalLogins.Add(new ExternalLogin { OwnerUserId = await UserIdAsync(userName), TenantId = FakeEntra.Tenant, ObjectId = oid });
        await db.SaveChangesAsync();
    }

    private async Task<HttpClient> MicrosoftClientAsync()
    {
        var client = _host.CreateClient();
        await _idp.RoundTripAsync(_host, client, MicrosoftSignIn.LoginPath);
        return client;
    }

    [Fact]
    public async Task The_login_page_offers_only_microsoft_and_leads_there_directly()
    {
        await _host.SeedWorldAsync();
        using var client = _host.CreateClient();
        using (var page = await client.GetAsync(new Uri("/Account/Login?ReturnUrl=%2Fabout", UriKind.Relative)))
        {
            Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
            Assert.Equal(MicrosoftSignIn.LoginPath + "?returnUrl=%2Fabout", page.Headers.Location!.OriginalString);
        }
        var (status, html) = await AppTestHost.GetAsync(client, "/Account/Login?microsoft=not_linked");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("microsoft-login", html, StringComparison.Ordinal);
        Assert.Contains("microsoft-message", html, StringComparison.Ordinal);
        Assert.DoesNotContain("login-form", html, StringComparison.Ordinal);
        Assert.DoesNotContain("passkey-login", html, StringComparison.Ordinal);
        Assert.DoesNotContain("name=\"Password\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Password_and_passkey_sign_in_are_refused_on_the_server()
    {
        await _host.SeedWorldAsync();
        await LinkAsync("alice", FakeEntra.AliceOid);
        // The page has no forms (so no antiforgery token) any more; a valid token comes from a Microsoft-signed-in
        // client, so the requests below reach the handlers and are refused there.
        using var client = await MicrosoftClientAsync();
        var token = await AppTestHost.FetchAntiforgeryTokenAsync(client, "/Account");
        var antiforgery = KeyValuePair.Create("__RequestVerificationToken", token);

        using (var password = await AppTestHost.PostFormAsync(client, "/Account/Login",
                   KeyValuePair.Create("UserName", "alice"), KeyValuePair.Create("Password", AppTestHost.Password), antiforgery))
            Assert.Equal(HttpStatusCode.NotFound, password.StatusCode);
        using (var options = await AppTestHost.PostFormAsync(client, "/Account/Login?handler=PasskeyOptions", antiforgery))
            Assert.Equal(HttpStatusCode.NotFound, options.StatusCode);
        using (var assertion = await AppTestHost.PostFormAsync(client, "/Account/Login?handler=PasskeyLogin",
                   KeyValuePair.Create("credentialJson", "{}"), antiforgery))
            Assert.Equal(HttpStatusCode.NotFound, assertion.StatusCode);

        // And signed out, the login POST doesn't get anywhere either.
        using var anonymous = _host.CreateClient();
        using (var post = await AppTestHost.PostFormAsync(anonymous, "/Account/Login",
                   KeyValuePair.Create("UserName", "alice"), KeyValuePair.Create("Password", AppTestHost.Password)))
            Assert.True(post.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest, $"{(int)post.StatusCode}");
        using var account = await anonymous.GetAsync(new Uri("/Account", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Redirect, account.StatusCode);
    }

    [Fact]
    public async Task Microsoft_sign_in_works_and_password_and_passkey_management_is_refused()
    {
        await _host.SeedWorldAsync();
        await LinkAsync("alice", FakeEntra.AliceOid);
        using var alice = await MicrosoftClientAsync();

        var (status, page) = await AppTestHost.GetAsync(alice, "/Account");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.DoesNotContain("currentPassword", page, StringComparison.Ordinal);
        Assert.DoesNotContain("passkey-register", page, StringComparison.Ordinal);
        Assert.Contains("microsoft-links", page, StringComparison.Ordinal);

        var antiforgery = KeyValuePair.Create("__RequestVerificationToken", await AppTestHost.FetchAntiforgeryTokenAsync(alice, "/Account"));
        foreach (var (handler, fields) in new (string, KeyValuePair<string, string>[])[]
                 {
                     ("ChangePassword", [KeyValuePair.Create("currentPassword", AppTestHost.Password), KeyValuePair.Create("newPassword", "New-Horse-99"), KeyValuePair.Create("confirmPassword", "New-Horse-99")]),
                     ("PasskeyCreationOptions", []),
                     ("AddPasskey", [KeyValuePair.Create("credentialJson", "{}")]),
                     ("DeletePasskey", [KeyValuePair.Create("credentialId", "AAAA")]),
                 })
        {
            using var response = await AppTestHost.PostFormAsync(alice, $"/Account?handler={handler}", [.. fields, antiforgery]);
            Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{handler} -> {(int)response.StatusCode}");
        }

        // The password did not change.
        await using var scope = _host.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.UserManager<AppUser>>();
        Assert.True(await users.CheckPasswordAsync((await users.FindByNameAsync("alice"))!, AppTestHost.Password));
    }

    [Fact]
    public async Task The_last_microsoft_link_cannot_be_unlinked()
    {
        await _host.SeedWorldAsync();
        await LinkAsync("alice", FakeEntra.AliceOid);
        using var alice = await MicrosoftClientAsync();
        int linkId;
        await using (var db = await _host.System.CreateDbContextAsync()) linkId = (await db.ExternalLogins.SingleAsync()).Id;

        async Task UnlinkAsync(int id)
        {
            var token = await AppTestHost.FetchAntiforgeryTokenAsync(alice, "/Account");
            using var response = await AppTestHost.PostFormAsync(alice, "/Account?handler=UnlinkMicrosoft",
                KeyValuePair.Create("linkId", id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                KeyValuePair.Create("__RequestVerificationToken", token));
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        }

        await UnlinkAsync(linkId);
        await using (var db = await _host.System.CreateDbContextAsync()) Assert.Equal(1, await db.ExternalLogins.CountAsync());
        var (_, page) = await AppTestHost.GetAsync(alice, "/Account");
        Assert.Contains("only way to sign in", page, StringComparison.Ordinal);

        // With a second link, one of them can go.
        await LinkAsync("alice", FakeEntra.StrangerOid);
        await UnlinkAsync(linkId);
        await using (var db = await _host.System.CreateDbContextAsync()) Assert.Equal(1, await db.ExternalLogins.CountAsync());
    }

    [Fact]
    public async Task Api_tokens_and_entra_tokens_still_open_the_mcp_api()
    {
        await _host.SeedWorldAsync();
        await LinkAsync("alice", FakeEntra.AliceOid);
        await using var scope = _host.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ActingUser>().Set(await UserIdAsync("alice"));
        var apiToken = await scope.ServiceProvider.GetRequiredService<ApiTokenService>().CreateAsync("t", null);

        foreach (var token in new[] { apiToken.Token, _idp.AccessToken(FakeEntra.AliceOid) })
        {
            using var client = _host.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            Assert.Equal(HttpStatusCode.OK, (await AppTestHost.GetAsync(client, "/api/mcp/v1/accounts")).Status);
        }
    }
}

public sealed class LocalLoginConfigurationTests
{
    [Fact]
    public async Task Turning_local_login_off_without_microsoft_sign_in_stops_the_start()
    {
        using var host = new AppTestHost((MicrosoftSignIn.LocalLoginSetting, "false"));
        var e = await Assert.ThrowsAnyAsync<Exception>(host.StartAsync);
        Assert.Contains(MicrosoftSignIn.LocalLoginSetting, e.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task With_local_login_on_password_login_and_the_forms_are_as_before()
    {
        using var host = new AppTestHost((MicrosoftSignIn.LocalLoginSetting, "true"));
        await host.SeedWorldAsync();
        using var anonymous = host.CreateClient();
        var (status, login) = await AppTestHost.GetAsync(anonymous, "/Account/Login");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("login-form", login, StringComparison.Ordinal);
        Assert.Contains("passkey-login", login, StringComparison.Ordinal);
        using var alice = await host.LoggedInClientAsync("alice");
        var (_, account) = await AppTestHost.GetAsync(alice, "/Account");
        Assert.Contains("currentPassword", account, StringComparison.Ordinal);
        Assert.Contains("passkey-register", account, StringComparison.Ordinal);
    }
}
