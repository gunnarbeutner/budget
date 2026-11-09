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

        public int LoginId { get; set; }

        public ValueTask DisposeAsync() => World.DisposeAsync();

        public BankLoginService Service => World.For(BankWorld.Alice);
    }

    private static async Task<Rig> CreateAsync(string password = FakeScalable.Password, Action<FakeScalable>? configure = null)
    {
        var bank = new FakeScalable();
        configure?.Invoke(bank);
        var logs = new CapturingLoggerFactory();
        var options = new ScalableOptions { PollInterval = TimeSpan.FromMilliseconds(10) };
        var world = await BankWorld.CreateAsync(extras: new BankWorldExtras
        {
            Registrations = [ScalableConnector.CreateRegistration(options)],
            HttpFactory = bank.CreateClient,
            Loggers = logs,
        });
        var view = await world.For(BankWorld.Alice).CreateLoginAsync(ScalableConnector.TypeName, "Scalable", FakeScalable.Username, password);
        return new Rig { World = world, Bank = bank, Logs = logs, LoginId = view.Id };
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
    public async Task Full_login_with_app_approval_then_a_complete_sync()
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

        // The framework's own flow was walked: form login, callback, 2FA polled until SUCCESS.
        Assert.Equal(1, rig.Bank.Count("POST secure.scalable.capital/u/login"));
        Assert.Equal(1, rig.Bank.Count("graphql start2faOnLogin"));
        Assert.Equal(4, rig.Bank.Count("graphql validate2faOnLogin")); // PENDING x3, SUCCESS

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
    public async Task A_stored_session_is_reused_without_login_or_two_factor()
    {
        await using var rig = await CreateAsync();
        Assert.True((await SyncAsync(rig, new SyncRequest(rig.LoginId, false, false))).Succeeded);
        Assert.Equal(1, rig.Bank.Count("POST secure.scalable.capital/u/login"));

        var second = await SyncAsync(rig, new SyncRequest(rig.LoginId, false, false));
        Assert.True(second.Succeeded, second.Error);
        Assert.Equal(1, rig.Bank.Count("POST secure.scalable.capital/u/login"));
        Assert.Equal(1, rig.Bank.Count("graphql start2faOnLogin"));
        Assert.Equal(1, rig.Bank.Count("GET de.scalable.capital/auth/login"));
        Assert.Contains(rig.Logs.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("Reusing the stored Scalable session", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_expired_session_logs_in_again_and_logs_how_long_it_lasted()
    {
        await using var rig = await CreateAsync();
        Assert.True((await SyncAsync(rig, new SyncRequest(rig.LoginId, false, false))).Succeeded);

        rig.World.Clock.Offset += TimeSpan.FromHours(30);
        rig.Bank.ExpireSessions();
        var second = await SyncAsync(rig, new SyncRequest(rig.LoginId, false, false));
        Assert.True(second.Succeeded, second.Error);
        Assert.Equal(2, rig.Bank.Count("POST secure.scalable.capital/u/login"));
        Assert.Equal(2, rig.Bank.Count("graphql start2faOnLogin"));
        var expired = Assert.Single(rig.Logs.Entries, e => e.Message.Contains("has expired", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Information, expired.Level);
        Assert.Contains("30", expired.Message, StringComparison.Ordinal); // "created 30.0 hours ago"

        // The new session is the one stored.
        Assert.True((await SyncAsync(rig, new SyncRequest(rig.LoginId, false, false))).Succeeded);
        Assert.Equal(2, rig.Bank.Count("POST secure.scalable.capital/u/login"));
    }

    [Fact]
    public async Task No_approval_is_asked_when_scalable_reports_an_approved_session()
    {
        await using var rig = await CreateAsync(configure: b => b.AlreadyApproved = true);
        var status = await SyncAsync(rig, new SyncRequest(rig.LoginId, false, false));
        Assert.True(status.Succeeded, status.Error);
        Assert.Equal(0, rig.Bank.Count("graphql start2faOnLogin"));
    }

    // ---- failures ----

    [Fact]
    public async Task A_wrong_password_is_reported_and_flags_the_credentials()
    {
        await using var rig = await CreateAsync(password: "not-the-password");
        var status = await SyncAsync(rig);
        Assert.False(status.Succeeded);
        Assert.Contains("rejected", status.Error, StringComparison.OrdinalIgnoreCase);
        Assert.True((await rig.Service.GetLoginAsync(rig.LoginId))!.CredentialsInvalid);
        Assert.Equal(0, rig.Bank.Count("graphql start2faOnLogin"));
        Assert.Equal(0, await rig.World.Coordinator.StartDueAsync(BankWorld.Alice));
    }

    [Fact]
    public async Task Graphql_errors_surface_in_the_last_error_and_the_warning_log()
    {
        await using var rig = await CreateAsync(configure: b => b.StartRejection = "Unknown type \"Start2faOnLoginInput\".");
        var status = await SyncAsync(rig);
        Assert.False(status.Succeeded);
        Assert.Contains("Unknown type \"Start2faOnLoginInput\"", status.Error, StringComparison.Ordinal);
        Assert.Contains("start2faOnLogin", status.Error, StringComparison.Ordinal);
        Assert.Contains("Unknown type", (await rig.Service.GetLoginAsync(rig.LoginId))!.LastError, StringComparison.Ordinal);
        Assert.Contains(rig.Logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("Unknown type", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sms_two_factor_is_reported_as_not_supported()
    {
        await using var rig = await CreateAsync(configure: b => b.Sms = true);
        var status = await SyncAsync(rig);
        Assert.False(status.Succeeded);
        Assert.Contains("SMS", status.Error, StringComparison.Ordinal);
        Assert.Contains("not supported", status.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nothing_secret_is_logged()
    {
        await using var rig = await CreateAsync();
        await LinkAllAsync(rig);
        Assert.True((await SyncAsync(rig, new SyncRequest(rig.LoginId, true, true))).Succeeded);

        var logs = rig.Logs.All;
        Assert.NotEmpty(logs);
        foreach (var secret in new[] { FakeScalable.Password, FakeScalable.Username, FakeScalable.MfaSessionId, "secretvalue", FakeScalable.PersonId, FakeScalable.Iban, "CODE1" })
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
    public void The_login_form_is_parsed_with_hidden_fields_button_and_resolved_action()
    {
        const string html = """
            <html><form action="/social" method="post"><input type="hidden" name="x" value="1"><button name="action" value="google">G</button></form>
            <form method="POST" action="/u/login?state=ab&amp;x=1">
              <input type="hidden" name="state" value="ab&amp;cd">
              <input type=email name='user-name' autofocus>
              <input type="password" name="pw">
              <button type="submit" name="action" value="default">Go</button>
            </form></html>
            """;
        var form = ScalableLoginForm.Parse(html, new Uri("https://secure.scalable.capital/u/login?state=ab"))!;
        Assert.Equal("https://secure.scalable.capital/u/login?state=ab&x=1", form.Action.ToString());
        Assert.Equal("user-name", form.UsernameField);
        Assert.Equal("pw", form.PasswordField);
        var fields = form.Fill("u", "p");
        Assert.Contains(new KeyValuePair<string, string>("state", "ab&cd"), fields);
        Assert.Contains(new KeyValuePair<string, string>("action", "default"), fields);
        Assert.Contains(new KeyValuePair<string, string>("user-name", "u"), fields);
        Assert.Contains(new KeyValuePair<string, string>("pw", "p"), fields);
        Assert.Null(ScalableLoginForm.Parse("<html>no form</html>", new Uri("https://x.invalid/")));
    }

    [Fact]
    public void Options_can_be_overridden_from_the_environment()
    {
        var options = ScalableOptions.FromVariables(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [ScalableOptions.Is2faInputVariable] = "{\"a\":1}",
            [ScalableOptions.Start2faQueryVariable] = "mutation x { y }",
            [ScalableOptions.Validate2faInputVariable] = "  ",
        });
        Assert.Equal("{\"a\":1}", options.Is2faEnabledInputJson);
        Assert.Equal("mutation x { y }", options.Start2faQuery);
        Assert.Equal(new ScalableOptions().Validate2faInputTemplate, options.Validate2faInputTemplate);
        Assert.Equal(new ScalableOptions().Start2faInputJson, options.Start2faInputJson);
    }

    [Fact]
    public async Task A_broken_input_setting_fails_with_a_clear_message()
    {
        var bank = new FakeScalable();
        var options = new ScalableOptions { Start2faInputJson = "[1,2" };
        await using var world = await BankWorld.CreateAsync(extras: new BankWorldExtras { Registrations = [ScalableConnector.CreateRegistration(options)], HttpFactory = bank.CreateClient });
        var view = await world.For(BankWorld.Alice).CreateLoginAsync(ScalableConnector.TypeName, "Scalable", FakeScalable.Username, FakeScalable.Password);
        var status = await world.SyncAsync(BankWorld.Alice, view.Id, new SyncRequest(view.Id, true, false));
        Assert.False(status.Succeeded);
        Assert.Contains("Start2faInputJson", status.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void The_connector_is_registered_with_its_display_name()
    {
        var registration = ScalableConnector.CreateRegistration(new ScalableOptions());
        Assert.Equal("scalable", registration.ConnectorType);
        Assert.Equal("Scalable Capital", registration.DisplayName);
    }

    [Theory]
    [InlineData("<script>self.__next_f.push([1,\"12:[\\\"$\\\",\\\"$L24\\\",null,{\\\"userId\\\":\\\"user-1\\\"}]\\n\"])</script>", "user-1")]
    [InlineData("{\"middlewareProps\":{\"session\":{\"user\":{\"userId\":\"user-2\"}}}}", "user-2")]
    [InlineData("<html>no id here</html>", null)]
    public void The_user_id_for_the_2fa_queries_is_read_from_the_mfa_check_page(string html, string? expected)
    {
        Assert.Equal(expected, OpenBudget.Connectors.Scalable.ScalableConnector.ExtractUserId(html));
    }
}
