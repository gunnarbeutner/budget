#pragma warning disable CA2000 // test objects live for the test
using System.Net;
using Microsoft.Extensions.Logging;
using OpenBudget.Browser;
using OpenBudget.Connectors;
using OpenBudget.Connectors.Openbank;
using OpenBudget.Connectors.Sync;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests.Connectors;

/// <summary>
/// The Openbank connector through the sync framework, against a made-up Openbank API and a fake browser that plays
/// Openbank's sign-in page (no network, no Chromium).
/// </summary>
public sealed class OpenbankConnectorTests
{
    private const string Dashboard = "https://kunden.openbankpay.com/amazon/dashboard";

    private sealed class Rig : IAsyncDisposable
    {
        public required BankWorld World { get; init; }

        public required FakeOpenbank Bank { get; init; }

        public required CapturingLoggerFactory Logs { get; init; }

        public required FakeBrowserHost Browser { get; init; }

        public int LoginId { get; set; }

        public ValueTask DisposeAsync() => World.DisposeAsync();

        public BankLoginService Service => World.For(BankWorld.Alice);
    }

    private static OpenbankOptions Options(int pageSize = 3) => new() { PageSize = pageSize };

    /// <summary>
    /// The sign-in page: Openbank's two-step React form. The e-mail page's "Weiter" only becomes clickable once the
    /// address was entered; it leads to the password page, whose submit redirects to the dashboard with the token
    /// cookies (<paramref name="withTokens"/> false: without them). A wrong password keeps the form.
    /// </summary>
    private static FakeBrowserHost SignInPage(FakeOpenbank bank, bool withTokens = true) => new()
    {
        User = session =>
        {
            session.Url = new Uri("https://kunden.openbankpay.com/amazon/login");
            session.OnFill = (s, selector) =>
            {
                if (selector == OpenbankConnector.UsernameSelector) s.Visible.Add(OpenbankConnector.ContinueSelector);
                if (Enumerable.Range(0, OpenbankConnector.AccessCodeLength).All(i => s.Filled.ContainsKey(OpenbankConnector.AccessCodeBox(i))))
                    s.Visible.Add(OpenbankConnector.SubmitSelector);
            };
            lock (session.Visible) session.Visible.Add(OpenbankConnector.UsernameSelector);
            session.OnClick = (s, selector) =>
            {
                if (selector == OpenbankConnector.ContinueSelector)
                {
                    if (s.Filled.GetValueOrDefault(OpenbankConnector.UsernameSelector) != FakeOpenbank.Username) return Task.CompletedTask;
                    lock (s.Visible)
                    {
                        s.Visible.Clear();
                        for (var i = 0; i < OpenbankConnector.AccessCodeLength; i++) s.Visible.Add(OpenbankConnector.AccessCodeBox(i));
                    }
                    return Task.CompletedTask;
                }
                if (string.Concat(Enumerable.Range(0, OpenbankConnector.AccessCodeLength).Select(i => s.Filled.GetValueOrDefault(OpenbankConnector.AccessCodeBox(i))))
                    != FakeOpenbank.Password) return Task.CompletedTask;
                lock (s.Visible) s.Visible.Clear();
                foreach (var c in bank.SignIn(withTokens)) s.SetCookie(c);
                s.Url = new Uri(Dashboard);
                return Task.CompletedTask;
            };
            return Task.CompletedTask;
        },
    };

    private static async Task<Rig> CreateAsync(Action<FakeOpenbank>? setup = null, string password = FakeOpenbank.Password, int pageSize = 3,
        bool withTokens = true, WebLoginOptions? webLogin = null)
    {
        var bank = new FakeOpenbank();
        setup?.Invoke(bank);
        var logs = new CapturingLoggerFactory();
        var browser = SignInPage(bank, withTokens);
        var world = await BankWorld.CreateAsync(extras: new BankWorldExtras
        {
            Registrations = [OpenbankConnector.CreateRegistration(Options(pageSize))],
            HttpFactory = bank.CreateClient,
            Loggers = logs,
            BrowserHost = browser,
            WebLoginOptions = webLogin,
        });
        var view = await world.For(BankWorld.Alice).CreateLoginAsync(OpenbankConnector.TypeName, "Openbank", FakeOpenbank.Username, password);
        return new Rig { World = world, Bank = bank, Logs = logs, Browser = browser, LoginId = view.Id };
    }

