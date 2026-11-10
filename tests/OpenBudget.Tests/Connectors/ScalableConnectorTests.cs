#pragma warning disable CA2000 // test objects live for the test
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenBudget.Connectors;
using OpenBudget.Connectors.Scalable;
using OpenBudget.Connectors.Sync;
using OpenBudget.Models;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests.Connectors;

/// <summary>The Scalable connector end to end through the sync framework, against a made-up Scalable (no network).</summary>
public sealed class ScalableConnectorTests
{
    private const string Depot = FakeScalable.PortfolioId + ":depot";
    private const string Cash = FakeScalable.PortfolioId + ":cash";

    private sealed class Rig : IAsyncDisposable
    {
        public required BankWorld World { get; init; }

        public required FakeScalable Bank { get; init; }

        public required CapturingLoggerFactory Logs { get; init; }

        public required FakeBrowserHost Browser { get; init; }

        public int LoginId { get; set; }

        public ValueTask DisposeAsync() => World.DisposeAsync();

        public BankLoginService Service => World.For(BankWorld.Alice);
    }

    private static async Task<Rig> CreateAsync()
    {
        var bank = new FakeScalable();
        var logs = new CapturingLoggerFactory();
        // The "user" signs in on Scalable's page: a session starts at Scalable and its cookie lands in the browser.
        var browser = new FakeBrowserHost
        {
            User = session =>
            {
                session.SetCookie(bank.SignIn());
                session.Url = new Uri("https://de.scalable.capital/cockpit/");
                return Task.CompletedTask;
            },
        };
        var world = await BankWorld.CreateAsync(extras: new BankWorldExtras
        {
            Registrations = [ScalableConnector.CreateRegistration(new ScalableOptions())],
            HttpFactory = bank.CreateClient,
            Loggers = logs,
            BrowserHost = browser,
        });
        var view = await world.For(BankWorld.Alice).CreateLoginAsync(ScalableConnector.TypeName, "Scalable", "", "");
        return new Rig { World = world, Bank = bank, Logs = logs, Browser = browser, LoginId = view.Id };
    }

    private static Task<BankSyncStatus> SyncAsync(Rig rig, SyncRequest? request = null) =>
        rig.World.SyncAsync(BankWorld.Alice, rig.LoginId, request ?? new SyncRequest(rig.LoginId, true, false));

    private static async Task LinkAllAsync(Rig rig)
    {
        Assert.True((await SyncAsync(rig, new SyncRequest(rig.LoginId, false, false))).Succeeded);
        foreach (var a in await rig.Service.GetAccountsAsync(rig.LoginId)) await rig.Service.CreateAccountAsync(a.Id);
    }

    private static async Task<long> LinkedAsync(Rig rig, string remoteId) =>
        (await rig.World.RemoteAsync(BankWorld.Alice, rig.LoginId, remoteId)).LinkedAccountId!.Value;

    // ---- the happy path ----

    [Fact]
    public async Task Web_login_then_a_complete_sync()
    {
        await using var rig = await CreateAsync();
        var status = await SyncAsync(rig, new SyncRequest(rig.LoginId, false, false));
        Assert.True(status.Succeeded, status.Error);

        var accounts = await rig.Service.GetAccountsAsync(rig.LoginId);
        var depot = accounts.Single(a => a.RemoteId == Depot);
        var cash = accounts.Single(a => a.RemoteId == Cash);
        Assert.Equal("Alice Portfolio Depot", depot.Name);
        Assert.Equal(RemoteAccountType.Depot, depot.Type);
        Assert.Equal("Alice Portfolio Verrechnungskonto", cash.Name);
        Assert.Equal(FakeScalable.Iban, cash.Iban);
        Assert.Equal(RemoteAccountType.Giro, cash.Type);

        // The user signed in once, on Scalable's login page in the (fake) browser, which was destroyed afterwards.
        var browser = Assert.Single(rig.Browser.Sessions);
        Assert.Equal("https://de.scalable.capital/auth/login", browser.StartedAt!.ToString());
        Assert.True(browser.Disposed);

        await LinkAllAsync(rig);
        Assert.True((await SyncAsync(rig)).Succeeded);

        var cashId = await LinkedAsync(rig, Cash);
        Assert.Equal(1234.56m, (await rig.World.AccountAsync(cashId)).CurrentBalance);
        var txs = await rig.World.TransactionsAsync(cashId);
        Assert.Equal(7, txs.Count);
        Assert.Equal(7, txs.Select(t => t.Id).Distinct().Count());

        var deposit = txs.Single(t => t.Name == "Deposit from Alice");
        Assert.Equal(50000, deposit.Amount);
        var buy = txs.Single(t => t.Name == "Example ETF");
        Assert.Equal(-5000, buy.Amount); // security buys leave the cash account
        Assert.Contains("BUY", buy.Purpose, StringComparison.Ordinal);
        Assert.Contains("XX0000000001", buy.Purpose, StringComparison.Ordinal);
        Assert.Contains("2 x", buy.Purpose, StringComparison.Ordinal);
        Assert.Contains("Storno", txs.Single(t => t.Name == "Cancelled buy").Purpose, StringComparison.Ordinal);
        Assert.False(txs.Single(t => t.Name == "Savings plan Example ETF").Booked);

        var depotId = await LinkedAsync(rig, Depot);
        await using var db = await rig.World.T.System.CreateDbContextAsync();
        var holdings = await db.Holdings.AsNoTracking().Where(h => h.AccountId == depotId).OrderBy(h => h.Isin).ToListAsync();
        Assert.Equal(["XX0000000001", "XX0000000002", "XX0000000003"], holdings.Select(h => h.Isin ?? "").ToArray()); // sold-out position skipped, ETP mapped like a security
        Assert.Equal(10.5, holdings[0].Quantity);
        Assert.Equal(100.25, holdings[0].Price);
        Assert.Equal(90, holdings[0].Purchase);
        Assert.Equal("Example Tracker ETP", holdings[2].Name);
        Assert.Equal(1052.63m + 200 + 50, (await rig.World.AccountAsync(depotId)).CurrentBalance!.Value);
    }

