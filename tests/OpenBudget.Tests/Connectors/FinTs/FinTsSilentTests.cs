#pragma warning disable CA2000 // test harness: lifetimes are owned by the test
using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using OpenBudget.Connectors;
using OpenBudget.Connectors.FinTs;
using Xunit;

namespace OpenBudget.Tests.Connectors.FinTs;

/// <summary>Background (silent) FinTS runs: the gate before the dialog, and nothing more sent once the bank wants a TAN.</summary>
public sealed class FinTsSilentTests
{
    private const string Pin = "SECRETPIN1";

    private sealed class Rig : IDisposable
    {
        private readonly FinTsBankConfig _config = new("test", "Test Bank", "test", SyntheticBank.Blz, new Uri("https://bank.invalid/fints"));

        public Rig(TanMode init = TanMode.None, TanMode job = TanMode.None)
        {
            Bank = new SyntheticBank { Pin = Pin, InitTan = init, JobTan = job };
            Bank.UseCamt();
            Server = new MockFinTsServer(Bank.Respond);
        }

        public SyntheticBank Bank { get; }

        public MockFinTsServer Server { get; }

        public TestClock Clock { get; } = new();

        public ConnectorCredentials Credentials { get; set; } = new("testuser", Pin);

        public IBankConnector Connector(bool silent) =>
            FinTsConnector.Registration(_config, FinTsHarness.Options)
                .Create(new ConnectorContext(new BankHttpClient(Server), Clock, NullLogger.Instance) { Silent = silent });

        /// <summary>A settled session: parameters, system id and a TAN method; <paramref name="lastSca"/> days ago, <paramref name="silentOk"/> as given.</summary>
        public ConnectorSession Seed(string tanMethod = "940", int? lastSca = 10, bool? silentOk = null)
        {
            var session = new ConnectorSession();
            session.Values[FinTsClient.SystemIdKey] = SyntheticBank.AssignedSystemId;
            session.Values[FinTsClient.TanMethodKey] = tanMethod;
            session.Values[FinTsClient.BpdKey] = Bank.BpdText;
            session.Values[FinTsClient.UpdKey] = Bank.UpdText;
            if (lastSca is { } days)
                session.Values[FinTsClient.LastScaKey] = Clock.GetUtcNow().UtcDateTime.AddDays(-days).ToString("O", CultureInfo.InvariantCulture);
            if (silentOk is { } ok) session.Values[FinTsClient.SilentOkKey] = ok ? "1" : "0";
            return session;
        }

        public void Dispose() => Server.Dispose();
    }

    private static bool SentTanOrPoll(MockRequest r) => r.Tan != null || r.Segments.Any(s => s.Name == "HKTAN" && s.Element(0) is "S" or "2");

    [Fact]
    public async Task Exempt_init_logs_in_silently_and_records_3076()
    {
        using var rig = new Rig();
        var connector = rig.Connector(silent: true);
        var step = await connector.LoginAsync(rig.Credentials, rig.Seed(silentOk: true), null, default);

        Assert.Equal(StepStatus.Done, step.Status);
        var session = await connector.LogoutAsync(default);
        Assert.Equal("1", session!.Values[FinTsClient.SilentOkKey]);
        Assert.Equal("3076", ((IBackgroundRunInfo)connector).FinTsCodes);
    }

    [Theory]
    [InlineData(false, "0030")]
    [InlineData(true, "3955")]
    public async Task Tan_at_init_ends_the_dialog_without_polling_or_a_tan_and_pauses_background_runs(bool decoupled, string code)
    {
        using var rig = new Rig(init: decoupled ? TanMode.Decoupled : TanMode.Code);
        var connector = rig.Connector(silent: true);
        var step = await connector.LoginAsync(rig.Credentials, rig.Seed(), null, default);

        Assert.Equal(StepStatus.NeedsUser, step.Status);
        Assert.Equal(["HKIDN", "HKEND"], rig.Server.Requests.Select(r => r.Segments[0].Name));
        Assert.DoesNotContain(rig.Server.Requests, SentTanOrPoll);
        Assert.Equal(0, rig.Bank.PollCount);
        var info = (IBackgroundRunInfo)connector;
        Assert.Equal(code, info.FinTsCodes);
        var after = info.SessionAfterStop!;
        Assert.True(after.Values.ContainsKey(FinTsClient.SilentBlockedKey));

        // the next background run does not even open a dialog
        var before = rig.Server.Requests.Count;
        var again = await rig.Connector(silent: true).LoginAsync(rig.Credentials, after, null, default);
        Assert.Equal(StepStatus.NeedsUser, again.Status);
        Assert.Equal(before, rig.Server.Requests.Count);
    }

