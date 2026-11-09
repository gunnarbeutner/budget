#pragma warning disable CA2000 // test harness: lifetimes are owned by the test
using OpenBudget.Connectors;
using OpenBudget.Connectors.FinTs;
using Xunit;

namespace OpenBudget.Tests.Connectors.FinTs;

/// <summary>
/// The client talks to a mock that replays the anonymized recordings of the real banks. Each message the client
/// sends must equal the recorded one segment by segment (versions, security function, data elements); only
/// timestamps, ids, references and secrets may differ. That is our evidence the real banks accept the client.
/// </summary>
public sealed class FinTsRecordedDialogTests
{
    private static async Task<T> DoneAsync<T>(Func<ChallengeAnswer?, Task<ConnectorStep<T>>> call)
    {
        var (step, challenges) = await FinTsHarness.DriveAsync(call);
        Assert.Empty(challenges);
        Assert.True(step.Status == StepStatus.Done, step.FailureReason);
        return step.Value!;
    }

    [Fact]
    public async Task Ing_dialog_matches_the_recording()
    {
        var fixture = FinTsFixture.Load("ing");
        var replay = new ReplayBank(fixture, ignoreBpdVersion: true);
        using var server = new MockFinTsServer(replay.Respond);
        var connector = new IngConnector(FinTsHarness.Context(server), FinTsHarness.Options);
        var credentials = new ConnectorCredentials("testuser2", "12345");

        await DoneAsync(a => connector.LoginAsync(credentials, fixture.SeedSession(), a, default));
        var accounts = await connector.ListAccountsAsync(default);
        Assert.Equal(3, accounts.Count);
        Assert.Equal(RemoteAccountType.Giro, accounts[0].Type);
        Assert.Equal(RemoteAccountType.Savings, accounts[1].Type);

        var refresh = await DoneAsync(a => connector.RefreshAsync(accounts[0], new DateOnly(2026, 11, 1), a, default));
        Assert.Equal(1234.56m, refresh.Balance);
        Assert.Equal(3, refresh.Transactions.Count);
        Assert.Equal(2, refresh.Transactions.Count(t => t.Booked));
        var pending = Assert.Single(refresh.Transactions, t => !t.Booked);
        Assert.Equal(-9.99m, pending.Amount);

        var session = await connector.LogoutAsync(default);
        Assert.NotNull(session);
        Assert.Equal(replay.Expected, replay.Served);
    }

    [Fact]
    public async Task Baader_dialog_with_hktan_matches_the_recording()
    {
        var fixture = FinTsFixture.Load("baader");
        var replay = new ReplayBank(fixture);
        using var server = new MockFinTsServer(replay.Respond);
        var connector = new BaaderConnector(FinTsHarness.Context(server), FinTsHarness.Options);
        var credentials = new ConnectorCredentials("9000008", "12345");

        await DoneAsync(a => connector.LoginAsync(credentials, fixture.SeedSession(), a, default));
        var accounts = await connector.ListAccountsAsync(default);
        Assert.Equal([RemoteAccountType.Giro, RemoteAccountType.Depot], accounts.Select(a => a.Type));

        var refresh = await DoneAsync(a => connector.RefreshAsync(accounts[0], new DateOnly(2026, 8, 12), a, default));
        Assert.Equal(1234.56m, refresh.Balance);
        Assert.Equal(2, refresh.Transactions.Count); // the booked document only; its pending entry is not a booked one
        await connector.LogoutAsync(default);
        Assert.Equal(replay.Expected, replay.Served);
    }

    [Fact]
    public async Task Psd_dialog_with_hktan_v7_matches_the_recording()
    {
        var fixture = FinTsFixture.Load("psd");
        var replay = new ReplayBank(fixture);
        using var server = new MockFinTsServer(replay.Respond);
        var connector = new PsdNuernbergConnector(FinTsHarness.Context(server), FinTsHarness.Options);
        var credentials = new ConnectorCredentials("9000015", "12345");

        // The recording was made with cached bank parameters (version 82); give the client the same.
        var session = fixture.SeedSession();
        session.Values[FinTsClient.BpdKey] =
            "HIBPA:3:3:4+82+280:76090900+Test Bank+0+1+300+0'"
            + "HIPINS:4:1:4+1+1+0+5:64:6:Alias::HKCAZ:N:HKSAL:N'"
            + "HITANS:5:7:4+1+1+0+N:N:0:" + SyntheticBank.MethodV7("946", "Test App", decoupled: true) + "'";

        await DoneAsync(a => connector.LoginAsync(credentials, session, a, default));
        var accounts = await connector.ListAccountsAsync(default);
        Assert.Equal(3, accounts.Count);
        await connector.LogoutAsync(default);
        Assert.Equal(replay.Expected, replay.Served);
    }

    [Fact]
    public async Task Login_keeps_parameters_system_id_and_tan_method_in_the_session()
    {
        var fixture = FinTsFixture.Load("baader");
        var replay = new ReplayBank(fixture);
        using var server = new MockFinTsServer(replay.Respond);
        var connector = new BaaderConnector(FinTsHarness.Context(server), FinTsHarness.Options);
        var login = await DoneAsync(a => connector.LoginAsync(new ConnectorCredentials("9000008", "12345"), fixture.SeedSession(), a, default));
        Assert.NotEmpty(login!.Values[FinTsClient.SystemIdKey]);
        Assert.Contains("HIBPA", login.Values[FinTsClient.BpdKey], StringComparison.Ordinal);
        Assert.Contains("HIUPD", login.Values[FinTsClient.UpdKey], StringComparison.Ordinal);
        Assert.Equal("930", login.Values[FinTsClient.TanMethodKey]);
    }
}
