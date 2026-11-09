using System.Net;
using Microsoft.EntityFrameworkCore;
using OpenBudget.Compat;
using OpenBudget.Models;
using Xunit;

namespace OpenBudget.Tests;

/// <summary>
/// The upload API stays token-authenticated: the legacy global token keeps working for every source
/// (each upload lands in the source named in its URL, as before), and each source's own token works
/// for that source only. None of it needs — or grants — a login.
/// </summary>
[Collection("ProcessEnvironment")]
public sealed class UploadTokenTests : IDisposable
{
    private readonly AppTestHost _host = new();

    public void Dispose() => _host.Dispose();

    private async Task<HttpStatusCode> PostAsync(string path, string? token)
    {
        using var client = _host.CreateClient();
        using var request = AppTestHost.Upload(path, AppTestHost.BuildSnapshot(), token);
        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    private async Task<(int Accounts, int Transactions, DateTime? LastUpload)> CountsAsync(int sourceId)
    {
        await using var db = await _host.System.CreateDbContextAsync();
        var source = await db.ImportSources.SingleAsync(s => s.Id == sourceId);
        return (await db.Accounts.CountAsync(a => a.SourceId == sourceId),
                await db.Transactions.CountAsync(t => t.SourceId == sourceId),
                source.LastUploadAt);
    }

    [Fact]
    public async Task Legacy_token_uploads_land_in_the_source_named_in_the_url_for_both_sources()
    {
        await _host.SeedWorldAsync();

        // alice's source first: bob's is untouched.
        Assert.Equal(HttpStatusCode.OK, await PostAsync("/api/moneymoney-db/alice", AppTestHost.LegacyToken));
        var alice = await CountsAsync(AccessWorld.SourceAlice);
        var bob = await CountsAsync(AccessWorld.SourceBob);
        Assert.Equal((1, 1), (alice.Accounts, alice.Transactions));
        Assert.NotNull(alice.LastUpload);
        Assert.Equal((1, 1), (bob.Accounts, bob.Transactions));   // seeded: one account, one transaction
        Assert.Null(bob.LastUpload);

        // Then bob's, through the new route.
        Assert.Equal(HttpStatusCode.OK, await PostAsync("/api/import/moneymoney/bob", AppTestHost.LegacyToken));
        Assert.NotNull((await CountsAsync(AccessWorld.SourceBob)).LastUpload);

        // Both existing sources were reused, not duplicated.
        await using var db = await _host.System.CreateDbContextAsync();
        Assert.Equal(new[] { "alice", "bob", "default" }, (await db.ImportSources.Select(s => s.Name).ToListAsync()).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task Legacy_token_upload_to_the_bob_source_replaces_only_lisas_data()
    {
        await _host.SeedWorldAsync();
        var aliceBefore = await CountsAsync(AccessWorld.SourceAlice);

        Assert.Equal(HttpStatusCode.OK, await PostAsync("/api/moneymoney-db/bob", AppTestHost.LegacyToken));

        Assert.Equal(aliceBefore.Accounts, (await CountsAsync(AccessWorld.SourceAlice)).Accounts);   // 3 accounts, untouched
        Assert.Equal(3, aliceBefore.Accounts);
        Assert.Equal(1, (await CountsAsync(AccessWorld.SourceBob)).Accounts);
    }

    [Fact]
    public async Task A_source_token_works_for_its_own_source_only()
    {
        await _host.SeedWorldAsync();

        Assert.Equal(HttpStatusCode.OK, await PostAsync("/api/moneymoney-db/bob", "tok-bob"));
        Assert.Equal(HttpStatusCode.OK, await PostAsync("/api/import/moneymoney/alice", "tok-alice"));
        Assert.Equal(HttpStatusCode.Unauthorized, await PostAsync("/api/moneymoney-db/alice", "tok-bob"));
        Assert.Equal(HttpStatusCode.Unauthorized, await PostAsync("/api/import/moneymoney/bob", "tok-alice"));
        Assert.Equal(HttpStatusCode.Unauthorized, await PostAsync("/api/moneymoney-db/bob", "wrong"));
        Assert.Equal(HttpStatusCode.Unauthorized, await PostAsync("/api/moneymoney-db/bob", null));
        // A source token can't create a new source (the source has to exist to have a token).
        Assert.Equal(HttpStatusCode.Unauthorized, await PostAsync("/api/moneymoney-db/brand-new", "tok-bob"));
        await using var db = await _host.System.CreateDbContextAsync();
        Assert.DoesNotContain(await db.ImportSources.ToListAsync(), s => s.Name == "brand-new");
    }

    [Fact]
    public async Task Source_tokens_keep_working_when_the_legacy_token_is_not_configured()
    {
        await _host.SeedWorldAsync();
        Environment.SetEnvironmentVariable(LegacyCompat.LegacyUploadTokenVariable, null);

        Assert.Equal(HttpStatusCode.OK, await PostAsync("/api/moneymoney-db/bob", "tok-bob"));
        Assert.Equal(HttpStatusCode.Unauthorized, await PostAsync("/api/moneymoney-db/bob", AppTestHost.LegacyToken));
        Assert.Equal(HttpStatusCode.Unauthorized, await PostAsync("/api/moneymoney-db/bob", null));
    }

    [Fact]
    public async Task Without_any_token_configured_the_endpoint_stays_disabled_as_before()
    {
        await _host.StartAsync();
        Environment.SetEnvironmentVariable(LegacyCompat.LegacyUploadTokenVariable, null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, await PostAsync("/api/moneymoney-db/whatever", "anything"));
    }

    [Fact]
    public async Task A_rotated_source_token_invalidates_the_old_one()
    {
        await _host.SeedWorldAsync();
        await using (var db = await _host.System.CreateDbContextAsync())
        {
            (await db.ImportSources.SingleAsync(s => s.Id == AccessWorld.SourceBob)).UploadToken = "tok-bob-2";
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.Unauthorized, await PostAsync("/api/moneymoney-db/bob", "tok-bob"));
        Assert.Equal(HttpStatusCode.OK, await PostAsync("/api/moneymoney-db/bob", "tok-bob-2"));
    }

    [Fact]
    public async Task Source_deletion_over_the_api_honours_the_same_tokens()
    {
        await _host.SeedWorldAsync();
        await using (var db = await _host.System.CreateDbContextAsync())
        {
            db.ImportSources.Add(new ImportSource { Id = 20, Name = "spare", UploadToken = "tok-spare" });
            await db.SaveChangesAsync();
        }
        using var client = _host.CreateClient();

        async Task<HttpStatusCode> Delete(string name, string? token)
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, new Uri($"/api/sources/{name}", UriKind.Relative));
            if (token != null) request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            using var response = await client.SendAsync(request);
            return response.StatusCode;
        }

        Assert.Equal(HttpStatusCode.Unauthorized, await Delete("spare", "tok-bob"));
        Assert.Equal(HttpStatusCode.Conflict, await Delete("bob", "tok-bob"));   // still has a project
        Assert.Equal(HttpStatusCode.OK, await Delete("spare", "tok-spare"));
    }

    [Fact]
    public async Task An_upload_token_does_not_log_anyone_in()
    {
        await _host.SeedWorldAsync();
        using var client = _host.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/", UriKind.Relative));
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", AppTestHost.LegacyToken);

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }
}
