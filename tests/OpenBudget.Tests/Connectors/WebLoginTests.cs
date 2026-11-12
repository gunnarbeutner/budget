#pragma warning disable CA2000 // test objects live for the test
using System.Text.Json;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
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
    private sealed class WebBankConnector(ConnectorContext context, bool probeHeaders = false, bool codePrompt = false, bool autofill = false) : IBankConnector
    {
        public const string Type = "webbank";

        public string ConnectorType => Type;

        public static ConnectorRegistration Registration { get; } = new(
            Type, "Web Bank", "Signs in on the bank's page.", ctx => new WebBankConnector(ctx), CredentialRequirement.None);

        public async Task<ConnectorStep<ConnectorSession?>> LoginAsync(ConnectorCredentials credentials, ConnectorSession? storedSession, ChallengeAnswer? answer, CancellationToken ct)
        {
            if (probeHeaders) await context.Http.GetStringAsync(new Uri("https://bank.example/probe"), ct);
            return answer == null
                ? ConnectorStep.Ask<ConnectorSession?>(ConnectorChallenge.WebLogin("Sign in", new WebLoginSpec(
                    Start, [Domain], successUrlPrefix: new Uri("https://bank.example/home"))
                {
                    CodePrompt = codePrompt ? new WebLoginCodePrompt("/mfa", "#code", "#verify", "Enter the bank's security code.") : null,
                    Autofill = autofill ? new WebLoginAutofill("alice", "Example-Password1!", [new WebLoginStep("#user", WebLoginCredential.Username, "#go")]) : null,
                }))
                : ConnectorStep.Done<ConnectorSession?>(new ConnectorSession());
        }

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_browser_security_code_uses_the_coordinators_existing_code_challenge(bool shortFirst)
    {
        var browser = new FakeBrowserHost
        {
            User = page =>
            {
                page.Url = new Uri("https://bank.example/mfa");
                page.Visible.UnionWith(["#code", "#verify"]);
                page.OnClick = (p, _) => SignedIn(p);
                return Task.CompletedTask;
            },
        };
        var registration = new ConnectorRegistration(WebBankConnector.Type, "Web bank", "Web login",
            context => new WebBankConnector(context, codePrompt: true), CredentialRequirement.None);
        await using var world = await BankWorld.CreateAsync(extras: new BankWorldExtras { Registrations = [registration], BrowserHost = browser });
        var login = await world.For(BankWorld.Alice).CreateLoginAsync(WebBankConnector.Type, "Web bank", "", "");
        var result = await world.SyncAsync(BankWorld.Alice, login.Id, LoginOnly(login.Id), shortFirst ? ["12", "123456"] : ["123456"]);
        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("123456", browser.Sessions.Single().Filled["#code"]);
        Assert.Single(browser.Sessions.Single().Clicked);
    }

    [Fact]
    public async Task A_security_code_is_not_typed_if_the_browser_leaves_the_banks_origin_while_waiting()
    {
        var browser = new FakeBrowserHost
        {
            User = page =>
            {
                page.Url = new Uri("https://bank.example/mfa");
                page.Visible.UnionWith(["#code", "#verify"]);
                return Task.CompletedTask;
            },
        };
        var service = new WebLoginService(browser, new WebLoginOptions { PollInterval = TimeSpan.FromMilliseconds(10), SettleDelay = TimeSpan.Zero }, TimeProvider.System, NullLoggerFactory.Instance);
        var spec = new WebLoginSpec(Start, [Domain], new Uri("https://bank.example/home"))
        {
            CodePrompt = new WebLoginCodePrompt("/mfa", "#code", "#verify", "Enter the code."),
        };
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        WebLoginSession? active = null;
        async Task<ChallengeAnswer?> Answer(ConnectorChallenge challenge, CancellationToken ct)
        {
            await ready.Task.WaitAsync(ct);
            active!.ModeChanged += mode => { if (mode == WebLoginMode.Interactive) shown.TrySetResult(); };
            browser.Sessions.Single().Url = new Uri("https://other.example/mfa");
            return new ChallengeAnswer(challenge, "123456");
        }
        await using var session = await service.StartAsync("user", spec, [], CancellationToken.None, Answer);
        active = session;
        ready.TrySetResult();
        await shown.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(browser.Sessions.Single().Filled);
        Assert.Empty(browser.Sessions.Single().Clicked);
        Assert.False(session.Completion.IsCompleted);
    }

    [Fact]
    public async Task Streamed_input_uses_the_native_viewport_instead_of_the_configured_window_size()
    {
        var page = new FakeBrowserSession(null) { ViewportWidth = 1600, ViewportHeight = 1000 };
        await using var view = new RemoteViewSession(page, new BrowserOptions(), NullLogger.Instance, _ => Task.CompletedTask);
        await view.StartAsync(1600, 1);
        view.Post("""{"t":"m","k":"down","x":1450,"y":950,"b":"left","bs":1,"c":1}""");
        for (var i = 0; i < 100; i++)
        {
            lock (page.Mouse) { if (page.Mouse.Count > 0) break; }
            await Task.Delay(10);
        }
        lock (page.Mouse) Assert.Equal(("mousePressed", 1450d, 950d), Assert.Single(page.Mouse));
    }

    private async Task<List<CookieState>> StoredCookiesAsync(BankWorld world, int loginId)
    {
        await using var db = await world.T.System.CreateDbContextAsync();
        var login = await db.BankLogins.AsNoTracking().FirstAsync(l => l.Id == loginId);
        Assert.True(world.Secrets.TryDecrypt(login.EncryptedSession, BankSyncRunner.SessionPurpose, out var json));
        return JsonSerializer.Deserialize<ConnectorSession>(json)!.Cookies;
    }

    private sealed class HeaderCapture(List<string> agents) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            agents.Add(request.Headers.UserAgent.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") });
        }
    }

    [Fact]
    public async Task Browser_identity_is_used_by_HTTPS_requests_and_restored_on_the_next_sync()
    {
        const string browserAgent = "Mozilla/5.0 NativeBrowser/1.0";
        var agents = new List<string>();
        var browser = new FakeBrowserHost { User = SignedIn, UserAgent = browserAgent };
        var registration = new ConnectorRegistration(WebBankConnector.Type, "Web Bank", "Web login",
            ctx => new WebBankConnector(ctx, probeHeaders: true), CredentialRequirement.None);
        await using var world = await BankWorld.CreateAsync(extras: new BankWorldExtras
        {
            Registrations = [registration],
            BrowserHost = browser,
            HttpFactory = () => new BankHttpClient(new HeaderCapture(agents)),
        });
        var login = await world.For(BankWorld.Alice).CreateLoginAsync(WebBankConnector.Type, "Web bank", "", "");

        Assert.True((await world.SyncAsync(BankWorld.Alice, login.Id, LoginOnly(login.Id))).Succeeded);
        Assert.Equal(browserAgent, agents[1]); // API request after the browser login
        Assert.True((await world.SyncAsync(BankWorld.Alice, login.Id, LoginOnly(login.Id))).Succeeded);
        Assert.Equal(browserAgent, agents[2]); // API request before opening the next browser
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Interrupted_sign_in_keeps_bank_cookies_for_the_next_attempt(bool cancel, bool previousSession)
    {
        var verified = new TaskCompletionSource();
        Task VerifyDevice(FakeBrowserSession session)
        {
            session.SetCookie(Cookie("verification", "device-verified", DateTime.UtcNow.AddDays(7), sameSite: "None"));
            session.SetCookie(Cookie("unrelated", "discard", DateTime.UtcNow.AddDays(7), domain: "ads.invalid"));
            verified.TrySetResult();
            return Task.CompletedTask;
        }
        var (world, browser, loginId) = await CreateAsync(previousSession ? SignedIn : VerifyDevice,
            options: new WebLoginOptions
            {
                Timeout = cancel ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(300),
                PollInterval = TimeSpan.FromMilliseconds(10),
                SettleDelay = TimeSpan.Zero,
            });
        await using var _ = world;
        if (previousSession)
        {
            Assert.True((await world.SyncAsync(BankWorld.Alice, loginId, LoginOnly(loginId))).Succeeded);
            await world.EditSessionAsync(loginId, values => values["known-account"] = "EUR");
            browser.User = VerifyDevice;
        }

        Assert.Equal(StartResult.Started, await world.Coordinator.StartAsync(BankWorld.Alice, LoginOnly(loginId)));
        await verified.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (cancel) Assert.True(world.Coordinator.Cancel(BankWorld.Alice, loginId));
        var interrupted = await world.WaitAsync(BankWorld.Alice, loginId, []);
        Assert.False(interrupted.Succeeded);
        Assert.Contains(cancel ? "cancelled" : "timed out", interrupted.Error!, StringComparison.OrdinalIgnoreCase);

        var cookies = await StoredCookiesAsync(world, loginId);
        var verification = Assert.Single(cookies, c => c.Name == "verification");
        Assert.Equal("device-verified", verification.Value);
        Assert.Equal("None", verification.SameSite);
        Assert.DoesNotContain(cookies, c => c.Domain == "ads.invalid");
        if (previousSession)
        {
            await using var db = await world.T.System.CreateDbContextAsync();
            var login = await db.BankLogins.SingleAsync(l => l.Id == loginId);
            Assert.True(world.Secrets.TryDecrypt(login.EncryptedSession, BankSyncRunner.SessionPurpose, out var json));
            Assert.Equal("EUR", JsonSerializer.Deserialize<ConnectorSession>(json)!.Values["known-account"]);
        }

        browser.User = SignedIn;
        Assert.True((await world.SyncAsync(BankWorld.Alice, loginId, LoginOnly(loginId))).Succeeded);
        Assert.Contains(browser.Sessions[^1].Seeded, c => c.Name == "verification" && c.Value == "device-verified");
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
    public async Task A_web_login_waits_for_the_user_only_while_its_page_is_shown_to_them()
    {
        // The app fills the form by itself first; the form never turns up, so after the timeout the page goes to the user.
        var browser = new FakeBrowserHost { FieldWait = TimeSpan.FromMilliseconds(600) };
        var registration = new ConnectorRegistration(WebBankConnector.Type, "Web bank", "Web login",
            context => new WebBankConnector(context, autofill: true), CredentialRequirement.None);
        await using var world = await BankWorld.CreateAsync(extras: new BankWorldExtras
        {
            Registrations = [registration],
            BrowserHost = browser,
            WebLoginOptions = new WebLoginOptions { PollInterval = TimeSpan.FromMilliseconds(10), SettleDelay = TimeSpan.Zero, FormTimeout = TimeSpan.FromMilliseconds(600) },
        });
        var loginId = (await world.For(BankWorld.Alice).CreateLoginAsync(WebBankConnector.Type, "Web bank", "", "")).Id;

        Assert.Equal(StartResult.Started, await world.Coordinator.StartAsync(BankWorld.Alice, LoginOnly(loginId)));
        await WaitForAsync(() => world.Coordinator.GetStatus(BankWorld.Alice, loginId)?.WebLoginSessionId != null);
        var signingIn = world.Coordinator.GetStatus(BankWorld.Alice, loginId)!;
        Assert.Equal(WebLoginMode.Starting, signingIn.WebLoginMode);
        Assert.Equal(SyncPhase.Running, signingIn.Phase);
        Assert.NotNull(signingIn.Challenge); // the top bar can still offer the page

        await WaitForAsync(() => world.Coordinator.GetStatus(BankWorld.Alice, loginId)?.Phase == SyncPhase.WaitingForInput);
        Assert.Equal(WebLoginMode.Interactive, world.Coordinator.GetStatus(BankWorld.Alice, loginId)!.WebLoginMode);

        await SignedIn(browser.Sessions.Single());
        var done = await world.WaitAsync(BankWorld.Alice, loginId, []);
        Assert.True(done.Succeeded, done.Error);
        Assert.Equal(SyncPhase.Finished, done.Phase);
        Assert.Null(done.Challenge);
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
        Assert.Equal(SyncPhase.WaitingForInput, open.Phase); // without a stored form the user signs in on the page
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

    private static WebLoginService Service(FakeBrowserHost host, Microsoft.Extensions.Logging.ILoggerFactory? logs = null) => new(host,
        new WebLoginOptions { PollInterval = TimeSpan.FromMilliseconds(10), SettleDelay = TimeSpan.Zero, FormTimeout = TimeSpan.FromMilliseconds(300) },
        TimeProvider.System, logs ?? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }

    [Fact]
    public async Task A_two_step_form_is_filled_page_by_page_and_waits_for_the_enabled_button()
    {
        const string email = "[data-testid=email]", next = "[data-testid=next]:not([disabled])", password = "input[type=password]", submit = "button[type=submit]";
        var host = new FakeBrowserHost
        {
            User = s =>
            {
                s.OnFill = (p, selector) =>
                {
                    if (selector == email) p.Visible.Add(next); // the button is enabled by the page's input handler
                };
                s.OnClick = (p, selector) =>
                {
                    lock (p.Visible)
                    {
                        p.Visible.Clear();
                        if (selector == next) { p.Visible.Add(password); p.Visible.Add(submit); }
                    }
                    if (selector == submit && p.Filled.GetValueOrDefault(password) == "pw") return SignedIn(p);
                    return Task.CompletedTask;
                };
                lock (s.Visible) s.Visible.Add(email);
                return Task.CompletedTask;
            },
        };
        var spec = new WebLoginSpec(Start, [Domain], new Uri("https://bank.example/home"))
        {
            Autofill = new WebLoginAutofill("alice@example.com", "pw",
            [
                new WebLoginStep(email, WebLoginCredential.Username, next),
                new WebLoginStep(password, WebLoginCredential.Secret, submit),
            ]),
        };
        await using var session = await Service(host).StartAsync(BankWorld.Alice, spec, [], CancellationToken.None);
        var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(result.Success, result.Error);
        var browser = host.Sessions.Single();
        Assert.Equal("alice@example.com", browser.Filled[email]);
        Assert.Equal("pw", browser.Filled[password]);
        Assert.Equal([next, submit], browser.Clicked);
        Assert.DoesNotContain("pw", spec.Autofill.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_single_step_form_still_fills_both_fields_and_submits_once()
    {
        var host = new FakeBrowserHost
        {
            User = s =>
            {
                lock (s.Visible) { s.Visible.Add("#user"); s.Visible.Add("#pass"); s.Visible.Add("#go"); }
                s.OnClick = (p, _) => p.Filled.GetValueOrDefault("#pass") == "pw" ? SignedIn(p) : Task.CompletedTask;
                return Task.CompletedTask;
            },
        };
        var spec = new WebLoginSpec(Start, [Domain], new Uri("https://bank.example/home"))
        {
            Autofill = new WebLoginAutofill("alice", "pw", "#user", "#pass", "#go"),
        };
        await using var session = await Service(host).StartAsync(BankWorld.Alice, spec, [], CancellationToken.None);
        Assert.True((await session.Completion.WaitAsync(TimeSpan.FromSeconds(10))).Success);
        var browser = host.Sessions.Single();
        Assert.Equal("alice", browser.Filled["#user"]);
        Assert.Equal(["#go"], browser.Clicked);
    }

    [Fact]
    public async Task A_form_that_never_matches_shows_the_page_after_the_timeout_even_when_the_login_page_is_under_the_success_prefix()
    {
        // Like Openbank: the login page (/portal/login) starts with the success prefix (/portal/), success also needs a cookie.
        var start = new Uri("https://bank.example/portal/login?token=secret-query");
        var host = new FakeBrowserHost { User = _ => Task.CompletedTask }; // a page with no matching field
        var logs = new CapturingLoggerFactory();
        var spec = new WebLoginSpec(start, [Domain], new Uri("https://bank.example/portal/"), (_, cookies) => cookies.Any(c => c.Name == "auth"))
        {
            Autofill = new WebLoginAutofill("alice@example.com", "Example-Password1!",
            [
                new WebLoginStep("#email", WebLoginCredential.Username, "#next"),
                new WebLoginStep("#password", WebLoginCredential.Secret, "#submit"),
            ]),
        };
        await using var session = await Service(host, logs).StartAsync(BankWorld.Alice, spec, [], CancellationToken.None);
        Assert.Equal(WebLoginMode.Starting, session.Mode);
        await WaitForAsync(() => session.Mode == WebLoginMode.Interactive);
        Assert.False(session.Completion.IsCompleted);

        var text = logs.All;
        Assert.Contains("Information: Web login:", text, StringComparison.Ordinal);
        Assert.Contains("#email", text, StringComparison.Ordinal);
        Assert.Contains("step 1 of 2", text, StringComparison.Ordinal);
        Assert.Contains("https://bank.example/portal/login", text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-query", text, StringComparison.Ordinal);
        Assert.DoesNotContain("alice@example.com", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Example-Password1!", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_second_step_that_never_appears_shows_the_page()
    {
        var host = new FakeBrowserHost
        {
            User = s =>
            {
                lock (s.Visible) { s.Visible.Add("#email"); s.Visible.Add("#next"); }
                s.OnClick = (p, _) => { lock (p.Visible) p.Visible.Clear(); return Task.CompletedTask; };
                return Task.CompletedTask;
            },
        };
        var spec = new WebLoginSpec(Start, [Domain], new Uri("https://bank.example/home"))
        {
            Autofill = new WebLoginAutofill("a", "b",
                [new WebLoginStep("#email", WebLoginCredential.Username, "#next"), new WebLoginStep("#password", WebLoginCredential.Secret, "#submit")]),
        };
        await using var session = await Service(host).StartAsync(BankWorld.Alice, spec, [], CancellationToken.None);
        await WaitForAsync(() => session.Mode == WebLoginMode.Interactive);
        Assert.Equal(["#next"], host.Sessions.Single().Clicked);
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

    [Fact]
    public void Drag_moves_keep_their_path_and_original_timestamps()
    {
        var drag = new[]
        {
            new RemoteInput { T = "m", K = "down", Buttons = 1, X = 10, Timestamp = 1000 },
            new RemoteInput { T = "m", K = "move", Buttons = 1, X = 20, Timestamp = 1000.01 },
            new RemoteInput { T = "m", K = "move", Buttons = 1, X = 40, Timestamp = 1000.03 },
            new RemoteInput { T = "m", K = "up", Buttons = 0, X = 40, Timestamp = 1000.05 },
        };
        var forwarded = InputCoalescer.Coalesce(drag);
        Assert.Equal(drag, forwarded);
        Assert.Equal([1000d, 1000.01d, 1000.03d, 1000.05d], forwarded.Select(m => m.Timestamp!.Value));
        Assert.Null(new RemoteInput { T = "m", K = "move", Timestamp = double.NaN }.Sanitize(1280, 800)!.Timestamp);
    }

    [Fact]
    public async Task Streamed_drag_timestamps_reach_the_browser_without_being_replaced_by_queue_time()
    {
        var page = new FakeBrowserSession(null);
        await using var view = new RemoteViewSession(page, new BrowserOptions(), NullLogger.Instance, _ => Task.CompletedTask);
        await view.StartAsync(1280, 1);
        view.Post("""{"t":"m","k":"down","x":10,"y":10,"bs":1,"ts":1000}""");
        view.Post("""{"t":"m","k":"move","x":20,"y":10,"bs":1,"ts":1000.01}""");
        view.Post("""{"t":"m","k":"move","x":40,"y":10,"bs":1,"ts":1000.03}""");
        view.Post("""{"t":"m","k":"up","x":40,"y":10,"bs":0,"ts":1000.05}""");
        for (var i = 0; i < 100; i++)
        {
            lock (page.Mouse) { if (page.Mouse.Count == 4) break; }
            await Task.Delay(10);
        }
        lock (page.Mouse) Assert.Equal(new double?[] { 1000, 1000.01, 1000.03, 1000.05 }, page.MouseTimestamps);
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
