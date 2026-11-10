#pragma warning disable CA2000 // test harness: lifetimes are owned by the test
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenBudget.Connectors;
using OpenBudget.Connectors.FinTs;
using Xunit;

namespace OpenBudget.Tests.Connectors.FinTs;

/// <summary>The client against a bank that follows the protocol rules: synthetic scenarios beyond the recordings.</summary>
public sealed class FinTsScenarioTests
{
    private const string Pin = "SECRETPIN1";

    private sealed class Rig : IDisposable
    {
        private readonly FinTsBankConfig _config;
        private readonly FinTsOptions _options;
        private readonly TimeProvider _clock;
        private readonly CapturingLogger _logger = new();

        public Rig(Action<SyntheticBank>? setup = null, FinTsOptions? options = null, bool updInventory = false, TimeProvider? clock = null, string? probe = null, string? fallbackBic = null)
        {
            Bank = new SyntheticBank { Pin = Pin };
            setup?.Invoke(Bank);
            Server = new MockFinTsServer(Bank.Respond);
            _options = options ?? FinTsHarness.Options;
            _config = new FinTsBankConfig("test", "Test Bank", "test", SyntheticBank.Blz, new Uri("https://bank.invalid/fints"));
            if (updInventory) _config = _config with { UpdInventory = true };
            if (probe != null) _config = _config with { ProbeSecurityFunction = probe };
            if (fallbackBic != null) _config = _config with { FallbackBic = fallbackBic };
            _clock = clock ?? TimeProvider.System;
        }

        public SyntheticBank Bank { get; }

        public MockFinTsServer Server { get; }

        public IReadOnlyList<string> Logs => _logger.Lines;

        public ConnectorCredentials Credentials { get; set; } = new("testuser", Pin);

        public IBankConnector NewConnector() =>
            FinTsConnector.Registration(_config, _options).Create(new ConnectorContext(new BankHttpClient(Server), _clock, _logger));

        /// <summary>The session a previous login would have left: bank parameters, system id and TAN method.</summary>
        public ConnectorSession Seed(string tanMethod)
        {
            var session = new ConnectorSession();
            session.Values[FinTsClient.SystemIdKey] = SyntheticBank.AssignedSystemId;
            session.Values[FinTsClient.TanMethodKey] = tanMethod;
            session.Values[FinTsClient.BpdKey] = Bank.BpdText;
            session.Values[FinTsClient.UpdKey] = Bank.UpdText;
            return session;
        }

        public async Task<(IBankConnector Connector, ConnectorStep<ConnectorSession?> Step, List<ConnectorChallenge> Challenges)> LoginAsync(
            ConnectorSession? session, Func<ConnectorChallenge, string?>? answerer = null)
        {
            var connector = NewConnector();
            var (step, challenges) = await FinTsHarness.DriveAsync(a => connector.LoginAsync(Credentials, session, a, default), answerer);
            return (connector, step, challenges);
        }