    [Fact]
    public async Task Tan_for_the_transactions_stops_without_sending_a_tan()
    {
        using var rig = new Rig(job: TanMode.Code);
        var connector = rig.Connector(silent: true);
        Assert.Equal(StepStatus.Done, (await connector.LoginAsync(rig.Credentials, rig.Seed(silentOk: true), null, default)).Status);
        var giro = (await connector.ListAccountsAsync(default)).First(a => a.Iban == SyntheticBank.Iban);

        var step = await connector.RefreshAsync(giro, null, null, default);

        Assert.Equal(StepStatus.NeedsUser, step.Status);
        Assert.DoesNotContain(rig.Server.Requests, SentTanOrPoll);
        Assert.Equal("HKEND", rig.Server.Requests[^1].Segments[0].Name);
        Assert.True(((IBackgroundRunInfo)connector).SessionAfterStop!.Values.ContainsKey(FinTsClient.SilentBlockedKey));
    }

    [Theory]
    [InlineData(10, null, true)]
    [InlineData(79, null, true)]
    [InlineData(81, null, false)]
    [InlineData(null, null, false)]
    [InlineData(200, true, true)]
    [InlineData(200, false, false)]
    public async Task Gate_allows_a_silent_dialog_only_after_an_exempt_init_or_a_recent_tan(int? lastScaDays, bool? silentOk, bool allowed)
    {
        using var rig = new Rig();
        var session = rig.Seed(lastSca: lastScaDays, silentOk: silentOk);
        Assert.Equal(allowed, FinTsClient.SilentBlocker(session, TimeSpan.FromDays(80), rig.Clock.GetUtcNow().UtcDateTime) == null);

        var step = await rig.Connector(silent: true).LoginAsync(rig.Credentials, session, null, default);
        Assert.Equal(allowed ? StepStatus.Done : StepStatus.NeedsUser, step.Status);
        if (!allowed) Assert.Empty(rig.Server.Requests);
    }

    [Fact]
    public async Task Gate_needs_a_settled_connection()
    {
        using var rig = new Rig();
        var session = rig.Seed(silentOk: true);
        session.Values.Remove(FinTsClient.SystemIdKey);
        var step = await rig.Connector(silent: true).LoginAsync(rig.Credentials, session, null, default);
        Assert.Equal(StepStatus.NeedsUser, step.Status);
        Assert.Empty(rig.Server.Requests);
    }

    [Fact]
    public async Task Manual_tan_records_the_sca_and_lifts_the_pause()
    {
        using var rig = new Rig(init: TanMode.Code);
        var session = rig.Seed(lastSca: null);
        session.Values[FinTsClient.SilentBlockedKey] = "1";
        var connector = rig.Connector(silent: false);
        var (step, _) = await FinTsHarness.DriveAsync(a => connector.LoginAsync(rig.Credentials, session, a, default), _ => rig.Bank.TanCode);

        Assert.Equal(StepStatus.Done, step.Status);
        Assert.True(((IBackgroundRunInfo)connector).ScaCompleted);
        var after = (await connector.LogoutAsync(default))!;
        Assert.False(after.Values.ContainsKey(FinTsClient.SilentBlockedKey));
        Assert.True(after.Values.ContainsKey(FinTsClient.LastScaKey));
        Assert.Equal("0", after.Values[FinTsClient.SilentOkKey]);
        Assert.Null(FinTsClient.SilentBlocker(after, TimeSpan.FromDays(80), rig.Clock.GetUtcNow().UtcDateTime));
    }

    [Fact]
    public async Task Rejected_pin_fails_as_rejected_credentials()
    {
        using var rig = new Rig();
        rig.Credentials = new ConnectorCredentials("testuser", "WRONGPIN");
        var step = await rig.Connector(silent: true).LoginAsync(rig.Credentials, rig.Seed(silentOk: true), null, default);
        Assert.Equal(StepStatus.Failed, step.Status);
        Assert.True(step.CredentialsRejected);
        Assert.Single(rig.Server.Requests);
    }
}

/// <summary>A FinTS bank registration against a <see cref="SyntheticBank"/>, for runs through the sync framework.</summary>
internal static class FinTsTestRegistration
{
    public static ConnectorRegistration Create()
    {
        var bank = new SyntheticBank { Pin = "pin" };
        var server = new MockFinTsServer(bank.Respond);
        var url = new Uri("https://bank.invalid/fints");
        var config = new FinTsBankConfig("testfints", "Test Bank", "test", SyntheticBank.Blz, url);
        return FinTsConnector.Registration(config, FinTsHarness.Options, _ => new FinTsHttpTransport(new BankHttpClient(server), url));
    }
}
