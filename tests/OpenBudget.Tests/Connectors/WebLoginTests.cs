#pragma warning disable CA2000 // test objects live for the test
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenBudget.Browser;
using OpenBudget.Connectors;
using OpenBudget.Connectors.Sync;
using Xunit;

namespace OpenBudget.Tests.Connectors;

/// <summary>The WebLogin challenge through the sync framework, against a fake browser (no Chromium).</summary>
public sealed class WebLoginTests
{
    private const string Domain = "bank.example";
    private static readonly Uri Start = new("https://bank.example/login");

    /// <summary>A bank whose only way in is a web login: asks for it, then is done.</summary>
    private sealed class WebBankConnector : IBankConnector
    {
        public const string Type = "webbank";

        public string ConnectorType => Type;

        public static ConnectorRegistration Registration { get; } = new(
            Type, "Web Bank", "Signs in on the bank's page.", _ => new WebBankConnector(), CredentialRequirement.None);

        public Task<ConnectorStep<ConnectorSession?>> LoginAsync(ConnectorCredentials credentials, ConnectorSession? storedSession, ChallengeAnswer? answer, CancellationToken ct) =>
            Task.FromResult(answer == null
                ? ConnectorStep.Ask<ConnectorSession?>(ConnectorChallenge.WebLogin("Sign in", new WebLoginSpec(
                    Start, [Domain], successUrlPrefix: new Uri("https://bank.example/home"))))
                : ConnectorStep.Done<ConnectorSession?>(new ConnectorSession()));

        public Task<IReadOnlyList<RemoteAccount>> ListAccountsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<RemoteAccount>>([]);

