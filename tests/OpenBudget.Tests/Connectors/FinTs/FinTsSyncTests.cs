#pragma warning disable CA2000 // test objects live for the test
using OpenBudget.Connectors;
using OpenBudget.Connectors.FinTs;
using OpenBudget.Connectors.Ing;
using OpenBudget.Connectors.Sync;
using Xunit;

namespace OpenBudget.Tests.Connectors.FinTs;

/// <summary>A FinTS bank inside the whole framework: coordinator, runner, stores, challenges, session persistence.</summary>
public sealed class FinTsSyncTests
{
    private const string Type = "fints-test";
    private const string Pin = "654321PIN";

    private static ConnectorRegistration Registration(MockFinTsServer server, FinTsOptions options)
    {
        var config = new FinTsBankConfig(Type, "FinTS test bank", "test", SyntheticBank.Blz, new Uri("https://bank.invalid/fints"));
        return FinTsConnector.Registration(config, options, _ => new FinTsHttpTransport(new BankHttpClient(server), config.Url));
    }

    private static (SyntheticBank Bank, MockFinTsServer Server) NewBank(Action<SyntheticBank>? setup = null)
    {
        var bank = new SyntheticBank { Pin = Pin };
        bank.UseCamt();
        bank.UseBalance();
        bank.Handlers["HKWPD"] = (s, rb, _) => rb.Segment(
            "HIWPD", 6, s.Number,
            SyntheticBank.Bin(":16R:GENL\r\n:16S:GENL\r\n:16R:FIN\r\n:35B:ISIN XX0000000001/DE/A0EXAM\r\nExample World ETF\r\n:90B::MRKT//ACTU/EUR100,25\r\n:93B::AGGR//UNIT/10,5\r\n:19A::HOLD//EUR1052,63\r\n:16S:FIN\r\n-"));
        setup?.Invoke(bank);
        return (bank, new MockFinTsServer(bank.Respond));
    }

    private static async Task<int> LoginAsync(BankWorld w, string pin = Pin)
    {
        var view = await w.For(BankWorld.Alice).CreateLoginAsync(Type, "FinTS", "testuser", pin);
        return view.Id;
    }

    [Fact]
    public async Task A_full_sync_stores_transactions_balance_and_depot_positions_and_remembers_the_session()
    {
        var (bank, server) = NewBank();
        await using var w = await BankWorld.CreateAsync(extras: new BankWorldExtras { Registrations = [Registration(server, FinTsHarness.Options)] });
        var id = await LoginAsync(w);

        var first = await w.SyncAsync(BankWorld.Alice, id, new SyncRequest(id, true, false));
        Assert.True(first.Succeeded, first.Error);
        Assert.Equal(1, bank.SyncCount);
        Assert.Equal(2, (await w.For(BankWorld.Alice).GetAccountsAsync(id)).Count);

        await w.LinkAllAsync(BankWorld.Alice, id);
        var second = await w.SyncAsync(BankWorld.Alice, id, new SyncRequest(id, true, false));
        Assert.True(second.Succeeded, second.Error);
        Assert.Equal(1, bank.SyncCount); // the customer system id is in the stored session now

        var giro = await w.RemoteAsync(BankWorld.Alice, id, SyntheticBank.Iban);
        var txs = await w.TransactionsAsync(giro.LinkedAccountId!.Value);
        Assert.Equal(2, txs.Count);
        Assert.Single(txs, t => t.Booked && t.Amount == -1230 && t.ExternalId == "id:REF1");
        Assert.Single(txs, t => !t.Booked && t.Amount == -400);
        Assert.Contains("1234.560000", (await w.AccountAsync(giro.LinkedAccountId.Value)).Balances, StringComparison.Ordinal);

        var depot = await w.RemoteAsync(BankWorld.Alice, id, SyntheticBank.DepotAccount);
        Assert.Equal(RemoteAccountType.Depot, depot.Type);
        await using var db = await w.T.System.CreateDbContextAsync();
        var holding = Assert.Single(db.Holdings.Where(h => h.AccountId == depot.LinkedAccountId));
        Assert.Equal("XX0000000001", holding.Isin);
        Assert.Equal(10.5, holding.Quantity);

        // A repeated sync does not duplicate anything.
        var third = await w.SyncAsync(BankWorld.Alice, id, new SyncRequest(id, true, false));
        Assert.True(third.Succeeded, third.Error);
        Assert.Equal(2, (await w.TransactionsAsync(giro.LinkedAccountId.Value)).Count);
        Assert.Equal(1, bank.SyncCount);
    }

