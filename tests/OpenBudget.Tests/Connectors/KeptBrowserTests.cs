#pragma warning disable CA2000 // test objects live for the test
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OpenBudget.Browser;
using OpenBudget.Connectors;
using OpenBudget.Connectors.Sync;
using Xunit;

namespace OpenBudget.Tests.Connectors;

/// <summary>Browsers kept after a web login (<see cref="WebLoginSpec.KeepBrowser"/>): hand-over, requests and lifetime, against a fake browser.</summary>
public sealed class KeptBrowserTests
{
    private const string Domain = "bank.example";
    private static readonly Uri Start = new("https://bank.example/login");
    private static readonly Uri Home = new("https://bank.example/home");

    private static WebLoginSpec Spec(bool keep = true, Uri? resume = null) => new(Start, [Domain], Home) { KeepBrowser = keep, ResumeUrl = resume };

    private static CookieState Sid(string value = "s-1") => new("sid", value, Domain, "/", DateTime.UtcNow.AddDays(1), true, true, "Lax");

    private static Task SignsIn(FakeBrowserSession s)
    {
        s.SetCookie(Sid());
        s.Url = Home;
        return Task.CompletedTask;
    }

    private static WebLoginService Service(FakeBrowserHost host, TimeSpan? lifetime = null) => new(host,
        new WebLoginOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(10),
            SettleDelay = TimeSpan.Zero,
            FormTimeout = TimeSpan.FromMilliseconds(300),
            KeptBrowserLifetime = lifetime ?? TimeSpan.FromMinutes(5),
        },
        TimeProvider.System, NullLoggerFactory.Instance);

    [Fact]
    public async Task A_kept_browser_outlives_the_login_runs_requests_and_closes_when_disposed()
    {
        var host = new FakeBrowserHost { User = SignsIn };
        var service = Service(host);
        KeptBrowser kept;
        await using (var session = await service.StartAsync(BankWorld.Alice, Spec(), [], CancellationToken.None))
        {
            Assert.True((await session.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Success);
            Assert.Null(service.Find(session.Id, BankWorld.Alice)); // nobody can watch it any more
            kept = session.TakeBrowser()!;
            Assert.NotNull(kept);
            Assert.Null(session.TakeBrowser()); // handed over once
        }
        var page = Assert.Single(host.Sessions);
        Assert.False(page.Disposed); // disposing the login leaves a taken browser open

        var response = await kept.GetAsync(new Uri("https://bank.example/api/status"), "application/json", CancellationToken.None);
        Assert.Equal(200, response.Status);
        Assert.Equal("application/json", Assert.Single(page.Fetches).Headers["Accept"]);

        await kept.DisposeAsync();
        Assert.True(page.Disposed);
        Assert.False(kept.IsOpen);
        await Assert.ThrowsAsync<ConnectorException>(() => kept.GetAsync(new Uri("https://bank.example/api/status"), null, CancellationToken.None));
    }

    [Fact]
    public async Task A_kept_browser_nobody_took_is_destroyed_with_the_login()
    {
        var host = new FakeBrowserHost { User = SignsIn };
        await using (var session = await Service(host).StartAsync(BankWorld.Alice, Spec(), [], CancellationToken.None))
            Assert.True((await session.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Success);
        Assert.True(Assert.Single(host.Sessions).Disposed);
    }

    [Fact]
    public async Task Without_keep_browser_the_browser_is_destroyed_at_once()
    {
        var host = new FakeBrowserHost { User = SignsIn };
        await using var session = await Service(host).StartAsync(BankWorld.Alice, Spec(keep: false), [], CancellationToken.None);
        Assert.True((await session.Completion.WaitAsync(TimeSpan.FromSeconds(5))).Success);
        Assert.Null(session.TakeBrowser());
        Assert.True(Assert.Single(host.Sessions).Disposed);
    }

    [Fact]
    public async Task A_kept_browser_closes_itself_after_its_lifetime()
    {
        var host = new FakeBrowserHost { User = SignsIn };
        await using var session = await Service(host, TimeSpan.FromMilliseconds(100)).StartAsync(BankWorld.Alice, Spec(), [], CancellationToken.None);
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        var kept = session.TakeBrowser()!;
        for (var i = 0; i < 300 && kept.IsOpen; i++) await Task.Delay(10);
        Assert.False(kept.IsOpen);
        Assert.True(Assert.Single(host.Sessions).Disposed);
    }

    [Fact]
    public async Task Concurrent_requests_run_one_at_a_time()
    {
        var host = new FakeBrowserHost { User = SignsIn };
        await using var session = await Service(host).StartAsync(BankWorld.Alice, Spec(), [], CancellationToken.None);
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        await using var kept = session.TakeBrowser()!;
        var all = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => kept.GetAsync(new Uri($"https://bank.example/r/{i}"), null, CancellationToken.None)));
        Assert.All(all, r => Assert.Equal(200, r.Status));
        Assert.Equal(20, host.Sessions.Single().Fetches.Count);
    }

    [Fact]
    public async Task Closing_an_expired_browser_waits_for_its_final_cookie_snapshot()
    {
        var host = new FakeBrowserHost { User = SignsIn };
        await using var session = await Service(host, TimeSpan.FromMilliseconds(100)).StartAsync(BankWorld.Alice, Spec(), [], CancellationToken.None);
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        var kept = session.TakeBrowser()!;
        var page = Assert.Single(host.Sessions);
        var collecting = new TaskCompletionSource();
        var release = new TaskCompletionSource();
        page.SetCookie(Sid("renewed"));
        page.BeforeGetCookies = async () => { collecting.TrySetResult(); await release.Task; };
        IReadOnlyList<CookieState>? finalCookies = null;
        kept.SetCookieSink(cookies => finalCookies = cookies);

        await collecting.Task.WaitAsync(TimeSpan.FromSeconds(5)); // the lifetime timer starts closing it
        var closing = kept.DisposeAsync().AsTask();
        try
        {
            Assert.False(closing.IsCompleted);
            Assert.False(page.Disposed);
        }
        finally { release.TrySetResult(); }
        await closing;
        Assert.Equal("renewed", Assert.Single(finalCookies!).Value);
        Assert.True(page.Disposed);
    }

    [Fact]
    public async Task A_stored_session_that_still_works_on_the_resume_page_needs_no_sign_in()
    {
        var host = new FakeBrowserHost(); // the stored cookie keeps the resume page signed in; nobody would sign in on the form
        await using var session = await Service(host).StartAsync(BankWorld.Alice, Spec(resume: Home), [Sid()], CancellationToken.None);
        var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.Success);
        Assert.Equal([Home], host.Sessions.Single().Navigations);
        await using var kept = session.TakeBrowser();
        Assert.NotNull(kept);
    }

    [Fact]
    public async Task An_expired_stored_session_goes_on_to_the_sign_in()
    {
        var host = new FakeBrowserHost
        {
            Redirect = url => url == Home ? new Uri("https://bank.example/") : null, // the site sends a dead session away
            User = s => s.Navigations.Count > 1 ? SignsIn(s) : Task.CompletedTask,
        };
        await using var session = await Service(host).StartAsync(BankWorld.Alice, Spec(resume: Home), [Sid("dead")], CancellationToken.None);
        var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.Success);
        Assert.Equal([Home, Start], host.Sessions.Single().Navigations);
    }

    /// <summary>Signs in through a web login that keeps the browser and uses it during the login step.</summary>
    private sealed class KeepingConnector : IBankConnector
    {
        public const string Type = "keepingbank";

        public static IBrowserFetcher? Browser { get; set; }

        public static string? Answer { get; set; }

        public static bool FailListing { get; set; }

        public string ConnectorType => Type;

        public static ConnectorRegistration Registration { get; } = new(
            Type, "Keeping Bank", "Signs in on the bank's page.", _ => new KeepingConnector(), CredentialRequirement.None);

        public async Task<ConnectorStep<ConnectorSession?>> LoginAsync(ConnectorCredentials credentials, ConnectorSession? storedSession, ChallengeAnswer? answer, CancellationToken ct)
        {
            if (answer == null)
                return ConnectorStep.Ask<ConnectorSession?>(ConnectorChallenge.WebLogin("Sign in", Spec()));
            Browser = answer.Browser;
            Answer = (await answer.Browser!.GetAsync(new Uri("https://bank.example/api/me"), "application/json", ct)).Text;
            return ConnectorStep.Done<ConnectorSession?>(new ConnectorSession());
        }

        public Task<IReadOnlyList<RemoteAccount>> ListAccountsAsync(CancellationToken ct) => FailListing
            ? Task.FromException<IReadOnlyList<RemoteAccount>>(new ConnectorException("Account listing failed."))
            : Task.FromResult<IReadOnlyList<RemoteAccount>>([]);

        public Task<ConnectorStep<AccountRefresh>> RefreshAsync(RemoteAccount account, DateOnly? since, ChallengeAnswer? answer, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<ConnectorStep<IReadOnlyList<RemoteStatement>>> FetchStatementsAsync(
            IReadOnlyList<RemoteAccount> accounts, IReadOnlySet<string> knownIdentifiers, ChallengeAnswer? answer, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<ConnectorSession?> LogoutAsync(CancellationToken ct) => Task.FromResult<ConnectorSession?>(new ConnectorSession());
    }

    [Fact]
    public async Task The_sync_hands_the_kept_browser_to_the_connector_and_closes_it_when_the_sync_ends()
    {
        var host = new FakeBrowserHost { User = SignsIn };
        await using var world = await BankWorld.CreateAsync(extras: new BankWorldExtras
        {
            Registrations = [KeepingConnector.Registration],
            BrowserHost = host,
            WebLoginOptions = new WebLoginOptions { PollInterval = TimeSpan.FromMilliseconds(10), SettleDelay = TimeSpan.Zero, FormTimeout = TimeSpan.FromMilliseconds(300) },
        });
        var view = await world.For(BankWorld.Alice).CreateLoginAsync(KeepingConnector.Type, "Keeping bank", "", "");

        var status = await world.SyncAsync(BankWorld.Alice, view.Id, new SyncRequest(view.Id, Transactions: false, Statements: false));

        Assert.True(status.Succeeded, status.Error);
        Assert.Equal("ok /api/me", KeepingConnector.Answer);
        Assert.False(KeepingConnector.Browser!.IsOpen);
        Assert.True(Assert.Single(host.Sessions).Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Final_browser_cookies_are_saved_even_when_account_listing_fails(bool failListing)
    {
        var host = new FakeBrowserHost
        {
            User = s =>
            {
                s.SetCookie(new CookieState("removed", "old", Domain, "/", null, true, true));
                s.OnFetch = page =>
                {
                    page.SetCookie(Sid("renewed"));
                    page.DropCookie("removed");
                    page.SetCookie(new CookieState("unrelated", "discard", "ads.invalid", "/", null, true, true));
                };
                return SignsIn(s);
            },
        };
        await using var world = await BankWorld.CreateAsync(extras: new BankWorldExtras
        {
            Registrations = [KeepingConnector.Registration],
            BrowserHost = host,
            WebLoginOptions = new WebLoginOptions { PollInterval = TimeSpan.FromMilliseconds(10), SettleDelay = TimeSpan.Zero },
        });
        var view = await world.For(BankWorld.Alice).CreateLoginAsync(KeepingConnector.Type, "Keeping bank", "", "");
        KeepingConnector.FailListing = failListing;
        try
        {
            var status = await world.SyncAsync(BankWorld.Alice, view.Id, new SyncRequest(view.Id, Transactions: false, Statements: false));
            Assert.Equal(!failListing, status.Succeeded);
            await using var db = await world.T.System.CreateDbContextAsync();
            var login = await db.BankLogins.SingleAsync(l => l.Id == view.Id);
            Assert.True(world.Secrets.TryDecrypt(login.EncryptedSession, BankSyncRunner.SessionPurpose, out var json));
            var cookie = Assert.Single(JsonSerializer.Deserialize<ConnectorSession>(json)!.Cookies);
            Assert.Equal("sid", cookie.Name);
            Assert.Equal("renewed", cookie.Value);
            Assert.True(Assert.Single(host.Sessions).Disposed);
        }
        finally { KeepingConnector.FailListing = false; }
    }
}