        public Task<ConnectorStep<AccountRefresh>> RefreshAsync(RemoteAccount account, DateOnly? since, ChallengeAnswer? answer, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<ConnectorStep<IReadOnlyList<RemoteStatement>>> FetchStatementsAsync(
            IReadOnlyList<RemoteAccount> accounts, IReadOnlySet<string> knownIdentifiers, ChallengeAnswer? answer, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<ConnectorSession?> LogoutAsync(CancellationToken ct) => Task.FromResult<ConnectorSession?>(new ConnectorSession());
    }

    private static async Task<(BankWorld World, FakeBrowserHost Browser, int LoginId)> CreateAsync(
        Func<FakeBrowserSession, Task>? user, Action<FakeBrowserHost>? configure = null, WebLoginOptions? options = null)
    {
        var browser = new FakeBrowserHost { User = user };
        configure?.Invoke(browser);
        var world = await BankWorld.CreateAsync(extras: new BankWorldExtras
        {
            Registrations = [WebBankConnector.Registration],
            BrowserHost = browser,
            WebLoginOptions = options,
        });
        var view = await world.For(BankWorld.Alice).CreateLoginAsync(WebBankConnector.Type, "Web bank", "", "");
        return (world, browser, view.Id);
    }

    private static CookieState Cookie(string name, string value, DateTime? expires, string domain = Domain, string? sameSite = "Lax") =>
        new(name, value, domain, "/", expires, Secure: true, HttpOnly: true, sameSite);

    private static Task SignedIn(FakeBrowserSession s)
    {
        s.SetCookie(Cookie("sid", "s-1", DateTime.UtcNow.AddDays(30)));
        s.Url = new Uri("https://bank.example/home");
        return Task.CompletedTask;
    }

    private static SyncRequest LoginOnly(int id) => new(id, Transactions: false, Statements: false);

    private async Task<List<CookieState>> StoredCookiesAsync(BankWorld world, int loginId)
    {
        await using var db = await world.T.System.CreateDbContextAsync();
        var login = await db.BankLogins.AsNoTracking().FirstAsync(l => l.Id == loginId);
        Assert.True(world.Secrets.TryDecrypt(login.EncryptedSession, BankSyncRunner.SessionPurpose, out var json));
        return JsonSerializer.Deserialize<ConnectorSession>(json)!.Cookies;
    }

    [Fact]
    public async Task Web_login_succeeds_imports_the_browser_cookies_and_destroys_the_browser()
    {
        var (world, browser, loginId) = await CreateAsync(SignedIn);
        await using var _ = world;

        var status = await world.SyncAsync(BankWorld.Alice, loginId, LoginOnly(loginId));
        Assert.True(status.Succeeded, status.Error);

        var session = Assert.Single(browser.Sessions);
        Assert.Equal(Start, session.StartedAt);
        Assert.True(session.Disposed); // nothing stays in a browser on the server

        var cookie = Assert.Single(await StoredCookiesAsync(world, loginId));
        Assert.Equal("sid", cookie.Name);
        Assert.Equal("s-1", cookie.Value);
        Assert.True(cookie.HttpOnly);
        Assert.True(cookie.Secure);
        Assert.Equal("Lax", cookie.SameSite);
        Assert.NotNull(cookie.ExpiresUtc); // persistent cookies keep their expiry
    }

    [Fact]
    public async Task Cookies_of_the_previous_login_seed_the_next_browser_and_the_new_ones_replace_them_in_the_jar()
    {
        var round = 0;
        var (world, browser, loginId) = await CreateAsync(s =>
        {
            round++;
            if (round == 1)
            {
                s.SetCookie(Cookie("remember", "device-1", DateTime.UtcNow.AddDays(90), sameSite: "None"));
                s.SetCookie(Cookie("sid", "s-1", DateTime.UtcNow.AddDays(1)));
                s.SetCookie(Cookie("old", "x", DateTime.UtcNow.AddDays(1)));
                s.SetCookie(Cookie("tracker", "t", DateTime.UtcNow.AddDays(1), domain: "ads.invalid")); // not a bank domain: never exported
            }
            else
            {
                s.SetCookie(Cookie("remember", "device-2", DateTime.UtcNow.AddDays(90), sameSite: "None"));
                s.SetCookie(Cookie("sid", "s-2", DateTime.UtcNow.AddDays(1)));
                s.SetCookie(Cookie("expired", "gone", DateTime.UtcNow.AddDays(-1)));
                s.DropCookie("old");
            }
            s.Url = new Uri("https://bank.example/home");
            return Task.CompletedTask;
        });
        await using var _ = world;

        Assert.True((await world.SyncAsync(BankWorld.Alice, loginId, LoginOnly(loginId))).Succeeded);
        var first = await StoredCookiesAsync(world, loginId);
        Assert.Equal(["old", "remember", "sid"], first.Select(c => c.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.DoesNotContain(first, c => c.Name == "tracker");
        Assert.Empty(browser.Sessions[0].Seeded); // nothing to seed the first time

        Assert.True((await world.SyncAsync(BankWorld.Alice, loginId, LoginOnly(loginId))).Succeeded);

        // The jar went into the second browser, with expiry and flags ("remember this device" can spare the 2FA).
        var seeded = browser.Sessions[1].Seeded;
        Assert.Equal(["old", "remember", "sid"], seeded.Select(c => c.Name).Order(StringComparer.Ordinal).ToArray());
        var remembered = seeded.Single(c => c.Name == "remember");
        Assert.Equal("device-1", remembered.Value);
        Assert.Equal("None", remembered.SameSite);
        Assert.True(remembered.ExpiresUtc > DateTime.UtcNow.AddDays(80));

        // What the second browser ended with replaces the bank's cookies in the jar: new values, dropped and expired ones gone.
        var second = await StoredCookiesAsync(world, loginId);
        Assert.Equal(["remember", "sid"], second.Select(c => c.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("device-2", second.Single(c => c.Name == "remember").Value);
        Assert.Equal("s-2", second.Single(c => c.Name == "sid").Value);
        Assert.Equal("None", second.Single(c => c.Name == "remember").SameSite);
    }

    [Fact]
    public async Task The_challenge_is_shown_to_the_owner_only_and_nobody_else_can_attach_to_the_session()
    {
        var gate = new TaskCompletionSource();
        var (world, browser, loginId) = await CreateAsync(async s =>
        {
            await gate.Task;
            await SignedIn(s);
        });
        await using var _ = world;

        Assert.Equal(StartResult.Started, await world.Coordinator.StartAsync(BankWorld.Alice, LoginOnly(loginId)));
        BankSyncStatus? open = null;
        for (var i = 0; i < 500 && open?.WebLoginSessionId == null; i++)
        {
            await Task.Delay(10);
            open = world.Coordinator.GetStatus(BankWorld.Alice, loginId);
        }
        Assert.NotNull(open?.WebLoginSessionId);
        Assert.Equal(ChallengeKind.WebLogin, open.Challenge!.Kind);
        Assert.Equal(SyncPhase.WaitingForInput, open.Phase);
        Assert.Equal(32, open.WebLoginSessionId.Length); // 128 random bits as hex
        Assert.NotNull(open.ChallengeExpiresAt);

        var id = open.WebLoginSessionId;
        Assert.NotNull(world.WebLogins!.Find(id, BankWorld.Alice));
        Assert.Null(world.WebLogins.Find(id, BankWorld.Bob)); // another user: the same answer as for an unknown id
        Assert.Null(world.WebLogins.Find(id, BankWorld.Carol));
        Assert.Null(world.WebLogins.Find("00000000000000000000000000000000", BankWorld.Alice));
        Assert.Empty(world.Coordinator.GetStatuses(BankWorld.Bob));
        Assert.False(world.Coordinator.SubmitAnswer(BankWorld.Alice, loginId, open.ChallengeId, "anything")); // a web login is not answered with text

        gate.SetResult();
        var done = await world.WaitAsync(BankWorld.Alice, loginId, []);
        Assert.True(done.Succeeded, done.Error);
        Assert.Null(done.WebLoginSessionId);
        Assert.Null(world.WebLogins.Find(id, BankWorld.Alice)); // gone with the login
        Assert.True(browser.Sessions[0].Disposed);
    }

    [Fact]
    public async Task Cancelling_the_sync_closes_the_browser_and_fails_cleanly()
    {
        var (world, browser, loginId) = await CreateAsync(user: null); // the user never finishes
        await using var _ = world;

        Assert.Equal(StartResult.Started, await world.Coordinator.StartAsync(BankWorld.Alice, LoginOnly(loginId)));
        for (var i = 0; i < 500 && world.Coordinator.GetStatus(BankWorld.Alice, loginId)?.WebLoginSessionId == null; i++) await Task.Delay(10);
        Assert.True(world.Coordinator.Cancel(BankWorld.Alice, loginId));

        var status = await world.WaitAsync(BankWorld.Alice, loginId, []);
        Assert.False(status.Succeeded);
        Assert.Contains("cancelled", status.Error, StringComparison.OrdinalIgnoreCase);
        Assert.True(browser.Sessions[0].Disposed);
    }

    [Fact]
    public async Task A_web_login_that_takes_too_long_times_out_and_the_browser_is_destroyed()
    {
        var (world, browser, loginId) = await CreateAsync(user: null,
            options: new WebLoginOptions { Timeout = TimeSpan.FromMilliseconds(300), PollInterval = TimeSpan.FromMilliseconds(10), SettleDelay = TimeSpan.Zero });
        await using var _ = world;

        var status = await world.SyncAsync(BankWorld.Alice, loginId, LoginOnly(loginId));
        Assert.False(status.Succeeded);
        Assert.Contains("timed out", status.Error, StringComparison.OrdinalIgnoreCase);
        Assert.True(browser.Sessions[0].Disposed);
    }

    [Fact]
    public async Task Without_a_browser_the_web_login_reports_it_and_other_connectors_keep_working()
    {
        var (world, browser, loginId) = await CreateAsync(SignedIn, b => b.IsAvailable = false);
        await using var _ = world;

        var status = await world.SyncAsync(BankWorld.Alice, loginId, LoginOnly(loginId));
        Assert.False(status.Succeeded);
        Assert.Contains("browser", status.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(browser.Sessions);

        var fake = await world.AddLoginAsync(BankWorld.Alice);
        Assert.True((await world.SyncAsync(BankWorld.Alice, fake, new SyncRequest(fake, true, false), "123456")).Succeeded);
    }

    [Fact]
    public async Task A_browser_that_cannot_be_opened_fails_the_sync_with_a_readable_reason()
    {
        var (world, _, loginId) = await CreateAsync(SignedIn, b => b.IsAvailable = true);
        await using var _ = world;
        var failing = new FailingHost();
        var service = new WebLoginService(failing, new WebLoginOptions(), TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        var ex = await Assert.ThrowsAsync<BrowserActionException>(() =>
            service.StartAsync(BankWorld.Alice, new WebLoginSpec(Start, [Domain], successUrlPrefix: Start), [], CancellationToken.None));
        Assert.Contains("Too many", ex.Message, StringComparison.Ordinal);
        Assert.True(loginId > 0);
    }

    private sealed class FailingHost : IBrowserHost
    {
        public bool IsAvailable => true;

        public BrowserOptions Options { get; } = new();

        public Task<IBrowserSession> OpenSessionAsync(CancellationToken ct) => throw new BrowserActionException("Too many web logins are open at the moment.");
    }

    [Fact]
    public async Task Success_needs_both_the_url_and_the_predicate_when_both_are_given()
    {
        var host = new FakeBrowserHost();
        var service = new WebLoginService(host, new WebLoginOptions { PollInterval = TimeSpan.FromMilliseconds(10), SettleDelay = TimeSpan.Zero },
            TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);
        var spec = new WebLoginSpec(Start, [Domain], new Uri("https://bank.example/home"), (_, cookies) => cookies.Any(c => c.Name == "auth"));
        await using var session = await service.StartAsync(BankWorld.Alice, spec, [], CancellationToken.None);
        var browser = host.Sessions.Single();
        while (browser.StartedAt == null) await Task.Delay(5); // the login page has been requested; now the "user" moves on

        browser.Url = new Uri("https://bank.example/home"); // right page, no auth cookie yet
        await Task.Delay(100);
        Assert.False(session.Completion.IsCompleted);

        browser.SetCookie(Cookie("auth", "1", null));
        var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.Success);
        Assert.Contains(result.Cookies, c => c.Name == "auth");
        Assert.True(browser.Disposed);
    }

    [Fact]
    public void A_spec_needs_https_a_domain_and_a_success_condition()
    {
        Assert.Throws<ArgumentException>(() => new WebLoginSpec(new Uri("http://bank.example/"), [Domain], successUrlPrefix: Start));
        Assert.Throws<ArgumentException>(() => new WebLoginSpec(Start, [], successUrlPrefix: Start));
        Assert.Throws<ArgumentException>(() => new WebLoginSpec(Start, [Domain]));
    }

    [Theory]
    [InlineData("de.scalable.capital", "de.scalable.capital", true)]
    [InlineData(".scalable.capital", "de.scalable.capital", true)] // a parent-domain cookie reaches the page
    [InlineData("secure.de.scalable.capital", "de.scalable.capital", true)]
    [InlineData("scalable.capital.evil.invalid", "de.scalable.capital", false)]
    [InlineData("notscalable.capital", "scalable.capital", false)]
    public void Cookie_domains_match_like_a_browser_would_send_them(string cookieDomain, string specDomain, bool expected)
    {
        var spec = new WebLoginSpec(new Uri("https://de.scalable.capital/"), [specDomain], successUrlPrefix: new Uri("https://de.scalable.capital/"));
        Assert.Equal(expected, spec.Matches(Cookie("c", "v", null, cookieDomain)));
    }

    [Fact]
    public void Input_from_the_view_is_clamped_and_unknown_messages_are_dropped()
    {
        Assert.Null(new RemoteInput { T = "eval", Text = "alert(1)" }.Sanitize(1280, 800));
        Assert.Null(new RemoteInput { T = "k", K = "down", Key = new string('x', 100) }.Sanitize(1280, 800));
        Assert.Null(new RemoteInput { T = "m", K = "teleport" }.Sanitize(1280, 800));
        Assert.Null(new RemoteInput { T = "text", Text = "" }.Sanitize(1280, 800));

        var mouse = new RemoteInput { T = "m", K = "move", X = 1e9, Y = -5, Buttons = 255, Mod = 255, Button = "evil", Clicks = 99, Dx = double.NaN }.Sanitize(1280, 800)!;
        Assert.Equal(1280, mouse.X);
        Assert.Equal(0, mouse.Y);
        Assert.Equal(7, mouse.Buttons);
        Assert.Equal(15, mouse.Mod);
        Assert.Equal("left", mouse.Button);
        Assert.Equal(3, mouse.Clicks);
        Assert.Equal(0, mouse.Dx);

        Assert.Equal(2000, new RemoteInput { T = "text", Text = new string('a', 5000) }.Sanitize(1280, 800)!.Text!.Length);
        Assert.Equal(3, new RemoteInput { T = "v", W = 800, Dpr = 9 }.Sanitize(1280, 800)!.Dpr);
    }

    [Fact]
    public void Mouse_moves_and_view_sizes_collapse_and_wheel_deltas_add_up()
    {
        var input = new List<RemoteInput>
        {
            new() { T = "m", K = "move", X = 1 }, new() { T = "m", K = "move", X = 2 },
            new() { T = "m", K = "down", X = 2 },
            new() { T = "m", K = "wheel", Dy = 10 }, new() { T = "m", K = "wheel", Dy = 5 },
            new() { T = "v", W = 1 }, new() { T = "v", W = 2 },
            new() { T = "k", K = "down", Key = "a" },
        };
        var result = InputCoalescer.Coalesce(input);
        Assert.Equal(["move", "down", "wheel", "v", "down"], result.Select(m => m.K ?? m.T).ToArray());
        Assert.Equal(2, result[0].X);
        Assert.Equal(15, result[2].Dy);
        Assert.Equal(2, result[3].W);
    }

    [Theory]
    [InlineData("https://app.example", "app.example", true)]
    [InlineData("https://APP.example", "app.example", true)]
    [InlineData("https://evil.example", "app.example", false)]
    [InlineData("not a url", "app.example", false)]
    public void The_websocket_origin_must_be_the_apps_own(string origin, string host, bool expected)
    {
        var ctx = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        ctx.Request.Host = new Microsoft.AspNetCore.Http.HostString(host);
        Assert.Equal(expected, WebLoginEndpoints.SameOrigin(origin, ctx.Request));
    }
}
