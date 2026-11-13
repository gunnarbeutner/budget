#pragma warning disable CA2000 // test harness: lifetimes are owned by OneDriveWorld
using System.Collections.Concurrent;
using System.IO.Compression;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OpenBudget.Connectors;
using OpenBudget.Connectors.OneDrive;
using OpenBudget.Models;
using OpenBudget.PortfolioPerformance.Proto;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests.Connectors;

/// <summary>Users alice and bob, alice's source "alice-src", the OneDrive service and the cloud-file checks over a fake Microsoft.</summary>
internal sealed class OneDriveWorld : IAsyncDisposable
{
    public const string Alice = "u-alice";
    public const string Bob = "u-bob";
    public static readonly Uri Callback = new("https://budget.example.org/connect/onedrive/callback");

    private readonly string _staging = Path.Combine(Path.GetTempPath(), $"ob-onedrive-{Guid.NewGuid():N}");

    private OneDriveWorld(TestDb t)
    {
        T = t;
        Clock.Set(new DateTime(2026, 11, 10, 9, 0, 0, DateTimeKind.Utc));
        Http = new FakeClientFactory(Fake);
        Loggers = LoggerFactory.Create(b => b.AddProvider(new CapturingProvider(Logged)).SetMinimumLevel(LogLevel.Trace));
        Service = Build(Secrets);
        PortfolioFiles = new UploadedPortfolioFileSource(Path.Combine(_staging, "pp"));
        var importer = new PortfolioPerformanceImportService(t.System, t.Notifier, NullLogger<PortfolioPerformanceImportService>.Instance, PortfolioFiles);
        Consumer = new PortfolioPerformanceCloudFileConsumer(importer, new CloudFileStaging(_staging));
        Coordinator = new CloudFileSyncCoordinator(t.System, Service, Service, [Consumer], Options, Clock, t.Notifier, Loggers.CreateLogger<CloudFileSyncCoordinator>());
    }

    public TestDb T { get; }
    public FakeMicrosoftCloud Fake { get; } = new();
    public TestClock Clock { get; } = new();
    public OneDriveOptions Options { get; } = new() { ClientId = FakeMicrosoftCloud.ClientId, ClientSecret = FakeMicrosoftCloud.ClientSecret };
    public SecretsProtector Secrets { get; } = new(SecretsProtector.GenerateKey());
    public FakeClientFactory Http { get; }
    public ILoggerFactory Loggers { get; }
    public ConcurrentQueue<string> Logged { get; } = new();
    public OneDriveService Service { get; }
    public UploadedPortfolioFileSource PortfolioFiles { get; }
    public PortfolioPerformanceCloudFileConsumer Consumer { get; }
    public CloudFileSyncCoordinator Coordinator { get; }

    /// <summary>Another service instance over the same data (a restarted server: no cached access tokens).</summary>
    public OneDriveService Build(SecretsProtector secrets) =>
        new(T.System, new MicrosoftIdentityClient(Http, Options, Clock), Http, secrets, Options, Clock, T.Notifier, Loggers.CreateLogger<OneDriveService>());

    public static async Task<OneDriveWorld> CreateAsync()
    {
        var t = new TestDb();
        t.AddUser(Alice, "alice");
        t.AddUser(Bob, "bob");
        t.AddIdSpace(Alice, 2);
        t.AddIdSpace(Bob, 3);
        await t.Db.SaveChangesAsync();
        return new OneDriveWorld(t);
    }

    /// <summary>The full sign-in: start, the user consents at the fake Microsoft, the callback completes.</summary>
    public async Task<ConnectOutcome> ConnectAsync(string user, OneDriveService? service = null)
    {
        service ??= Service;
        var url = service.BeginConnect(user, Callback);
        var (code, state, redirect) = Fake.Authorize(url);
        Assert.Equal(Callback.AbsoluteUri, redirect);
        return await service.CompleteConnectAsync(user, state, code, null, CancellationToken.None);
    }

    public async Task<CloudConnection?> ConnectionRowAsync(string user)
    {
        await using var db = await T.System.CreateDbContextAsync();
        return await db.CloudConnections.AsNoTracking().FirstOrDefaultAsync(c => c.OwnerUserId == user);
    }