    // ---- sessions ----

    [Fact]
    public async Task A_stored_session_is_reused_without_a_web_login()
    {
        await using var rig = await CreateAsync();
        Assert.True((await SyncAsync(rig, new SyncRequest(rig.LoginId, false, false))).Succeeded);
        Assert.Single(rig.Browser.Sessions);

        var second = await SyncAsync(rig, new SyncRequest(rig.LoginId, false, false));
        Assert.True(second.Succeeded, second.Error);
        Assert.Single(rig.Browser.Sessions); // no second sign-in
        Assert.Contains(rig.Logs.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("Reusing the stored Scalable session", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_expired_session_opens_a_web_login_seeded_with_the_stored_cookies_and_logs_how_long_it_lasted()
    {
        await using var rig = await CreateAsync();
        Assert.True((await SyncAsync(rig, new SyncRequest(rig.LoginId, false, false))).Succeeded);

        rig.World.Clock.Offset += TimeSpan.FromHours(30);
        rig.Bank.ExpireSessions();
        var second = await SyncAsync(rig, new SyncRequest(rig.LoginId, false, false));
        Assert.True(second.Succeeded, second.Error);
        Assert.Equal(2, rig.Browser.Sessions.Count);
        // The stale app session must not travel into the browser, or the login page would jump straight to the cockpit.
        Assert.DoesNotContain(rig.Browser.Sessions[1].Seeded, c => c.Name == FakeScalable.SessionCookie);
        var expired = Assert.Single(rig.Logs.Entries, e => e.Message.Contains("has expired", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Information, expired.Level);
        Assert.Contains("30", expired.Message, StringComparison.Ordinal); // "created 30.0 hours ago"

        // The new session is the one stored.
        Assert.True((await SyncAsync(rig, new SyncRequest(rig.LoginId, false, false))).Succeeded);
        Assert.Equal(2, rig.Browser.Sessions.Count);
    }

    [Fact]
    public async Task A_session_whose_api_token_expired_opens_a_web_login_even_though_the_cockpit_page_still_renders()
    {
        await using var rig = await CreateAsync();
        Assert.True((await SyncAsync(rig, new SyncRequest(rig.LoginId, false, false))).Succeeded);

        rig.Bank.ExpireApiTokens();
        var second = await SyncAsync(rig, new SyncRequest(rig.LoginId, false, false));
        Assert.True(second.Succeeded, second.Error);
        Assert.Equal(2, rig.Browser.Sessions.Count);
    }

    [Fact]
    public async Task Nothing_secret_is_logged()
    {
        await using var rig = await CreateAsync();
        await LinkAllAsync(rig);
        Assert.True((await SyncAsync(rig, new SyncRequest(rig.LoginId, true, true))).Succeeded);

        var logs = rig.Logs.All;
        Assert.NotEmpty(logs);
        foreach (var secret in new[] { "secretvalue", FakeScalable.PersonId, FakeScalable.Iban })
            Assert.DoesNotContain(secret, logs, StringComparison.Ordinal);
    }

    // ---- bookings ----

    [Fact]
    public async Task Paging_stops_at_since_and_unknown_statuses_are_logged_once_as_booked()
    {
        await using var rig = await CreateAsync();
        await LinkAllAsync(rig);
        Assert.True((await SyncAsync(rig)).Succeeded);
        Assert.Equal(["(first)", "c1", "c2", "c3"], rig.Bank.Cursors); // a first sync reads everything

        var cashId = await LinkedAsync(rig, Cash);
        var odd = (await rig.World.TransactionsAsync(cashId)).Single(t => t.Name == "Odd one");
        Assert.True(odd.Booked);
        Assert.Equal(1, rig.Logs.Entries.Count(e => e.Message.Contains("WEIRD_STATE", StringComparison.Ordinal)));

        rig.Bank.Cursors.Clear();
        Assert.True((await SyncAsync(rig)).Succeeded);
        // since = newest booked day (2026-11-05) minus a week: the third page reaches back past it, the fourth is never asked for.
        Assert.Equal(["(first)", "c1", "c2"], rig.Bank.Cursors);
        Assert.Equal(7, (await rig.World.TransactionsAsync(cashId)).Count);
    }

    [Fact]
    public async Task Pending_bookings_are_replaced_on_every_sync()
    {
        await using var rig = await CreateAsync();
        await LinkAllAsync(rig);
        Assert.True((await SyncAsync(rig)).Succeeded);
        rig.Bank.Transactions = FakeScalable.DefaultTransactions();
        rig.Bank.Transactions[2]["status"] = "SETTLED"; // the savings plan executed
        Assert.True((await SyncAsync(rig)).Succeeded);

        var txs = await rig.World.TransactionsAsync(await LinkedAsync(rig, Cash));
        var plan = Assert.Single(txs, t => t.Name == "Savings plan Example ETF");
        Assert.True(plan.Booked);
    }

    // ---- statements ----

    [Fact]
    public async Task Statements_are_listed_downloaded_once_and_account_less_ones_come_along()
    {
        await using var rig = await CreateAsync();
        await LinkAllAsync(rig);
        var depot = await rig.World.RemoteAsync(BankWorld.Alice, rig.LoginId, Depot);
        await rig.Service.SetStatementsAsync(depot.Id, true, StatementsInterval.ManualOnly);

        var status = await SyncAsync(rig, new SyncRequest(rig.LoginId, false, true));
        Assert.True(status.Succeeded, status.Error);

        var list = await rig.Service.GetLoginStatementsAsync(rig.LoginId);
        Assert.Equal(2, list.Count); // D3 belongs to a portfolio that is not wanted
        Assert.Single(list, s => s.AccountId == null && s.Name == "Tax certificate");
        Assert.Single(list, s => s.AccountId == depot.LinkedAccountId && s.Name == "Securities statement October");
        Assert.Equal(1, rig.Bank.Downloads["D1"]);
        Assert.Equal(1, rig.Bank.Downloads["D2"]);
        Assert.DoesNotContain("D3", rig.Bank.Downloads.Keys, StringComparer.Ordinal);
        var files = Directory.GetFiles(rig.World.StatementsDir, "*.pdf", SearchOption.AllDirectories);
        Assert.Equal(2, files.Length);
        Assert.All(files, f => Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(f), 0, 4), StringComparison.Ordinal));

        // A second run (with a new document) downloads only the new one.
        rig.Bank.Documents.Add(FakeScalable.Doc("D4", "2026-11-08T00:00:00Z", "Newer statement", FakeScalable.PortfolioId));
        Assert.True((await SyncAsync(rig, new SyncRequest(rig.LoginId, false, true))).Succeeded);
        Assert.Equal(1, rig.Bank.Downloads["D1"]);
        Assert.Equal(1, rig.Bank.Downloads["D4"]);
        Assert.Equal(3, (await rig.Service.GetLoginStatementsAsync(rig.LoginId)).Count);
    }

    [Fact]
    public async Task Statements_of_the_cash_account_are_used_when_only_it_wants_them()
    {
        await using var rig = await CreateAsync();
        await LinkAllAsync(rig);
        var cash = await rig.World.RemoteAsync(BankWorld.Alice, rig.LoginId, Cash);
        await rig.Service.SetStatementsAsync(cash.Id, true, StatementsInterval.ManualOnly);
        Assert.True((await SyncAsync(rig, new SyncRequest(rig.LoginId, false, true))).Succeeded);
        Assert.Single(await rig.Service.GetAccountStatementsAsync(cash.LinkedAccountId!.Value));
    }

    // ---- pieces ----

    [Fact]
    public void The_connector_is_registered_with_its_display_name()
    {
        var registration = ScalableConnector.CreateRegistration(new ScalableOptions());
        Assert.Equal("scalable", registration.ConnectorType);
        Assert.Equal("Scalable Capital", registration.DisplayName);
        Assert.Equal(CredentialRequirement.None, registration.Credentials);
    }

    [Fact]
    public void The_user_id_is_read_from_the_cockpit_pages_next_data()
    {
        const string page = """
            <html><head><script>window.x = 1;</script></head><body>
            <script id="__NEXT_DATA__" type="application/json">{"props":{"pageProps":{"nonce":"n","middlewareProps":{"m0":{"flag":true},"m7":{"session":{"user":{"userId":"user-0001"}}}}}},"page":"/"}</script>
            </body></html>
            """;
        Assert.Equal("user-0001", ScalableConnector.ExtractUserId(page));

        // No signed-in user, no Next.js data, or broken JSON: no id (the caller treats that as an expired session).
        Assert.Null(ScalableConnector.ExtractUserId("""<script id="__NEXT_DATA__" type="application/json">{"props":{"pageProps":{"middlewareProps":{"m1":{"session":null}}}}}</script>"""));
        Assert.Null(ScalableConnector.ExtractUserId("<html><body>Sign in</body></html>"));
        Assert.Null(ScalableConnector.ExtractUserId("""<script id="__NEXT_DATA__">{not json</script>"""));
    }
}