    [Fact]
    public async Task Without_a_product_registration_the_sync_says_so_and_does_not_touch_the_bank()
    {
        var (_, server) = NewBank();
        await using var w = await BankWorld.CreateAsync(extras: new BankWorldExtras { Registrations = [Registration(server, new FinTsOptions(null, "1.0"))] });
        var id = await LoginAsync(w);

        var status = await w.SyncAsync(BankWorld.Alice, id);
        Assert.False(status.Succeeded);
        Assert.Contains("FinTS product registration not configured", status.Error, StringComparison.Ordinal);
        Assert.Empty(server.Requests);
        Assert.False((await w.For(BankWorld.Alice).GetLoginAsync(id))!.CredentialsInvalid);
    }

    [Fact]
    public async Task A_rejected_pin_marks_the_credentials_invalid()
    {
        var (_, server) = NewBank();
        await using var w = await BankWorld.CreateAsync(extras: new BankWorldExtras { Registrations = [Registration(server, FinTsHarness.Options)] });
        var id = await LoginAsync(w, "wrong-pin");

        var status = await w.SyncAsync(BankWorld.Alice, id);
        Assert.False(status.Succeeded);
        Assert.Contains("9931", status.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("wrong-pin", status.Error, StringComparison.Ordinal);
        Assert.True((await w.For(BankWorld.Alice).GetLoginAsync(id))!.CredentialsInvalid);
    }

    [Fact]
    public async Task The_tan_method_choice_reaches_the_user_and_is_remembered()
    {
        var (bank, server) = NewBank(b => b.AllowedMethods = ["930", "931"]);
        await using var w = await BankWorld.CreateAsync(extras: new BankWorldExtras { Registrations = [Registration(server, FinTsHarness.Options)] });
        var id = await LoginAsync(w);

        var status = await w.SyncAsync(BankWorld.Alice, id, null, "931");
        Assert.True(status.Succeeded, status.Error);
        Assert.Equal("931", server.Requests[^1].SecurityFunction);

        // The next sync does not ask again.
        var again = await w.SyncAsync(BankWorld.Alice, id);
        Assert.True(again.Succeeded, again.Error);
        Assert.Equal(1, bank.SyncCount);
    }

    [Fact]
    public async Task App_approval_is_polled_by_the_framework_until_the_bank_confirms()
    {
        var (bank, server) = NewBank(b => { b.AllowedMethods = ["940"]; b.InitTan = TanMode.Decoupled; b.PendingPolls = 2; });
        await using var w = await BankWorld.CreateAsync(extras: new BankWorldExtras { Registrations = [Registration(server, FinTsHarness.Options)] });
        var id = await LoginAsync(w);

        var status = await w.SyncAsync(BankWorld.Alice, id);
        Assert.True(status.Succeeded, status.Error);
        Assert.True(bank.PollCount >= 3);
    }

    [Fact]
    public async Task App_approval_that_never_comes_expires()
    {
        var (_, server) = NewBank(b => { b.InitTan = TanMode.Decoupled; b.NeverApprove = true; });
        await using var w = await BankWorld.CreateAsync(challengeTimeout: TimeSpan.FromMilliseconds(400), extras: new BankWorldExtras { Registrations = [Registration(server, FinTsHarness.Options)] });
        var id = await LoginAsync(w);

        var status = await w.SyncAsync(BankWorld.Alice, id);
        Assert.False(status.Succeeded);
        Assert.Contains("expired", status.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_real_registrations_are_offered_and_use_the_documented_bank_addresses()
    {
        var options = FinTsHarness.Options;
        var registrations = new[] { IngConnector.Registration(options), BaaderConnector.Registration(options), PsdNuernbergConnector.Registration(options) };
        Assert.Equal(["ing", "baader", "psd-nuernberg"], registrations.Select(r => r.ConnectorType));
        Assert.Equal(("50010517", "https://fints.ing.de/fints/"), (IngConnector.Config.Blz, IngConnector.Config.Url.ToString()));
        Assert.Equal(("70033100", "https://fints.baaderbank.de/"), (BaaderConnector.Config.Blz, BaaderConnector.Config.Url.ToString()));
        Assert.Equal(("76090900", "https://fints2.atruvia.de/cgi-bin/hbciservlet"), (PsdNuernbergConnector.Config.Blz, PsdNuernbergConnector.Config.Url.ToString()));
        Assert.True(PsdNuernbergConnector.Config.UpdInventory);
    }
}
