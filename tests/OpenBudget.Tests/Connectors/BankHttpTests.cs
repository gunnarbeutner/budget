using System.Net;
using System.Text;
using OpenBudget.Connectors;
using OpenBudget.Models;
using Xunit;

namespace OpenBudget.Tests.Connectors;

/// <summary>The real app over HTTP: the bank-login page and the statement download URL.</summary>
[Collection("ProcessEnvironment")]
public sealed class BankHttpTests : IDisposable
{
    private readonly string _statements = Path.Combine(Path.GetTempPath(), $"ob-http-stmts-{Guid.NewGuid():N}");
    private readonly string _key = SecretsProtector.GenerateKey();
    private readonly AppTestHost _host;

    public BankHttpTests() => _host = new AppTestHost(
        (SecretsProtector.KeyVariable, _key),
        (BankConnectorsRegistration.StatementsDirVariable, _statements));

    public void Dispose()
    {
        _host.Dispose();
        try { Directory.Delete(_statements, recursive: true); } catch { /* best-effort */ }
    }

    private async Task<long> AddStatementAsync(long? accountId, int? sourceId, string owner, string name)
    {
        var relative = Path.Combine("x", $"{Guid.NewGuid():N}.pdf");
        var full = Path.Combine(_statements, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await File.WriteAllBytesAsync(full, Encoding.ASCII.GetBytes("%PDF-1.4\n%%EOF\n"));
        await using var db = await _host.System.CreateDbContextAsync();
        var row = new BankStatement { BankLoginId = 1, OwnerUserId = owner, AccountId = accountId, SourceId = sourceId, Identifier = name, Name = name, FilePath = relative.Replace('\\', '/'), Date = DateTime.UtcNow };
        db.BankStatements.Add(row);
        await db.SaveChangesAsync();
        return row.Id;
    }

    [Fact]
    public async Task Statement_downloads_follow_account_visibility()
    {
        await _host.SeedWorldAsync(ProjectRole.Viewer);
        var alice = await DbUserIdAsync("alice");
        var shared = await AddStatementAsync(AccessWorld.AccountWp, AccessWorld.SourceAlice, alice, "shared-account");
        var priv = await AddStatementAsync(AccessWorld.AccountPrivate, AccessWorld.SourceAlice, alice, "private-account");
        var loose = await AddStatementAsync(null, null, alice, "tax-certificate");

        using var aliceClient = await _host.LoggedInClientAsync("alice");
        foreach (var id in new[] { shared, priv, loose })
        {
            using var response = await aliceClient.GetAsync(new Uri($"/statements/{id}", UriKind.Relative));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        }
        using (var download = await aliceClient.GetAsync(new Uri($"/statements/{shared}?download=true", UriKind.Relative)))
            Assert.Contains("attachment", download.Content.Headers.ContentDisposition?.DispositionType, StringComparison.Ordinal);

        using var bob = await _host.LoggedInClientAsync("bob");
        using (var ok = await bob.GetAsync(new Uri($"/statements/{shared}", UriKind.Relative)))
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode); // viewer of the project the account belongs to
        foreach (var id in new[] { priv, loose, 99999L })
        {
            using var response = await bob.GetAsync(new Uri($"/statements/{id}", UriKind.Relative));
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        using var anonymous = _host.CreateClient();
        using var denied = await anonymous.GetAsync(new Uri($"/statements/{shared}", UriKind.Relative));
        Assert.NotEqual(HttpStatusCode.OK, denied.StatusCode);
    }

    private async Task<string> DbUserIdAsync(string name)
    {
        await using var db = await _host.System.CreateDbContextAsync();
        return db.Users.Single(u => u.UserName == name).Id;
    }

    [Fact]
    public async Task Bank_login_page_shows_only_the_signed_in_users_logins()
    {
        await _host.SeedWorldAsync();
        var alice = await DbUserIdAsync("alice");
        await using (var db = await _host.System.CreateDbContextAsync())
        {
            db.BankLogins.Add(new BankLogin { OwnerUserId = alice, ConnectorType = "fake", DisplayName = "AliceOnlyLogin" });
            await db.SaveChangesAsync();
        }

        using var aliceClient = await _host.LoggedInClientAsync("alice");
        var (aliceStatus, aliceBody) = await AppTestHost.GetAsync(aliceClient, "/banklogins");
        Assert.Equal(HttpStatusCode.OK, aliceStatus);
        Assert.Contains("AliceOnlyLogin", aliceBody, StringComparison.Ordinal);
        Assert.Contains("Add a bank login", aliceBody, StringComparison.Ordinal);

        using var bob = await _host.LoggedInClientAsync("bob");
        var (bobStatus, bobBody) = await AppTestHost.GetAsync(bob, "/banklogins");
        Assert.Equal(HttpStatusCode.OK, bobStatus);
        Assert.DoesNotContain("AliceOnlyLogin", bobBody, StringComparison.Ordinal);
        Assert.Contains("No bank logins yet.", bobBody, StringComparison.Ordinal);
    }
}

/// <summary>Without a secrets key the app runs and the page explains why bank logins are off.</summary>
[Collection("ProcessEnvironment")]
public sealed class BankHttpWithoutKeyTests : IDisposable
{
    private readonly AppTestHost _host = new((SecretsProtector.KeyVariable, ""));

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Page_explains_that_the_secrets_key_is_not_configured()
    {
        await _host.AddUserAsync("alice");
        using var client = await _host.LoggedInClientAsync("alice");
        var (status, body) = await AppTestHost.GetAsync(client, "/banklogins");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("OPENBUDGET_SECRETS_KEY", body, StringComparison.Ordinal);
        Assert.Contains("Bank logins are disabled", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Add a bank login", body, StringComparison.Ordinal);

        // The rest of the app is unaffected.
        var (home, _) = await AppTestHost.GetAsync(client, "/sources");
        Assert.Equal(HttpStatusCode.OK, home);
    }
}