    public async Task<CloudFileLink> LinkRowAsync(int id)
    {
        await using var db = await T.System.CreateDbContextAsync();
        return await db.CloudFileLinks.AsNoTracking().FirstAsync(f => f.Id == id);
    }

    /// <summary>A PortfolioPerformance file (ZIP with the PPPBV1 protobuf) with one classified security.</summary>
    public static byte[] PortfolioFile(string classification = "Germany")
    {
        var client = new PClient();
        client.Securities.Add(new PSecurity { Uuid = "sec-etf", Name = "Example World ETF", Isin = "XX0000000001" });
        var tax = new PTaxonomy { Id = "tax-regions", Name = "Regions" };
        tax.Classifications.Add(new PTaxonomy.Types.Classification { Id = "root", Name = "Regions" });
        var leaf = new PTaxonomy.Types.Classification { Id = "leaf", ParentId = "root", Name = classification };
        leaf.Assignments.Add(new PTaxonomy.Types.Assignment { InvestmentVehicle = "sec-etf", Weight = 10000 });
        tax.Classifications.Add(leaf);
        client.Taxonomies.Add(tax);

        var payload = client.ToByteArray();
        using var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = archive.CreateEntry("data.portfolio").Open();
            entry.Write("PPPBV1"u8);
            entry.Write(payload);
        }
        return zip.ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        Coordinator.Dispose();
        Loggers.Dispose();
        Http.Dispose();
        await T.DisposeAsync();
        try { Directory.Delete(_staging, recursive: true); } catch (DirectoryNotFoundException) { /* never staged */ }
    }

    internal sealed class FakeClientFactory(HttpMessageHandler handler) : IHttpClientFactory, IDisposable
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);

        public void Dispose() => handler.Dispose();
    }

    private sealed class CapturingProvider(ConcurrentQueue<string> sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Capture(sink);

        public void Dispose() { }

        private sealed class Capture(ConcurrentQueue<string> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                sink.Enqueue(formatter(state, exception) + " " + exception);
        }
    }
}

public sealed class OneDriveTests
{
    private const string Alice = OneDriveWorld.Alice;
    private const string Bob = OneDriveWorld.Bob;

    // ---- Connecting ----

    [Fact]
    public async Task Connect_redeems_the_code_with_pkce_and_stores_the_refresh_token_encrypted()
    {
        await using var w = await OneDriveWorld.CreateAsync();
        w.Fake.AccountName = "alice@example.org";

        Assert.Equal(ConnectOutcome.Connected, await w.ConnectAsync(Alice));

        var view = await w.Service.GetConnectionAsync(Alice);
        Assert.NotNull(view);
        Assert.Equal("alice@example.org", view.AccountName);
        Assert.False(view.NeedsReconnect);
        var row = await w.ConnectionRowAsync(Alice);
        Assert.StartsWith("v1:", row!.EncryptedRefreshToken, StringComparison.Ordinal);
        Assert.DoesNotContain(w.Fake.IssuedSecrets, s => row.EncryptedRefreshToken!.Contains(s, StringComparison.Ordinal));
        Assert.Null(await w.Service.GetConnectionAsync(Bob));
    }