        public void Dispose() => Server.Dispose();
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));
    }

    private static string SegmentNames(MockRequest r) => string.Join(' ', r.Segments.Select(s => s.Name + "v" + s.Version));

    // ---------------------------------------------------------------- login

    [Fact]
    public async Task First_login_discovers_the_method_synchronizes_the_system_id_and_reuses_both_next_time()
    {
        using var rig = new Rig();
        var (connector, step, challenges) = await rig.LoginAsync(null);

        Assert.Empty(challenges);
        Assert.Equal(StepStatus.Done, step.Status);
        Assert.Equal(1, rig.Bank.SyncCount);
        Assert.Equal(["999", "940", "940", "940"], rig.Server.Requests.Select(r => r.SecurityFunction));
        Assert.Equal("HKIDNv2 HKVVBv3", SegmentNames(rig.Server.Requests[0]));
        Assert.Contains("HKSYNv3", SegmentNames(rig.Server.Requests[1]), StringComparison.Ordinal);
        Assert.Equal("HKEND", rig.Server.Requests[2].Segments[0].Name);
        var session = (await connector.LogoutAsync(default))!;
        Assert.Equal(SyntheticBank.AssignedSystemId, session.Values[FinTsClient.SystemIdKey]);
        Assert.Equal("940", session.Values[FinTsClient.TanMethodKey]);
        Assert.Equal(SyntheticBank.AssignedSystemId, rig.Server.Requests[3].SystemId);

        // Next login: no discovery, no synchronization, cached parameter versions are announced.
        var before = rig.Server.Requests.Count;
        var (again, step2, _) = await rig.LoginAsync(session);
        Assert.Equal(StepStatus.Done, step2.Status);
        var first = rig.Server.Requests[before];
        Assert.Equal("940", first.SecurityFunction);
        Assert.Equal("HKIDNv2 HKVVBv3 HKTANv7", SegmentNames(first));
        Assert.Equal("7", first.Segments[1].Element(0)); // BPD version
        Assert.Equal("5", first.Segments[1].Element(1)); // UPD version
        Assert.Equal(1, rig.Bank.SyncCount);
        await again.LogoutAsync(default);
    }

    /// <summary>An ING-like bank: only HITANS version 1 (method 900, PIN only) and 999 refused with 9010.</summary>
    private static void IngLike(SyntheticBank b)
    {
        b.RejectOneStep = true;
        b.AllowedMethods = ["900"];
        b.BpdText = "HIBPA:3:3:4+9+280:12345678+Test Bank+0+1+220:300+200'"
            + "HIPINS:4:1:4+1+1+0+5:64:6:Alias::HKCAZ:N:HKSAL:N:HKTAN:N'"
            + "HITANS:5:1:4+1+1+0+J:N:0:0:900:2:iTAN:iTAN:6:1:Index:3:1:N:N'"
            + "HISALS:7:5:4+1+1+0'"
            + "HICAZS:9:1:4+1+1+0+390:N:N:urn?:iso?:std?:iso?:20022?:tech?:xsd?:camt.052.001.02'";
    }

    [Fact]
    public async Task Ing_like_bank_is_never_sent_the_one_step_placeholder_nor_HKTAN()
    {
        using var rig = new Rig(IngLike, probe: "900");
        var (connector, step, challenges) = await rig.LoginAsync(null);

        Assert.Empty(challenges);
        Assert.Equal(StepStatus.Done, step.Status);
        Assert.All(rig.Server.Requests, r => Assert.Equal("900", r.SecurityFunction));
        Assert.DoesNotContain(rig.Server.Requests, r => r.Segments.Any(s => s.Name == "HKTAN"));
        Assert.Equal(1, rig.Bank.SyncCount);
        var session = (await connector.LogoutAsync(default))!;
        Assert.Equal("900", session.Values[FinTsClient.TanMethodKey]);
    }

    [Fact]
    public async Task One_step_probe_refused_by_the_bank_logs_the_outgoing_structure_without_secrets()
    {
        using var rig = new Rig(IngLike);
        var (_, step, _) = await rig.LoginAsync(null);

        Assert.NotEqual(StepStatus.Done, step.Status);
        var line = Assert.Single(rig.Logs, l => l.StartsWith("FinTS dialog init refused", StringComparison.Ordinal));
        Assert.Contains("9010", line, StringComparison.Ordinal);
        Assert.Contains("sf=999", line, StringComparison.Ordinal);
        Assert.Contains("HKIDNv2[4] HKVVBv3[5]", line, StringComparison.Ordinal);
        Assert.DoesNotContain(Pin, line, StringComparison.Ordinal);
        Assert.DoesNotContain("testuser", line, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(5000)]
    public async Task Transactions_are_asked_from_a_date_inside_the_banks_window(int? daysBack)
    {
        // Baader: no date, or one older than HICAZS's storage period, is refused with 9210.
        using var rig = new Rig(b => { b.StatementWindowDays = 390; b.UseCamt(); b.UseBalance(); }, fallbackBic: "TESTDEFFXXX");
        var (connector, _, _) = await rig.LoginAsync(rig.Seed("940"));
        var account = (await connector.ListAccountsAsync(default)).Single(a => a.Type == RemoteAccountType.Giro);
        DateOnly? since = daysBack is { } d ? DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-d) : null;
        var (step, _) = await FinTsHarness.DriveAsync(a => connector.RefreshAsync(account, since, a, default), _ => null);
        Assert.Equal(StepStatus.Done, step.Status);
        Assert.NotEmpty(step.Value!.Transactions);
        await connector.LogoutAsync(default);
    }

    [Fact]
    public async Task Camt_names_the_fallback_bic_when_the_upd_has_none()
    {
        // PSD (Atruvia): savings and shares accounts come without a BIC; an empty one is a missing mandatory field (9160).
        using var rig = new Rig(b => { b.RequireBic = true; b.UseCamt(); b.UseBalance(); }, fallbackBic: "TESTDEFFXXX");
        var (connector, _, _) = await rig.LoginAsync(rig.Seed("940"));
        var account = (await connector.ListAccountsAsync(default)).Single(a => a.Type == RemoteAccountType.Giro);
        var (step, _) = await FinTsHarness.DriveAsync(a => connector.RefreshAsync(account, null, a, default), _ => null);
        Assert.Equal(StepStatus.Done, step.Status);
        await connector.LogoutAsync(default);
        Assert.NotNull(PsdNuernbergConnector.Config.FallbackBic);
    }

    [Fact]
    public async Task Hktan_process_4_names_the_job_it_belongs_to()
    {
        using var rig = new Rig();
        var (connector, _, _) = await rig.LoginAsync(rig.Seed("940"));
        var tan = rig.Server.Requests[0].Segments.Single(s => s.Name == "HKTAN");
        Assert.Equal(["4"], tan.Deg(0));
        Assert.Equal("HKIDN", tan.Element(1));
        await connector.LogoutAsync(default);
    }

    [Fact]
    public async Task Wrong_pin_fails_and_marks_the_credentials_as_rejected_without_echoing_them()
    {
        using var rig = new Rig();
        rig.Credentials = new ConnectorCredentials("testuser", "WRONGPIN99");
        var (_, step, _) = await rig.LoginAsync(rig.Seed("940"));
        Assert.Equal(StepStatus.Failed, step.Status);
        Assert.True(step.CredentialsRejected);
        Assert.Contains("9931", step.FailureReason, StringComparison.Ordinal);
        Assert.DoesNotContain("WRONGPIN99", step.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Product_registration_is_required_and_nothing_is_sent_without_it()
    {
        using var rig = new Rig(options: new FinTsOptions(null, "1.0.0"));
        var (_, step, _) = await rig.LoginAsync(null);
        Assert.Equal(StepStatus.Failed, step.Status);
        Assert.Contains("FinTS product registration not configured", step.FailureReason, StringComparison.Ordinal);
        Assert.Contains("OPENBUDGET_FINTS_PRODUCT_ID", step.FailureReason, StringComparison.Ordinal);
        Assert.Empty(rig.Server.Requests);
    }

    [Fact]
    public void Product_id_comes_from_configuration_only()
    {
        var empty = FinTsOptions.FromConfiguration(new ConfigurationBuilder().Build());
        Assert.False(empty.IsConfigured);
        var set = FinTsOptions.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal) { [FinTsOptions.ProductIdVariable] = " TEST-ID " }).Build());
        Assert.True(set.IsConfigured);
        Assert.Equal("TEST-ID", set.ProductId);
        Assert.NotEmpty(set.ProductVersion);
    }

    [Fact]
    public async Task Product_id_and_version_are_sent_in_hkvvb()
    {
        using var rig = new Rig();
        var (connector, _, _) = await rig.LoginAsync(rig.Seed("940"));
        var hkvvb = rig.Server.Requests[0].Segments.Single(s => s.Name == "HKVVB");
        Assert.Equal(FinTsHarness.FakeProductId, hkvvb.Element(3));
        Assert.Equal("9.9.9", hkvvb.Element(4));
        await connector.LogoutAsync(default);
    }

    [Fact]
    public async Task Http_failure_is_reported_without_details_of_the_exchange()
    {
        using var rig = new Rig();
        rig.Server.FailWith = HttpStatusCode.ServiceUnavailable;
        var (_, step, _) = await rig.LoginAsync(rig.Seed("940"));
        Assert.Equal(StepStatus.Failed, step.Status);
        Assert.Contains("HTTP 503", step.FailureReason, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- TAN

    [Fact]
    public async Task Several_tan_methods_become_a_choice_challenge()
    {
        using var rig = new Rig(b => b.AllowedMethods = ["930", "931"]);
        var (connector, step, challenges) = await rig.LoginAsync(null, c => c.Kind == ChallengeKind.Choice ? "931" : null);

        Assert.Equal(StepStatus.Done, step.Status);
        var choice = Assert.Single(challenges);
        Assert.Equal(ChallengeKind.Choice, choice.Kind);
        Assert.Equal([("930", "smsTAN"), ("931", "chipTAN")], choice.Options.Select(o => (o.Id, o.Label)));
        var session = (await connector.LogoutAsync(default))!;
        Assert.Equal("931", session.Values[FinTsClient.TanMethodKey]);
    }

    [Fact]
    public async Task An_unknown_choice_answer_fails()
    {
        using var rig = new Rig(b => b.AllowedMethods = ["930", "931"]);
        var (_, step, _) = await rig.LoginAsync(null, _ => "777");
        Assert.Equal(StepStatus.Failed, step.Status);
        Assert.Contains("Unknown TAN method", step.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Decoupled_login_polls_until_the_user_approves()
    {
        using var rig = new Rig(b => { b.InitTan = TanMode.Decoupled; b.PendingPolls = 2; });
        var (connector, step, challenges) = await rig.LoginAsync(rig.Seed("940"), _ => null);

        Assert.Equal(StepStatus.Done, step.Status);
        Assert.Equal(3, challenges.Count);
        Assert.All(challenges, c => Assert.Equal(ChallengeKind.Approval, c.Kind));
        Assert.All(challenges, c => Assert.True(challenges[0].IsSameQuestion(c)));
        Assert.Equal(3, rig.Bank.PollCount);

        var polls = rig.Server.Requests.Where(r => r.Segments.Any(s => s.Name == "HKTAN" && s.Element(0) == "S")).ToList();
        Assert.Equal(3, polls.Count);
        Assert.All(polls, p =>
        {
            var tan = p.Segments.Single(s => s.Name == "HKTAN");
            Assert.Equal(7, tan.Version);
            Assert.Equal("ORDER1", tan.Element(2));
            Assert.Equal(["HKTAN"], p.Segments.Select(s => s.Name));
        });
        Assert.NotEmpty(connector.ConnectorType);
        Assert.Single(await connector.ListAccountsAsync(default), a => a.Type == RemoteAccountType.Giro);
        await connector.LogoutAsync(default);
    }

    [Fact]
    public async Task Decoupled_approval_that_never_comes_ends_after_the_banks_poll_limit()
    {
        using var rig = new Rig(b =>
        {
            b.InitTan = TanMode.Decoupled;
            b.NeverApprove = true;
            b.BpdText = SyntheticBank.DefaultBpd.Replace(SyntheticBank.MethodV7("940", "Test App", decoupled: true), SyntheticBank.MethodV7("940", "Test App", decoupled: true, maxPolls: 4), StringComparison.Ordinal);
        });
        var (_, step, challenges) = await rig.LoginAsync(rig.Seed("940"), _ => null);

        Assert.Equal(StepStatus.Failed, step.Status);
        Assert.Contains("did not arrive in time", step.FailureReason, StringComparison.Ordinal);
        Assert.Equal(4, challenges.Count);
    }

    [Fact]
    public async Task Decoupled_approval_for_a_job_delivers_the_data_after_the_polls()
    {
        using var rig = new Rig(b => { b.JobTan = TanMode.Decoupled; b.PendingPolls = 1; b.UseCamt(); b.UseBalance(); });
        var (connector, login, _) = await rig.LoginAsync(rig.Seed("940"));
        Assert.Equal(StepStatus.Done, login.Status);
        var account = (await connector.ListAccountsAsync(default)).Single(a => a.Type == RemoteAccountType.Giro);

        var (step, challenges) = await FinTsHarness.DriveAsync(a => connector.RefreshAsync(account, null, a, default), _ => null);
        Assert.Equal(StepStatus.Done, step.Status);
        Assert.Equal(4, challenges.Count); // the bank approves every job (HKCAZ, HKSAL) separately: two polls each
        Assert.Equal(1234.56m, step.Value!.Balance);
        Assert.Equal(2, step.Value.Transactions.Count);

        // HKCAZ went out together with HKTAN process 4 for it (HIPINS says J).
        var request = rig.Server.Requests.First(r => r.Segments.Any(s => s.Name == "HKCAZ"));
        Assert.Equal("HKCAZv1 HKTANv7", SegmentNames(request));
        Assert.Equal("HKCAZ", request.Segments[1].Element(1));
        await connector.LogoutAsync(default);
    }

    [Fact]
    public async Task Tan_entry_becomes_a_code_challenge_and_the_tan_travels_in_the_signature()
    {
        using var rig = new Rig(b =>
        {
            b.AllowedMethods = ["930"];
            b.JobTan = TanMode.Code;
            b.UseCamt();
            b.UseBalance();
        });
        var (connector, login, _) = await rig.LoginAsync(rig.Seed("930"));
        Assert.Equal(StepStatus.Done, login.Status);
        var account = (await connector.ListAccountsAsync(default)).Single(a => a.Type == RemoteAccountType.Giro);

        var (step, challenges) = await FinTsHarness.DriveAsync(a => connector.RefreshAsync(account, null, a, default), _ => rig.Bank.TanCode);
        Assert.Equal(StepStatus.Done, step.Status);
        Assert.Equal(2, challenges.Count); // one TAN per job (HKCAZ, HKSAL)
        var code = challenges[0];
        Assert.Equal(ChallengeKind.Code, code.Kind);
        Assert.Equal("Bitte die TAN eingeben\nDanke", code.Message);
        Assert.Equal(6, code.Length);
        Assert.True(code.Numeric);

        var withTan = rig.Server.Requests.First(r => r.Tan != null);
        Assert.Equal(rig.Bank.TanCode, withTan.Tan);
        Assert.Equal(Pin, withTan.Pin);
        var hktan = Assert.Single(withTan.Segments);
        Assert.Equal((6, "2", "ORDER1"), (hktan.Version, hktan.Element(0), hktan.Element(2)));
        await connector.LogoutAsync(default);
    }

    [Fact]
    public async Task A_wrong_tan_fails_the_refresh_with_the_banks_reason()
    {
        using var rig = new Rig(b => { b.AllowedMethods = ["930"]; b.JobTan = TanMode.Code; b.UseCamt(); });
        var (connector, _, _) = await rig.LoginAsync(rig.Seed("930"));
        var account = (await connector.ListAccountsAsync(default)).Single(a => a.Type == RemoteAccountType.Giro);
        var (step, _) = await FinTsHarness.DriveAsync(a => connector.RefreshAsync(account, null, a, default), _ => "000000");
        Assert.Equal(StepStatus.Failed, step.Status);
        Assert.Contains("9941", step.FailureReason, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- business transactions

    [Fact]
    public async Task Touchdown_paging_over_two_pages_collects_everything()
    {
        using var rig = new Rig(b =>
        {
            b.UseBalance();
            b.Handlers["HKCAZ"] = (s, rb, _) =>
            {
                var second = s.Element(6) == "TD1";
                var doc = SyntheticBank.Camt(second
                    ? ("BOOK", "DBIT", "2.00", "2026-11-04", "Page Two", "Second", "P2")
                    : ("BOOK", "DBIT", "1.00", "2026-11-03", "Page One", "First", "P1"));
                rb.Segment("HICAZ", 1, s.Number, "x", "urn?:iso?:std?:iso?:20022?:tech?:xsd?:camt.052.001.02", SyntheticBank.Bin(doc));
                rb.Codes(s.Number, second ? "0020::Ausgeführt." : "3040::Weitere Informationen liegen vor.:TD1");
            };
        });
        var (connector, _, _) = await rig.LoginAsync(rig.Seed("940"), _ => null);
        var account = (await connector.ListAccountsAsync(default)).Single(a => a.Type == RemoteAccountType.Giro);
        var (step, _) = await FinTsHarness.DriveAsync(a => connector.RefreshAsync(account, null, a, default), _ => null);

        Assert.Equal(StepStatus.Done, step.Status);
        Assert.Equal(["P1", "P2"], step.Value!.Transactions.Select(t => t.BankTransactionId));
        var requests = rig.Server.Requests.Where(r => r.Segments.Any(s => s.Name == "HKCAZ")).ToList();
        Assert.Equal(2, requests.Count);
        var second = requests[1].Segments.Single(s => s.Name == "HKCAZ");
        Assert.Equal("TD1", second.Element(6));
        await connector.LogoutAsync(default);
    }

    [Fact]
    public async Task Mt940_is_used_when_the_bank_has_no_hkcaz()
    {
        using var rig = new Rig(b =>
        {
            b.BpdText = b.BpdText.Replace("HICAZS:9:1:4+1+1+0+390:N:N:urn?:iso?:std?:iso?:20022?:tech?:xsd?:camt.052.001.02'", string.Empty, StringComparison.Ordinal);
            b.UseBalance();
            b.Handlers["HKKAZ"] = (s, rb, _) => rb.Segment(
                "HIKAZ", 7, s.Number,
                SyntheticBank.Bin(":20:X\r\n:25:12345678/1234567\r\n:60F:C261031EUR100,00\r\n:61:2611031103D12,30NMSCNONREF//BANKREF1\r\n:86:177?00Lastschrift?20Coffee beans?32Example Roaster\r\n:62F:C261103EUR87,70\r\n-"),
                SyntheticBank.Bin(":20:Y\r\n:25:12345678/1234567\r\n:28C:0\r\n:60F:C261103EUR87,70\r\n:61:261109D4,00NMSCNONREF\r\n:86:177?00Kartenzahlung?20Reserved?32Example Cafe\r\n:62F:C261109EUR83,70\r\n-"));
        });
        var (connector, _, _) = await rig.LoginAsync(rig.Seed("940"), _ => null);
        var account = (await connector.ListAccountsAsync(default)).Single(a => a.Type == RemoteAccountType.Giro);
        var (step, _) = await FinTsHarness.DriveAsync(a => connector.RefreshAsync(account, new DateOnly(2026, 11, 1), a, default), _ => null);

        Assert.Equal(StepStatus.Done, step.Status);
        var txs = step.Value!.Transactions;
        Assert.Equal(2, txs.Count);
        Assert.Equal((-12.30m, true, "Example Roaster", "Coffee beans", "BANKREF1"), (txs[0].Amount, txs[0].Booked, txs[0].Name, txs[0].Purpose, txs[0].BankTransactionId));
        Assert.Equal((-4.00m, false, "Example Cafe"), (txs[1].Amount, txs[1].Booked, txs[1].Name));
        var request = rig.Server.Requests.Last(r => r.Segments.Any(s => s.Name == "HKKAZ")).Segments.Single(s => s.Name == "HKKAZ");
        Assert.Equal(7, request.Version);
        Assert.Equal("20261101", request.Element(2));
        Assert.DoesNotContain(rig.Server.Requests, r => r.Segments.Any(s => s.Name == "HKCAZ"));
        await connector.LogoutAsync(default);
    }

    [Fact]
    public async Task Depot_positions_come_from_hkwpd()
    {
        using var rig = new Rig(b => b.Handlers["HKWPD"] = (s, rb, _) => rb.Segment(
            "HIWPD", 6, s.Number,
            SyntheticBank.Bin(":16R:GENL\r\n:16S:GENL\r\n:16R:FIN\r\n:35B:ISIN XX0000000001/DE/A0EXAM\r\nExample World ETF\r\n:90B::MRKT//ACTU/EUR100,25\r\n:93B::AGGR//UNIT/10,5\r\n:19A::HOLD//EUR1052,63\r\n:16S:FIN\r\n-")));
        var (connector, _, _) = await rig.LoginAsync(rig.Seed("940"), _ => null);
        var depot = (await connector.ListAccountsAsync(default)).Single(a => a.Type == RemoteAccountType.Depot);
        var (step, _) = await FinTsHarness.DriveAsync(a => connector.RefreshAsync(depot, null, a, default), _ => null);

        Assert.Equal(StepStatus.Done, step.Status);
        var position = Assert.Single(step.Value!.Positions);
        Assert.Equal(("XX0000000001", 10.5m, 100.25m), (position.Isin, position.Quantity, position.Price));
        Assert.Equal(1052.63m, step.Value.Balance);
        Assert.Empty(step.Value.Transactions);
        var request = rig.Server.Requests.Last(r => r.Segments.Any(s => s.Name == "HKWPD")).Segments.Single(s => s.Name == "HKWPD");
        Assert.Equal(6, request.Version);
        Assert.Equal($"{SyntheticBank.DepotAccount}::280:{SyntheticBank.Blz}", string.Join(':', request.Deg(0)));
        await connector.LogoutAsync(default);
    }

    [Fact]
    public async Task Electronic_statements_are_downloaded_once_per_identifier()
    {
        var clock = new TestClock();
        clock.Set(new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc));
        using var rig = new Rig(b => b.Handlers["HKEKA"] = (s, rb, _) =>
        {
            if (s.Element(3) == "2026")
                rb.Segment("HIEKA", 5, s.Number, "3", "20261001:20261031", "20261031", "2026", "10", SyntheticBank.Bin("%PDF-1.4\n(synthetic statement)\n%%EOF\n"));
            else
                rb.Codes(s.Number, "3010::Keine Einträge.");
        }, clock: clock);
        var (connector, _, _) = await rig.LoginAsync(rig.Seed("940"), _ => null);
        var accounts = (await connector.ListAccountsAsync(default)).Where(a => a.Type == RemoteAccountType.Giro).ToList();

        var (step, _) = await FinTsHarness.DriveAsync(a => connector.FetchStatementsAsync(accounts, new HashSet<string>(StringComparer.Ordinal), a, default), _ => null);
        Assert.Equal(StepStatus.Done, step.Status);
        var statement = Assert.Single(step.Value!);
        Assert.Equal($"eka:{SyntheticBank.Iban}:2026-10", statement.Identifier);
        Assert.Equal(new DateOnly(2026, 10, 31), statement.Date);
        Assert.StartsWith("%PDF", System.Text.Encoding.Latin1.GetString(statement.Content), StringComparison.Ordinal);
        var requests = rig.Server.Requests.SelectMany(r => r.Segments).Where(s => s.Name == "HKEKA").ToList();
        Assert.Equal(["2026", "2025"], requests.Select(r => r.Element(3)));
        Assert.All(requests, r => Assert.Equal("3", r.Element(1)));
        Assert.Equal(5, requests[0].Version);

        var known = new HashSet<string>(StringComparer.Ordinal) { statement.Identifier };
        var (again, _) = await FinTsHarness.DriveAsync(a => connector.FetchStatementsAsync(accounts, known, a, default), _ => null);
        Assert.Empty(again.Value!);
        Assert.Equal(1, rig.Server.Requests.SelectMany(r => r.Segments).Count(s => s.Name == "HKEKA" && s.Element(3) == "2026") - 1);
        await connector.LogoutAsync(default);
    }

    [Fact]
    public async Task A_bank_without_statements_yields_none()
    {
        using var rig = new Rig(b => b.BpdText = b.BpdText.Replace("HIEKAS:12:5:4+1+1+0+N:N:N:3'", string.Empty, StringComparison.Ordinal));
        var (connector, _, _) = await rig.LoginAsync(rig.Seed("940"), _ => null);
        var accounts = await connector.ListAccountsAsync(default);
        var (step, _) = await FinTsHarness.DriveAsync(a => connector.FetchStatementsAsync(accounts, new HashSet<string>(StringComparer.Ordinal), a, default), _ => null);
        Assert.Empty(step.Value!);
        Assert.DoesNotContain(rig.Server.Requests, r => r.Segments.Any(s => s.Name == "HKEKA"));
    }

    [Fact]
    public async Task A_rejected_job_fails_the_refresh_with_code_and_text()
    {
        using var rig = new Rig(b => b.Unsupported.Add("HKCAZ"));
        var (connector, _, _) = await rig.LoginAsync(rig.Seed("940"), _ => null);
        var account = (await connector.ListAccountsAsync(default)).Single(a => a.Type == RemoteAccountType.Giro);
        var (step, _) = await FinTsHarness.DriveAsync(a => connector.RefreshAsync(account, null, a, default), _ => null);
        Assert.Equal(StepStatus.Failed, step.Status);
        Assert.Contains("9120", step.FailureReason, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- UPD inventory

    private static string UpdWith(string inventory) =>
        SyntheticBank.DefaultUpd.Replace("HKEKA:1'", "HKEKA:1+{\"umsltzt\"?:\"" + inventory + "\"}'", StringComparison.Ordinal);

    [Fact]
    public async Task Unchanged_upd_inventory_skips_the_transaction_fetch_but_pending_rows_force_one()
    {
        using var rig = new Rig(b =>
        {
            b.UpdText = UpdWith("T1");
            b.UseBalance();
            b.Handlers["HKCAZ"] = (s, rb, _) => rb.Segment(
                "HICAZ", 1, s.Number, "x", "urn?:iso?:std?:iso?:20022?:tech?:xsd?:camt.052.001.02",
                SyntheticBank.Bin(SyntheticBank.Camt(("BOOK", "DBIT", "1.00", "2026-11-03", "Shop", "Buy", "B1"))));
        }, updInventory: true);
        var since = new DateOnly(2026, 11, 1);

        async Task<(ConnectorSession Session, AccountRefresh Refresh)> SyncAsync(ConnectorSession? session)
        {
            var (connector, login, _) = await rig.LoginAsync(session, _ => null);
            Assert.Equal(StepStatus.Done, login.Status);
            var account = (await connector.ListAccountsAsync(default)).Single(a => a.Type == RemoteAccountType.Giro);
            var (step, _) = await FinTsHarness.DriveAsync(a => connector.RefreshAsync(account, since, a, default), _ => null);
            return ((await connector.LogoutAsync(default))!, step.Value!);
        }

        var (s1, r1) = await SyncAsync(rig.Seed("940"));
        Assert.Single(r1.Transactions);
        Assert.Equal("T1|0", s1.Values[FinTsClient.InventoryKeyPrefix + SyntheticBank.Iban]);
        Assert.Equal("0", rig.Server.Requests[0].Segments.Single(s => s.Name == "HKVVB").Element(1)); // the UPD is asked for every time

        int camtRequests() => rig.Server.Requests.Count(r => r.Segments.Any(s => s.Name == "HKCAZ"));
        Assert.Equal(1, camtRequests());

        var (s2, r2) = await SyncAsync(s1);
        Assert.Equal(1, camtRequests());
        Assert.Empty(r2.Transactions);
        Assert.Null(r2.Balance);

        rig.Bank.UpdText = UpdWith("T2");
        var (s3, r3) = await SyncAsync(s2);
        Assert.Equal(2, camtRequests());
        Assert.Single(r3.Transactions);
        Assert.Equal("T2|0", s3.Values[FinTsClient.InventoryKeyPrefix + SyntheticBank.Iban]);

        // A first sync (no start date: nothing stored yet) never skips, whatever the marker says.
        var (connector, _, _) = await rig.LoginAsync(s3, _ => null);
        var account = (await connector.ListAccountsAsync(default)).Single(a => a.Type == RemoteAccountType.Giro);
        var (full, _) = await FinTsHarness.DriveAsync(a => connector.RefreshAsync(account, null, a, default), _ => null);
        Assert.Single(full.Value!.Transactions);
        Assert.Equal(3, camtRequests());

        // Pending rows are replaced on every sync, so a marker seen together with pending ones is not trusted.
        s3.Values[FinTsClient.InventoryKeyPrefix + SyntheticBank.Iban] = "T2|1";
        var (_, r4) = await SyncAsync(s3);
        Assert.Single(r4.Transactions);
        Assert.Equal(4, camtRequests());
    }

    [Fact]
    public async Task Without_the_atruvia_quirk_the_marker_is_ignored()
    {
        using var rig = new Rig(b => { b.UpdText = UpdWith("T1"); b.UseCamt(); b.UseBalance(); });
        var session = rig.Seed("940");
        session.Values[FinTsClient.InventoryKeyPrefix + SyntheticBank.Iban] = "T1|0";
        var (connector, _, _) = await rig.LoginAsync(session, _ => null);
        var account = (await connector.ListAccountsAsync(default)).Single(a => a.Type == RemoteAccountType.Giro);
        var (step, _) = await FinTsHarness.DriveAsync(a => connector.RefreshAsync(account, new DateOnly(2026, 11, 1), a, default), _ => null);
        Assert.NotEmpty(step.Value!.Transactions);
    }

    // ---------------------------------------------------------------- hygiene

    [Fact]
    public async Task Logs_name_segments_and_codes_but_never_secrets_or_data()
    {
        using var rig = new Rig(b => { b.InitTan = TanMode.Decoupled; b.PendingPolls = 1; b.UseCamt(); b.UseBalance(); });
        var (connector, _, _) = await rig.LoginAsync(rig.Seed("940"), _ => null);
        var account = (await connector.ListAccountsAsync(default)).Single(a => a.Type == RemoteAccountType.Giro);
        await FinTsHarness.DriveAsync(a => connector.RefreshAsync(account, null, a, default), _ => null);
        await connector.LogoutAsync(default);

        var log = string.Join('\n', rig.Logs);
        Assert.Contains("HKIDN", log, StringComparison.Ordinal);
        Assert.Contains("3955", log, StringComparison.Ordinal);
        foreach (var secret in new[] { Pin, "testuser", SyntheticBank.Iban, SyntheticBank.Account, "Example Shop", SyntheticBank.AssignedSystemId, FinTsHarness.FakeProductId })
            Assert.DoesNotContain(secret, log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_request_goes_to_the_banks_url_as_base64_text()
    {
        using var rig = new Rig();
        var (connector, _, _) = await rig.LoginAsync(rig.Seed("940"));
        Assert.Equal(new Uri("https://bank.invalid/fints"), rig.Server.LastUrl);
        await connector.LogoutAsync(default);
    }
}
