#pragma warning disable CA2000 // test objects live for the test
using System.Net;
using Microsoft.Extensions.Logging;
using OpenBudget.Connectors;
using OpenBudget.Connectors.Openbank;
using OpenBudget.Connectors.Sync;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests.Connectors;

/// <summary>The Openbank connector through the sync framework, against a made-up Openbank API (no network).</summary>
public sealed class OpenbankConnectorTests
{
    private sealed class Rig : IAsyncDisposable
    {
        public required BankWorld World { get; init; }

        public required FakeOpenbank Bank { get; init; }

        public required CapturingLoggerFactory Logs { get; init; }

        public int LoginId { get; set; }

        public ValueTask DisposeAsync() => World.DisposeAsync();

        public BankLoginService Service => World.For(BankWorld.Alice);
    }

    private static OpenbankOptions Options(int pageSize = 3) => new() { PageSize = pageSize, ApprovalPollInterval = TimeSpan.FromMilliseconds(20) };

    private static async Task<Rig> CreateAsync(Action<FakeOpenbank>? setup = null, string password = FakeOpenbank.Password, int pageSize = 3)
    {
        var bank = new FakeOpenbank();
        setup?.Invoke(bank);
        var logs = new CapturingLoggerFactory();
        var world = await BankWorld.CreateAsync(extras: new BankWorldExtras
        {
            Registrations = [OpenbankConnector.CreateRegistration(Options(pageSize))],
            HttpFactory = bank.CreateClient,
            Loggers = logs,
        });
        var view = await world.For(BankWorld.Alice).CreateLoginAsync(OpenbankConnector.TypeName, "Openbank", FakeOpenbank.Username, password);
        return new Rig { World = world, Bank = bank, Logs = logs, LoginId = view.Id };
    }

    private static Task<BankSyncStatus> SyncAsync(Rig rig, bool refresh = true, params string[] answers) =>
        rig.World.SyncAsync(BankWorld.Alice, rig.LoginId, new SyncRequest(rig.LoginId, refresh, false), answers);

    private static async Task<long> LinkCardAsync(Rig rig)
    {
        Assert.True((await SyncAsync(rig, false, FakeOpenbank.Otp)).Succeeded);
        foreach (var a in await rig.Service.GetAccountsAsync(rig.LoginId)) await rig.Service.CreateAccountAsync(a.Id);
        return (await rig.World.RemoteAsync(BankWorld.Alice, rig.LoginId, FakeOpenbank.CardId)).LinkedAccountId!.Value;
    }

    [Fact]
    public async Task Sms_code_login_then_accounts_balance_booked_and_pending_transactions()
    {
        await using var rig = await CreateAsync();
        var status = await SyncAsync(rig, false, FakeOpenbank.Otp);
        Assert.True(status.Succeeded, status.Error);

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
    public async Task App_approval_login_is_polled_until_confirmed()
    {
        await using var rig = await CreateAsync(b => { b.SecondFactor = "PUSH"; b.ApprovalPolls = 3; });
        var status = await SyncAsync(rig, false);
        Assert.True(status.Succeeded, status.Error);
        Assert.Equal(3, rig.Bank.Count("/challenges/"));
    }

    [Fact]
    public async Task A_rejected_approval_fails_the_sync()
    {
        await using var rig = await CreateAsync(b => { b.SecondFactor = "PUSH"; b.RejectApproval = true; });
        var status = await SyncAsync(rig, false);
        Assert.False(status.Succeeded);
        Assert.Contains("rejected", status.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_wrong_password_is_reported_and_no_code_is_asked_for()
    {
        await using var rig = await CreateAsync(password: "wrong");
        var status = await SyncAsync(rig, false);
        Assert.False(status.Succeeded);
        Assert.Contains("username or password", status.Error, StringComparison.Ordinal);
        Assert.Equal(0, rig.Bank.Count("/otp"));
    }

    [Fact]
    public async Task A_wrong_code_fails_the_login()
    {
        await using var rig = await CreateAsync();
        var status = await SyncAsync(rig, false, "000000");
        Assert.False(status.Succeeded);
        Assert.Contains("code", status.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_locked_access_is_reported()
    {
        await using var rig = await CreateAsync(b => b.LockedAccount = true);
        var status = await SyncAsync(rig, false);
        Assert.False(status.Succeeded);
        Assert.Contains("locked", status.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_stored_refresh_token_is_reused_and_an_ended_session_signs_in_again()
    {
        await using var rig = await CreateAsync();
        Assert.True((await SyncAsync(rig, false, FakeOpenbank.Otp)).Succeeded);
        Assert.Equal(1, rig.Bank.Count("/authentication/login"));

        // Second sync: no sign-in, no code; the refresh token buys a new access token.
        var second = await SyncAsync(rig, false);
        Assert.True(second.Succeeded, second.Error);
        Assert.Equal(1, rig.Bank.Count("/authentication/login"));
        Assert.Equal(1, rig.Bank.Count("/refresh-token"));
        Assert.Contains(rig.Logs.Entries, e => e.Message.Contains("Reusing the stored Openbank session", StringComparison.Ordinal));

        // Openbank ends the session: the next sync signs in again with a code.
        rig.Bank.ExpireSessions();
        var third = await SyncAsync(rig, false, FakeOpenbank.Otp);
        Assert.True(third.Succeeded, third.Error);
        Assert.Equal(2, rig.Bank.Count("/authentication/login"));
    }

    [Fact]
    public async Task An_access_token_that_runs_out_mid_sync_is_refreshed_once()
    {
        var bank = new FakeOpenbank { SecondFactor = null };
        var logs = new CapturingLoggerFactory();
        using var http = bank.CreateClient();
        var connector = new OpenbankConnector(new ConnectorContext(http, TimeProvider.System, logs.CreateLogger("t")), Options());
        var login = await connector.LoginAsync(new ConnectorCredentials(FakeOpenbank.Username, FakeOpenbank.Password), null, null, CancellationToken.None);
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
        var status = await SyncAsync(rig, false, FakeOpenbank.Otp);
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
        foreach (var secret in new[] { FakeOpenbank.TokenMarker, FakeOpenbank.Password, FakeOpenbank.Otp, FakeOpenbank.CardId, FakeOpenbank.Iban })
            Assert.DoesNotContain(secret, logs, StringComparison.Ordinal);
    }
}