    private static Task<BankSyncStatus> SyncAsync(Rig rig, bool refresh = true) =>
        rig.World.SyncAsync(BankWorld.Alice, rig.LoginId, new SyncRequest(rig.LoginId, refresh, false));

    private static async Task<long> LinkCardAsync(Rig rig)
    {
        Assert.True((await SyncAsync(rig, false)).Succeeded);
        foreach (var a in await rig.Service.GetAccountsAsync(rig.LoginId)) await rig.Service.CreateAccountAsync(a.Id);
        return (await rig.World.RemoteAsync(BankWorld.Alice, rig.LoginId, FakeOpenbank.CardId)).LinkedAccountId!.Value;
    }

    [Fact]
    public async Task Web_login_then_accounts_balance_booked_and_pending_transactions()
    {
        await using var rig = await CreateAsync();
        var status = await SyncAsync(rig, false);
        Assert.True(status.Succeeded, status.Error);

        var session = Assert.Single(rig.Browser.Sessions);
        Assert.Equal(new Uri("https://kunden.openbankpay.com/amazon/login"), session.StartedAt);
        Assert.Equal(FakeOpenbank.Username, session.Filled[OpenbankConnector.UsernameSelector]);
        for (var i = 0; i < 4; i++) Assert.Equal(FakeOpenbank.Password[i].ToString(), session.Filled[OpenbankConnector.AccessCodeBox(i)]); // one digit per box
        Assert.Equal([OpenbankConnector.ContinueSelector, OpenbankConnector.SubmitSelector], session.Clicked);
        Assert.DoesNotContain(session.Clicked, c => c.Contains("checkbox", StringComparison.Ordinal)); // "Merken" stays unticked

        var accounts = await rig.Service.GetAccountsAsync(rig.LoginId);
        Assert.Equal(2, accounts.Count);
        var card = accounts.Single(a => a.RemoteId == FakeOpenbank.CardId);
        Assert.Equal("Example Card ••0001", card.Name);
        Assert.Equal(RemoteAccountType.CreditCard, card.Type);
        Assert.Equal(FakeOpenbank.Iban, card.Iban);
        Assert.Equal("Alice's second card", accounts.Single(a => a.RemoteId == FakeOpenbank.AddonCardId).Name);

        foreach (var a in accounts) await rig.Service.CreateAccountAsync(a.Id);
        Assert.True((await SyncAsync(rig)).Succeeded);
        var cardId = (await rig.World.RemoteAsync(BankWorld.Alice, rig.LoginId, FakeOpenbank.CardId)).LinkedAccountId!.Value;
        Assert.Equal(-312.40m, (await rig.World.AccountAsync(cardId)).CurrentBalance);

        var txs = await rig.World.TransactionsAsync(cardId);
        Assert.Equal(6, txs.Count); // the declined authorization is left out
        Assert.DoesNotContain(txs, t => t.Name == "Declined Shop");
        var pending = Assert.Single(txs, t => !t.Booked);
        Assert.Equal("Example Streaming", pending.Name);
        Assert.Equal(-1299, pending.Amount);
        Assert.Equal(25000, txs.Single(t => t.Name == "Kartenabrechnung").Amount);
        Assert.Contains("10.99 USD", txs.Single(t => t.Name == "Example Cloud").Purpose, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_sign_in_that_does_not_complete_is_not_reported_as_a_wrong_password()
    {
        await using var rig = await CreateAsync(password: "wrong",
            webLogin: new WebLoginOptions { PollInterval = TimeSpan.FromMilliseconds(10), SettleDelay = TimeSpan.Zero, Timeout = TimeSpan.FromMilliseconds(500) });
        var status = await SyncAsync(rig, false);
        Assert.False(status.Succeeded);
        Assert.DoesNotContain("username or password", status.Error, StringComparison.Ordinal);
        Assert.Equal(0, rig.Bank.Count("/cb-api-global-position"));
    }

    [Fact]
    public async Task An_access_code_of_the_wrong_length_is_not_entered_and_the_page_is_shown()
    {
        await using var rig = await CreateAsync(password: "12345",
            webLogin: new WebLoginOptions { PollInterval = TimeSpan.FromMilliseconds(10), SettleDelay = TimeSpan.Zero, Timeout = TimeSpan.FromSeconds(3) });
        var status = await SyncAsync(rig, false);
        Assert.False(status.Succeeded);
        var session = Assert.Single(rig.Browser.Sessions);
        Assert.Equal([OpenbankConnector.ContinueSelector], session.Clicked); // the code was never submitted
        Assert.DoesNotContain(session.Filled.Keys, k => k.Contains("gap", StringComparison.Ordinal));
        Assert.Contains("access code length doesn't match the form", rig.Logs.All, StringComparison.Ordinal);
        Assert.DoesNotContain("12345", rig.Logs.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_portal_page_without_token_cookies_fails_the_sign_in()
    {
        var bank = new FakeOpenbank();
        using var http = bank.CreateClient();
        var connector = new OpenbankConnector(new ConnectorContext(http, TimeProvider.System, new CapturingLoggerFactory().CreateLogger("t")), Options());
        var credentials = new ConnectorCredentials(FakeOpenbank.Username, FakeOpenbank.Password);
        var ask = await connector.LoginAsync(credentials, null, null, CancellationToken.None);
        Assert.Equal(StepStatus.Challenge, ask.Status);
        http.ImportCookies(bank.SignIn(withTokens: false));
        var done = await connector.LoginAsync(credentials, null, new ChallengeAnswer(ask.Challenge!, null), CancellationToken.None);
        Assert.Equal(StepStatus.Failed, done.Status);
        Assert.False(done.CredentialsRejected);
        Assert.Contains("did not complete", done.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public void The_web_login_counts_as_done_only_on_a_portal_page_with_an_access_token()
    {
        var bank = new FakeOpenbank();
        using var http = bank.CreateClient();
        var connector = new OpenbankConnector(new ConnectorContext(http, TimeProvider.System, new CapturingLoggerFactory().CreateLogger("t")), Options());
        var spec = connector.WebLogin(new ConnectorCredentials(FakeOpenbank.Username, FakeOpenbank.Password));
        var cookies = bank.SignIn();
        Assert.True(spec.IsSuccess(new Uri(Dashboard), cookies));
        Assert.True(spec.IsSuccess(new Uri("https://kunden.openbankpay.com/amazon/cards/x"), cookies));
        Assert.False(spec.IsSuccess(new Uri("https://kunden.openbankpay.com/amazon/login"), cookies));
        Assert.False(spec.IsSuccess(new Uri("https://login.openbankpay.com/protocol/openid-connect/auth"), cookies));
        Assert.False(spec.IsSuccess(new Uri(Dashboard), bank.SignIn(withTokens: false)));
        Assert.Contains("login.openbankpay.com", spec.CookieDomains);
    }

    [Fact]
    public async Task The_stored_refresh_token_is_reused_and_an_ended_session_signs_in_again()
    {
        await using var rig = await CreateAsync();
        Assert.True((await SyncAsync(rig, false)).Succeeded);
        Assert.Single(rig.Browser.Sessions);

        // Second sync: no web login; the refresh token buys a new access token.
        var second = await SyncAsync(rig, false);
        Assert.True(second.Succeeded, second.Error);
        Assert.Single(rig.Browser.Sessions);
        Assert.Equal(1, rig.Bank.Count("/refresh-token"));
        Assert.Contains(rig.Logs.Entries, e => e.Message.Contains("Reusing the stored Openbank session", StringComparison.Ordinal));

        // Openbank ends the session: the next sync signs in on the web page again.
        rig.Bank.ExpireSessions();
        var third = await SyncAsync(rig, false);
        Assert.True(third.Succeeded, third.Error);
        Assert.Equal(2, rig.Browser.Sessions.Count);
    }

    [Fact]
    public async Task An_access_token_that_runs_out_mid_sync_is_refreshed_once()
    {
        var bank = new FakeOpenbank();
        var logs = new CapturingLoggerFactory();
        using var http = bank.CreateClient();
        var connector = new OpenbankConnector(new ConnectorContext(http, TimeProvider.System, logs.CreateLogger("t")), Options());
        var credentials = new ConnectorCredentials(FakeOpenbank.Username, FakeOpenbank.Password);
        var ask = await connector.LoginAsync(credentials, null, null, CancellationToken.None);
        http.ImportCookies(bank.SignIn());
        var login = await connector.LoginAsync(credentials, null, new ChallengeAnswer(ask.Challenge!, null), CancellationToken.None);
        Assert.Equal(StepStatus.Done, login.Status);

        bank.ExpireAccessTokens();
        var accounts = await connector.ListAccountsAsync(CancellationToken.None);
        Assert.Equal(2, accounts.Count);
        Assert.Equal(1, bank.Count("/refresh-token"));

        bank.ExpireSessions();
        var ex = await Assert.ThrowsAsync<ConnectorException>(() => connector.RefreshAsync(accounts[1], null, null, CancellationToken.None));
        Assert.Contains("session", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Transactions_are_paged_until_since_is_reached()
    {
        await using var rig = await CreateAsync();
        var cardId = await LinkCardAsync(rig);
        rig.Bank.Requests.Clear();
        Assert.True((await SyncAsync(rig)).Succeeded);
        // A first sync reads everything: 7 transactions at 3 per page.
        Assert.Equal(1, rig.Bank.Count($"{FakeOpenbank.CardId}/transactions?limit=3&epp=true&offset=3"));
        Assert.Equal(1, rig.Bank.Count("epp=true&offset=6"));
        Assert.Equal(0, rig.Bank.Count("offset=9"));
        Assert.Equal(6, (await rig.World.TransactionsAsync(cardId)).Count);

        // Later syncs stop once a page reaches back past the start date.
        rig.Bank.Requests.Clear();
        Assert.True((await SyncAsync(rig)).Succeeded);
        Assert.Equal(0, rig.Bank.Count("offset=6"));
        Assert.Equal(6, (await rig.World.TransactionsAsync(cardId)).Count);
    }

    [Fact]
    public async Task Without_paging_support_the_first_page_is_kept()
    {
        await using var rig = await CreateAsync(b => b.SupportsOffset = false);
        var cardId = await LinkCardAsync(rig);
        var status = await SyncAsync(rig);
        Assert.True(status.Succeeded, status.Error);
        Assert.Equal(2, (await rig.World.TransactionsAsync(cardId)).Count); // page one: pending, declined (skipped), one booked
        Assert.Contains(rig.Logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("further page", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_server_error_fails_the_sync_with_a_readable_reason()
    {
        await using var rig = await CreateAsync(b => b.GlobalPositionFailure = HttpStatusCode.InternalServerError);
        var status = await SyncAsync(rig, false);
        Assert.False(status.Succeeded);
        Assert.Contains("HTTP 500", status.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nothing_secret_is_logged()
    {
        await using var rig = await CreateAsync();
        await LinkCardAsync(rig);
        Assert.True((await SyncAsync(rig)).Succeeded);
        var logs = rig.Logs.All;
        Assert.NotEmpty(logs);
        foreach (var secret in new[] { FakeOpenbank.TokenMarker, FakeOpenbank.Password, FakeOpenbank.CardId, FakeOpenbank.Iban })
            Assert.DoesNotContain(secret, logs, StringComparison.Ordinal);
    }
}