    [Fact]
    public void Authorize_url_asks_for_a_code_with_s256_challenge_state_and_offline_access()
    {
        var verifier = MicrosoftIdentityClient.NewCodeVerifier();
        Assert.InRange(verifier.Length, 43, 128);
        var options = new OneDriveOptions { ClientId = "cid", ClientSecret = "x", Tenant = "contoso.example" };
        using var http = new OneDriveWorld.FakeClientFactory(new FakeMicrosoftCloud());
        var url = new MicrosoftIdentityClient(http, options, TimeProvider.System)
            .BuildAuthorizeUrl("the-state", MicrosoftIdentityClient.CodeChallenge(verifier), OneDriveWorld.Callback);

        Assert.StartsWith("https://login.microsoftonline.com/contoso.example/oauth2/v2.0/authorize?", url.AbsoluteUri, StringComparison.Ordinal);
        var q = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(url.Query);
        Assert.Equal("cid", q["client_id"]);
        Assert.Equal("code", q["response_type"]);
        Assert.Equal("S256", q["code_challenge_method"]);
        Assert.Equal(MicrosoftIdentityClient.CodeChallenge(verifier), q["code_challenge"]);
        Assert.Equal("the-state", q["state"]);
        Assert.Equal(OneDriveWorld.Callback.AbsoluteUri, q["redirect_uri"]);
        Assert.Contains("offline_access", q["scope"].ToString(), StringComparison.Ordinal);
        Assert.Contains("Files.Read", q["scope"].ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("x", q.Keys);
    }

    [Fact]
    public async Task Callback_rejects_unknown_reused_expired_and_foreign_state()
    {
        await using var w = await OneDriveWorld.CreateAsync();

        // Unknown state.
        Assert.Equal(ConnectOutcome.InvalidState, await w.Service.CompleteConnectAsync(Alice, "made-up", "code", null, default));

        // Another user's state: rejected (and spent).
        var (code, state, _) = w.Fake.Authorize(w.Service.BeginConnect(Alice, OneDriveWorld.Callback));
        Assert.Equal(ConnectOutcome.InvalidState, await w.Service.CompleteConnectAsync(Bob, state, code, null, default));
        Assert.Equal(ConnectOutcome.InvalidState, await w.Service.CompleteConnectAsync(Alice, state, code, null, default));

        // Expired state.
        (code, state, _) = w.Fake.Authorize(w.Service.BeginConnect(Alice, OneDriveWorld.Callback));
        w.Clock.Offset += TimeSpan.FromMinutes(11);
        Assert.Equal(ConnectOutcome.InvalidState, await w.Service.CompleteConnectAsync(Alice, state, code, null, default));

        // Reused state after success.
        (code, state, _) = w.Fake.Authorize(w.Service.BeginConnect(Alice, OneDriveWorld.Callback));
        Assert.Equal(ConnectOutcome.Connected, await w.Service.CompleteConnectAsync(Alice, state, code, null, default));
        Assert.Equal(ConnectOutcome.InvalidState, await w.Service.CompleteConnectAsync(Alice, state, code, null, default));
        Assert.Null(await w.ConnectionRowAsync(Bob));
    }

    [Fact]
    public async Task Callback_with_an_error_or_a_rejected_code_does_not_connect()
    {
        await using var w = await OneDriveWorld.CreateAsync();

        var (_, state, _) = w.Fake.Authorize(w.Service.BeginConnect(Alice, OneDriveWorld.Callback));
        Assert.Equal(ConnectOutcome.Denied, await w.Service.CompleteConnectAsync(Alice, state, null, "access_denied", default));

        (_, state, _) = w.Fake.Authorize(w.Service.BeginConnect(Alice, OneDriveWorld.Callback));
        Assert.Equal(ConnectOutcome.Failed, await w.Service.CompleteConnectAsync(Alice, state, "not-a-code", null, default));
        Assert.Null(await w.ConnectionRowAsync(Alice));
    }

    [Fact]
    public async Task Token_endpoint_refuses_a_code_redeemed_with_the_wrong_verifier()
    {
        await using var w = await OneDriveWorld.CreateAsync();
        var verifier = MicrosoftIdentityClient.NewCodeVerifier();
        var identity = new MicrosoftIdentityClient(w.Http, w.Options, w.Clock);
        var (code, _, _) = w.Fake.Authorize(identity.BuildAuthorizeUrl("s", MicrosoftIdentityClient.CodeChallenge(verifier), OneDriveWorld.Callback));

        await Assert.ThrowsAsync<CloudReconnectRequiredException>(() =>
            identity.RedeemCodeAsync(code, MicrosoftIdentityClient.NewCodeVerifier(), OneDriveWorld.Callback, default));
    }

    [Fact]
    public async Task Wrong_client_secret_is_reported_as_a_configuration_problem()
    {
        await using var w = await OneDriveWorld.CreateAsync();
        var identity = new MicrosoftIdentityClient(w.Http, new OneDriveOptions { ClientId = FakeMicrosoftCloud.ClientId, ClientSecret = "wrong" }, w.Clock);
        var ex = await Assert.ThrowsAsync<CloudException>(() => identity.RefreshAsync("anything", default));
        Assert.Contains("client id or secret", ex.Message, StringComparison.Ordinal);
    }

    // ---- Tokens ----

    [Fact]
    public async Task An_expired_access_token_is_refreshed_and_the_rotated_refresh_token_saved()
    {
        await using var w = await OneDriveWorld.CreateAsync();
        await w.ConnectAsync(Alice);
        var before = (await w.ConnectionRowAsync(Alice))!.EncryptedRefreshToken;

        w.Fake.RevokeAccessTokens();
        await w.Service.ListAsync(Alice, CloudFolder.MyFiles);

        Assert.Equal(1, w.Fake.TokenRefreshes);
        var after = (await w.ConnectionRowAsync(Alice))!.EncryptedRefreshToken;
        Assert.NotEqual(before, after);

        // A restarted server (no access token in memory) refreshes with the rotated token; the old one is spent.
        var restarted = w.Build(w.Secrets);
        await restarted.ListAsync(Alice, CloudFolder.MyFiles);
        Assert.Equal(2, w.Fake.TokenRefreshes);
        Assert.False((await w.ConnectionRowAsync(Alice))!.NeedsReconnect);
    }

    [Fact]
    public async Task A_rejected_refresh_token_asks_for_reconnect_and_reconnecting_keeps_linked_files()
    {
        await using var w = await OneDriveWorld.CreateAsync();
        w.Fake.Add(new() { Drive = FakeMicrosoftCloud.MyDrive, Id = "f1", ParentId = "root", Name = "Depot.portfolio", Content = OneDriveWorld.PortfolioFile() });
        await w.ConnectAsync(Alice);
        var linkId = await w.Service.LinkFileAsync(Alice, FakeMicrosoftCloud.MyDrive, "f1", "My files/Depot.portfolio");

        w.Fake.RejectRefresh = true;
        w.Fake.RevokeAccessTokens();
        await Assert.ThrowsAsync<CloudReconnectRequiredException>(() => w.Service.ListAsync(Alice, CloudFolder.MyFiles));
        var view = await w.Service.GetConnectionAsync(Alice);
        Assert.True(view!.NeedsReconnect);
        Assert.Contains("Reconnect", view.LastError, StringComparison.Ordinal);

        // While a reconnect is needed nothing is tried again.
        var refreshes = w.Fake.TokenRefreshes;
        await w.Coordinator.CheckNowAsync(Alice);
        Assert.Equal(refreshes, w.Fake.TokenRefreshes);

        w.Fake.RejectRefresh = false;
        Assert.Equal(ConnectOutcome.Connected, await w.ConnectAsync(Alice));
        Assert.False((await w.Service.GetConnectionAsync(Alice))!.NeedsReconnect);
        Assert.Equal(linkId, Assert.Single(await w.Service.GetLinkedFilesAsync(Alice)).Id);
    }

    [Fact]
    public async Task A_stored_token_under_another_secrets_key_asks_for_reconnect()
    {
        await using var w = await OneDriveWorld.CreateAsync();
        await w.ConnectAsync(Alice);
        var other = w.Build(new SecretsProtector(SecretsProtector.GenerateKey()));

        await Assert.ThrowsAsync<CloudReconnectRequiredException>(() => other.ListAsync(Alice, CloudFolder.MyFiles));
        Assert.True((await w.ConnectionRowAsync(Alice))!.NeedsReconnect);
    }

    // ---- Picker ----

    [Fact]
    public async Task Picker_lists_folders_and_portfolio_files_only_and_navigates_into_folders_and_shared_items()
    {
        await using var w = await OneDriveWorld.CreateAsync();
        w.Fake.Add(new() { Drive = FakeMicrosoftCloud.MyDrive, Id = "fin", ParentId = "root", Name = "Finance", IsFolder = true });
        w.Fake.Add(new() { Drive = FakeMicrosoftCloud.MyDrive, Id = "doc", ParentId = "root", Name = "Notes.docx" });
        w.Fake.Add(new() { Drive = FakeMicrosoftCloud.MyDrive, Id = "pp", ParentId = "fin", Name = "Depot.PORTFOLIO" });
        w.Fake.Add(new() { Drive = FakeMicrosoftCloud.MyDrive, Id = "bak", ParentId = "fin", Name = "Depot.portfolio.bak" });
        w.Fake.Add(new() { Drive = FakeMicrosoftCloud.SharedDrive, Id = "sh", Name = "Family.portfolio", SharedWithMe = true, SharedBy = "Example Partner" });
        w.Fake.Add(new() { Drive = FakeMicrosoftCloud.SharedDrive, Id = "shf", Name = "Shared folder", IsFolder = true, SharedWithMe = true, SharedBy = "Example Partner" });
        w.Fake.Add(new() { Drive = FakeMicrosoftCloud.SharedDrive, Id = "in", ParentId = "shf", Name = "Inner.portfolio" });
        await w.ConnectAsync(Alice);

        var root = await w.Service.ListAsync(Alice, CloudFolder.MyFiles);
        Assert.Equal(["Finance"], root.Select(i => i.Name));
        Assert.True(root[0].IsFolder);

        var finance = await w.Service.ListAsync(Alice, new CloudFolder(root[0].DriveId, root[0].ItemId));
        var file = Assert.Single(finance);
        Assert.Equal(("Depot.PORTFOLIO", FakeMicrosoftCloud.MyDrive, "pp"), (file.Name, file.DriveId, file.ItemId));

        var shared = await w.Service.ListAsync(Alice, CloudFolder.Shared);
        Assert.Equal(["Shared folder", "Family.portfolio"], shared.Select(i => i.Name));
        Assert.All(shared, i => Assert.Equal(FakeMicrosoftCloud.SharedDrive, i.DriveId));
        Assert.Equal("Example Partner", shared[1].SharedBy);
        var inner = await w.Service.ListAsync(Alice, new CloudFolder(shared[0].DriveId, shared[0].ItemId));
        Assert.Equal(["Inner.portfolio"], inner.Select(i => i.Name));
    }

    [Fact]
    public async Task Picker_needs_a_connection()
    {
        await using var w = await OneDriveWorld.CreateAsync();
        await Assert.ThrowsAsync<CloudException>(() => w.Service.ListAsync(Alice, CloudFolder.MyFiles));
    }

    // ---- Reading and change detection ----

    [Fact]
    public async Task Linked_file_is_imported_for_its_owner_and_unchanged_versions_are_not_downloaded()
    {
        await using var w = await OneDriveWorld.CreateAsync();
        w.Fake.Add(new() { Drive = FakeMicrosoftCloud.MyDrive, Id = "f1", ParentId = "root", Name = "Depot.portfolio", Content = OneDriveWorld.PortfolioFile("Germany") });
        await w.ConnectAsync(Alice);
        var linkId = await w.Service.LinkFileAsync(Alice, FakeMicrosoftCloud.MyDrive, "f1", "My files/Depot.portfolio");

        await w.Coordinator.CheckNowAsync(Alice);
        Assert.Equal(1, w.Fake.Downloads);
        var link = await w.LinkRowAsync(linkId);
        Assert.Null(link.LastError);
        Assert.NotNull(link.LastImportedAt);
        Assert.Equal("\"c:{f1},1\"", link.LastCTag);
        Assert.Equal("Germany", (await w.T.Db.SecurityTaxonomies.AsNoTracking().SingleAsync(t => t.OwnerUserId == Alice)).ClassificationName);
        // Like an upload, the file becomes the source's current file for the IPortfolioFileSource seam.
        await using (var stored = await w.PortfolioFiles.OpenLatestAsync(Alice))
            Assert.NotNull(stored);

        // Unchanged: checked, not downloaded.
        await w.Coordinator.CheckNowAsync(Alice);
        Assert.Equal(1, w.Fake.Downloads);

        // A new version is downloaded and imported.
        w.Fake.Update(FakeMicrosoftCloud.MyDrive, "f1", OneDriveWorld.PortfolioFile("France"));
        await w.Coordinator.CheckNowAsync(Alice);
        Assert.Equal(2, w.Fake.Downloads);
        Assert.Equal("France", (await w.T.Db.SecurityTaxonomies.AsNoTracking().SingleAsync(t => t.OwnerUserId == Alice)).ClassificationName);
    }

    [Fact]
    public async Task Reader_returns_bytes_and_tags_and_skips_a_known_content_tag()
    {
        await using var w = await OneDriveWorld.CreateAsync();
        w.Fake.Add(new() { Drive = FakeMicrosoftCloud.MyDrive, Id = "f1", ParentId = "root", Name = "Depot.portfolio", Content = [1, 2, 3] });
        await w.ConnectAsync(Alice);
        var linkId = await w.Service.LinkFileAsync(Alice, FakeMicrosoftCloud.MyDrive, "f1", "");
        var reader = w.Service;

        var first = await reader.ReadAsync(Alice, linkId, null, default);
        Assert.Equal([1, 2, 3], first.Content);
        Assert.Equal("Depot.portfolio", first.Metadata.Name);
        Assert.NotNull(first.Metadata.ETag);
        Assert.NotNull(first.Metadata.LastModifiedUtc);

        var again = await reader.ReadAsync(Alice, linkId, first.Metadata.ContentTag, default);
        Assert.False(again.Changed);
        Assert.Equal(1, w.Fake.Downloads);
        Assert.Equal("Depot.portfolio", (await w.Service.GetLinkedFilesAsync(Alice)).Single().DisplayPath);
    }

    [Fact]
    public async Task Errors_are_recorded_on_the_link_and_a_failed_version_is_retried()
    {
        await using var w = await OneDriveWorld.CreateAsync();
        w.Fake.Add(new() { Drive = FakeMicrosoftCloud.MyDrive, Id = "f1", ParentId = "root", Name = "Depot.portfolio", Content = "not a zip"u8.ToArray() });
        await w.ConnectAsync(Alice);
        var linkId = await w.Service.LinkFileAsync(Alice, FakeMicrosoftCloud.MyDrive, "f1", "Depot.portfolio");

        await w.Coordinator.CheckNowAsync(Alice);
        Assert.Equal(PortfolioPerformanceUpload.NotAPortfolioFile, (await w.LinkRowAsync(linkId)).LastError);

        // The same (still broken) version is downloaded again on the next check.
        await w.Coordinator.CheckNowAsync(Alice);
        Assert.Equal(2, w.Fake.Downloads);

        w.Fake.Remove(FakeMicrosoftCloud.MyDrive, "f1");
        await w.Coordinator.CheckNowAsync(Alice);
        Assert.Contains("no longer exists", (await w.LinkRowAsync(linkId)).LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Page_load_checks_run_at_most_once_per_interval()
    {
        await using var w = await OneDriveWorld.CreateAsync();
        w.Fake.Add(new() { Drive = FakeMicrosoftCloud.MyDrive, Id = "f1", ParentId = "root", Name = "Depot.portfolio", Content = OneDriveWorld.PortfolioFile() });
        await w.ConnectAsync(Alice);
        await w.Service.LinkFileAsync(Alice, FakeMicrosoftCloud.MyDrive, "f1", "Depot.portfolio");

        Assert.True(w.Coordinator.StartDue(Alice));
        await w.Coordinator.WhenIdleAsync(Alice);
        Assert.False(w.Coordinator.StartDue(Alice));
        Assert.True(w.Coordinator.StartDue(Bob)); // per user; Bob has nothing linked
        await w.Coordinator.WhenIdleAsync(Bob);

        var requests = w.Fake.Requests.Count;
        w.Clock.Offset += w.Options.CheckInterval;
        Assert.True(w.Coordinator.StartDue(Alice));
        await w.Coordinator.WhenIdleAsync(Alice);
        Assert.True(w.Fake.Requests.Count > requests);
        Assert.Equal(1, w.Fake.Downloads);
    }

    // ---- Access scoping and disconnect ----

    [Fact]
    public async Task Users_reach_only_their_own_connection_and_links()
    {
        await using var w = await OneDriveWorld.CreateAsync();
        w.Fake.Add(new() { Drive = FakeMicrosoftCloud.MyDrive, Id = "f1", ParentId = "root", Name = "Depot.portfolio", Content = [1] });
        await w.ConnectAsync(Alice);

        var linkId = await w.Service.LinkFileAsync(Alice, FakeMicrosoftCloud.MyDrive, "f1", "x");

        Assert.Empty(await w.Service.GetLinkedFilesAsync(Bob));
        Assert.False(await w.Service.UnlinkFileAsync(Bob, linkId));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => w.Service.ReadAsync(Bob, linkId, null, default));
        await Assert.ThrowsAsync<CloudException>(() => w.Service.LinkFileAsync(Bob, FakeMicrosoftCloud.MyDrive, "f1", "x"));

        // The user-scoped context of another user sees neither row and cannot write them.
        await using var bobDb = await w.T.ForUser(Bob).Factory.CreateDbContextAsync();
        Assert.Empty(await bobDb.CloudConnections.ToListAsync());
        Assert.Empty(await bobDb.CloudFileLinks.ToListAsync());
        bobDb.CloudConnections.Add(new CloudConnection { OwnerUserId = Alice });
        await Assert.ThrowsAsync<AccessDeniedException>(() => bobDb.SaveChangesAsync());
    }

    [Fact]
    public async Task Disconnect_forgets_the_connection_and_its_files()
    {
        await using var w = await OneDriveWorld.CreateAsync();
        w.Fake.Add(new() { Drive = FakeMicrosoftCloud.MyDrive, Id = "f1", ParentId = "root", Name = "Depot.portfolio", Content = [1] });
        await w.ConnectAsync(Alice);
        await w.ConnectAsync(Bob);
        await w.Service.LinkFileAsync(Alice, FakeMicrosoftCloud.MyDrive, "f1", "x");

        await w.Service.DisconnectAsync(Alice);

        Assert.Null(await w.Service.GetConnectionAsync(Alice));
        Assert.Empty(await w.Service.GetLinkedFilesAsync(Alice));
        Assert.NotNull(await w.Service.GetConnectionAsync(Bob));
        await Assert.ThrowsAsync<CloudException>(() => w.Service.ListAsync(Alice, CloudFolder.MyFiles));
    }

    [Fact]
    public async Task No_token_code_or_client_secret_reaches_the_log()
    {
        await using var w = await OneDriveWorld.CreateAsync();
        w.Fake.Add(new() { Drive = FakeMicrosoftCloud.MyDrive, Id = "f1", ParentId = "root", Name = "Depot.portfolio", Content = "broken"u8.ToArray() });
        await w.ConnectAsync(Alice);
        await w.Service.LinkFileAsync(Alice, FakeMicrosoftCloud.MyDrive, "f1", "x");
        w.Fake.RevokeAccessTokens();
        await w.Coordinator.CheckNowAsync(Alice);
        var (code, state, _) = w.Fake.Authorize(w.Service.BeginConnect(Alice, OneDriveWorld.Callback));
        await w.Service.CompleteConnectAsync(Alice, state, code + "x", null, default);
        w.Fake.RejectRefresh = true;
        w.Fake.RevokeAccessTokens();
        await w.Coordinator.CheckNowAsync(Alice);

        Assert.NotEmpty(w.Logged);
        var secrets = w.Fake.IssuedSecrets.Append(FakeMicrosoftCloud.ClientSecret).ToList();
        Assert.All(w.Logged, line => Assert.DoesNotContain(secrets, s => line.Contains(s, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Feature_is_disabled_without_client_credentials_or_secrets_key()
    {
        await using var w = await OneDriveWorld.CreateAsync();
        var noApp = new OneDriveService(w.T.System, new MicrosoftIdentityClient(w.Http, new OneDriveOptions(), w.Clock), w.Http, w.Secrets,
            new OneDriveOptions(), w.Clock, w.T.Notifier, NullLogger<OneDriveService>.Instance);
        Assert.False(noApp.IsEnabled);
        Assert.Throws<InvalidOperationException>(() => noApp.BeginConnect(Alice, OneDriveWorld.Callback));
        Assert.False(w.Build(new SecretsProtector((string?)null)).IsEnabled);
        Assert.True(w.Service.IsEnabled);
    }

    [Theory]
    [InlineData("Depot.portfolio", true)]
    [InlineData("Depot.PORTFOLIO", true)]
    [InlineData("Depot.autosave.portfolio", false)]
    [InlineData("Depot.backup.portfolio", false)]
    [InlineData("Depot.backup-after-open.portfolio", false)]
    [InlineData("Depot.backup-on-save.portfolio", false)]
    [InlineData("Depot backup.portfolio", true)]
    [InlineData("Notes.docx", false)]
    public void The_picker_offers_portfolio_files_but_not_the_copies_Portfolio_Performance_writes(string name, bool offered) =>
        Assert.Equal(offered, OneDriveService.IsPortfolioFile(name));
}
