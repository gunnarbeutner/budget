#pragma warning disable CA2000 // test objects live for the test
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenBudget.Connectors;
using OpenBudget.Connectors.FinTs;
using Xunit;

namespace OpenBudget.Tests.Connectors;

/// <summary>The Baader portal's document download against a made-up portal (no network, no Chromium).</summary>
public sealed class BaaderPortalTests
{
    private static readonly RemoteAccount Cash = new("cash-1", "Verrechnungskonto", RemoteAccountType.Giro, "DE00000000000000000004", "EUR");
    private static readonly RemoteAccount Depot = new("depot-1", "Depot", RemoteAccountType.Depot, null, "EUR");
    private static readonly ConnectorCredentials Credentials = new("ALICE1", "Example-Password1!");

    private sealed class Rig
    {
        public FakeBaaderPortal Portal { get; } = new();

        public CapturingLoggerFactory Logs { get; } = new();

        public BankHttpClient Http { get; }

        public int WebLogins { get; private set; }

        public int TanRequests { get; private set; }

        public List<FakeBaaderPortal.FakePortalBrowser> Browsers { get; } = [];

        /// <summary>The login's session values, kept from sync to sync like the runner's stored session.</summary>
        public Dictionary<string, string> State { get; } = new(StringComparer.Ordinal);

        public Rig() => Http = Portal.CreateClient();

        /// <summary>
        /// One sync's statements step, driven like the sync runner: a web login hands over a signed-in browser (or none when
        /// the user does not sign in); a TAN question is answered with <paramref name="tan"/>.
        /// </summary>
        public async Task<ConnectorStep<IReadOnlyList<RemoteStatement>>> FetchAsync(
            IReadOnlySet<string>? known = null, IReadOnlyList<RemoteAccount>? accounts = null, bool userSignsIn = true, string tan = FakeBaaderPortal.Tan)
        {
            var client = new BaaderPortal(Http, Logs.CreateLogger("baader"), state: State); // a new connector per sync
            ChallengeAnswer? answer = null;
            for (var i = 0; i < 6; i++)
            {
                var step = await client.FetchAsync(accounts ?? [Cash, Depot], known ?? new HashSet<string>(StringComparer.Ordinal), Credentials, answer, default);
                if (step.Status != StepStatus.Challenge) return step;
                if (step.Challenge!.Kind == ChallengeKind.WebLogin)
                {
                    Assert.True(step.Challenge.WebLoginSpec!.KeepBrowser);
                    WebLogins++;
                    FakeBaaderPortal.FakePortalBrowser? browser = null;
                    if (userSignsIn)
                    {
                        browser = Portal.SignInBrowser();
                        Browsers.Add(browser);
                    }
                    answer = new ChallengeAnswer(step.Challenge, null) { Browser = browser };
                }
                else
                {
                    Assert.Equal(BaaderPortal.TanReference, step.Challenge.Reference);
                    TanRequests++;
                    answer = new ChallengeAnswer(step.Challenge, tan);
                }
            }
            throw new InvalidOperationException("too many rounds");
        }
    }

    private static FakeBaaderPortal.Doc D(int y, int m, int d, string title, string id) => new(new DateOnly(y, m, d), title, id);

    private static void Seed(FakeBaaderPortal portal) => portal.Documents.AddRange(
    [
        D(2026, 10, 31, "Kontoauszug monatlich", "0123456789abcdef0123456789abcd01"),
        D(2026, 10, 14, "Quartalsaufstellung", "0123456789abcdef0123456789abcd02"),
        D(2026, 9, 15, "Wertpapierabrechnung Kauf", "0123456789abcdef0123456789abcd03"),
        D(2026, 3, 1, "Jahressteuerbescheinigung 2025", "0123456789abcdef0123456789abcd04"), // only in the extended listing
        D(2025, 1, 2, "Kontoauszug monatlich", "0123456789abcdef0123456789abcd05"), // only in the extended listing
        D(2023, 1, 2, "Kontoauszug monatlich", "0123456789abcdef0123456789abcd06"), // older than the longest period
    ]);

    private static string Id(int n) => "baader:0123456789abcdef0123456789abcd0" + n;

    [Fact]
    public async Task The_first_download_asks_for_the_tan_and_lists_the_whole_filtered_history()
    {
        var rig = new Rig();
        Seed(rig.Portal);
        var step = await rig.FetchAsync();

        Assert.Equal(StepStatus.Done, step.Status);
        Assert.Equal(1, rig.WebLogins);
        Assert.Equal(1, rig.TanRequests);
        var docs = step.Value!;
        // the date range reaches back to the earliest date the form allows, beyond the longest period
        Assert.Equal([Id(1), Id(2), Id(3), Id(4), Id(5), Id(6)], docs.Select(d => d.Identifier).Order(StringComparer.Ordinal));
        Assert.All(docs, d => Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(d.Content, 0, 4), StringComparison.Ordinal));
        Assert.Equal("cash-1", docs.Single(d => d.Identifier == Id(1)).AccountRemoteId);
        Assert.Equal(new DateOnly(2026, 10, 31), docs.Single(d => d.Identifier == Id(1)).Date);
        Assert.Equal("depot-1", docs.Single(d => d.Identifier == Id(3)).AccountRemoteId);
        Assert.Null(docs.Single(d => d.Identifier == Id(4)).AccountRemoteId);

        Assert.Equal(["/mail", "/mail/" + FakeBaaderPortal.CustomerId], rig.Portal.FilterPaths);
        var filter = rig.Portal.FilterPosts[0];
        Assert.Equal("1", filter["filter"]);
        Assert.Equal(FakeBaaderPortal.PageToken, filter["token"]);
        Assert.Equal("fromuntil", filter["timeframe"]);
        Assert.Equal("01.01.2016", filter["from"]);
        Assert.False(filter.ContainsKey("unread"));
        var check = Assert.Single(rig.Portal.TanChecks);
        Assert.Equal(FakeBaaderPortal.Tan, check["tan"]);
        Assert.Equal("1", check["__multipart"]);
        Assert.Equal(6, rig.Browsers[0].Resolved); // each download's storage link came from the browser
    }

    [Fact]
    public async Task A_rejected_date_range_falls_back_to_the_longest_period()
    {
        var rig = new Rig();
        Seed(rig.Portal);
        rig.Portal.RejectDateRange = true;
        var docs = (await rig.FetchAsync()).Value!;

        Assert.Equal([Id(1), Id(2), Id(3), Id(4), Id(5)], docs.Select(d => d.Identifier).Order(StringComparer.Ordinal));
        Assert.Equal(["fromuntil", "period"], rig.Portal.FilterPosts.Take(2).Select(p => p["timeframe"]));
        Assert.Equal("720", rig.Portal.FilterPosts[1]["periods"]);
        Assert.Contains(rig.Logs.Entries, e => e.Message.Contains("rejected the date range", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_customer_mailbox_is_listed_too_and_merged_by_file_id()
    {
        var rig = new Rig();
        Seed(rig.Portal);
        rig.Portal.CustomerDocuments.AddRange(
        [
            D(2025, 6, 2, "Depotauszug Smartbroker+", "0123456789abcdef0123456789abcd11"),
            D(2026, 9, 15, "Wertpapierabrechnung Kauf", "0123456789abcdef0123456789abcd03"), // also in the general mailbox
        ]);
        var docs = (await rig.FetchAsync()).Value!;

        Assert.Equal(1, rig.TanRequests); // one TAN covers both mailboxes
        Assert.Single(docs, d => d.Identifier == Id(3));
        Assert.Equal("depot-1", docs.Single(d => d.Identifier == "baader:0123456789abcdef0123456789abcd11").AccountRemoteId);
        Assert.Equal(1, rig.Portal.Downloads["0123456789abcdef0123456789abcd03"]);
        Assert.Contains(rig.Logs.Entries, e => e.Message.Contains("listed in the customer mailbox 1 (long listing)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_customer_mailbox_gets_its_long_listing_once_even_when_accounts_have_documents()
    {
        var rig = new Rig();
        Seed(rig.Portal);
        var known = new HashSet<string>(StringComparer.Ordinal) { Id(1), Id(3) };
        known.Add(OpenBudget.Connectors.Sync.BankStatementStore.AccountHasDocumentsMarker + "cash-1");
        known.Add(OpenBudget.Connectors.Sync.BankStatementStore.AccountHasDocumentsMarker + "depot-1");
        rig.Portal.CustomerDocuments.Add(D(2024, 6, 3, "Depotauszug Smartbroker+", "0123456789abcdef0123456789abcd11"));

        var first = await rig.FetchAsync(known);

        Assert.Equal(["/mail/" + FakeBaaderPortal.CustomerId], rig.Portal.FilterPaths); // the general mailbox keeps its recent listing
        Assert.Equal(1, rig.TanRequests);
        Assert.Contains(first.Value!, d => d.Identifier == "baader:0123456789abcdef0123456789abcd11");
        Assert.Contains(BaaderPortal.HistoryDoneKeyPrefix + FakeBaaderPortal.CustomerId, rig.State.Keys);

        rig.Portal.FilterPaths.Clear();
        foreach (var d in first.Value!) known.Add(d.Identifier);
        var second = await rig.FetchAsync(known);
        Assert.Empty(rig.Portal.FilterPaths);
        Assert.Equal(1, rig.TanRequests);
        Assert.Empty(second.Value!);
    }

    [Fact]
    public void Merge_keeps_each_file_id_once_from_the_first_mailbox()
    {
        var a = new BaaderPortal.PortalDocument("x1", new DateOnly(2026, 1, 1), "A");
        var b = new BaaderPortal.PortalDocument("x2", new DateOnly(2026, 1, 2), "B");
        var merged = BaaderPortal.Merge([("", [a, b]), ("4711", [b with { Title = "B again" }, a, new("x3", new DateOnly(2026, 1, 3), "C")])]).ToList();
        Assert.Equal(["x1", "x2", "x3"], merged.Select(m => m.Document.Id));
        Assert.Equal(["", "", "4711"], merged.Select(m => m.Mailbox));
        Assert.Equal("B", merged[1].Document.Title);
    }

    [Fact]
    public void Customer_ids_come_from_the_status_answer()
    {
        var json = JsonElement.Parse("""[{"customer_id":1234567,"first_name":"A"},{"customer_id":"7654321"},{"customer_id":1234567},{"customer_id":"../x"},{"other":1}]""");
        Assert.Equal(["1234567", "7654321"], BaaderPortal.CustomerIds(json));
        Assert.Empty(BaaderPortal.CustomerIds(JsonElement.Parse("{}")));
    }

    [Fact]
    public async Task An_account_without_documents_gets_its_own_history_pass()
    {
        var rig = new Rig();
        Seed(rig.Portal);
        var depotOnly = await rig.FetchAsync(accounts: [Depot]);
        var known = depotOnly.Value!.Select(d => d.Identifier).ToHashSet(StringComparer.Ordinal);
        known.Add(OpenBudget.Connectors.Sync.BankStatementStore.AccountHasDocumentsMarker + "depot-1");
        rig.Portal.FilterPosts.Clear();

        var withCash = await rig.FetchAsync(known);

        Assert.NotEmpty(rig.Portal.FilterPosts); // the long listing again, for the cash account
        Assert.Contains(withCash.Value!, d => d.AccountRemoteId != null && d.AccountRemoteId != "depot-1");
    }

    [Fact]
    public async Task The_sms_is_requested_before_the_tan_is_asked_for()
    {
        var rig = new Rig();
        Seed(rig.Portal);
        var step = await rig.FetchAsync();
        Assert.Equal(1, rig.Portal.SmsSent);
        Assert.NotEmpty(step.Value!);
    }

    [Fact]
    public async Task Later_syncs_use_the_default_listing_without_a_tan()
    {
        var rig = new Rig();
        Seed(rig.Portal);
        var first = await rig.FetchAsync();
        var known = first.Value!.Select(d => d.Identifier).ToHashSet(StringComparer.Ordinal);
        // the runner marks requested accounts that already hold documents
        foreach (var account in first.Value!.Where(d => d.AccountRemoteId != null).Select(d => d.AccountRemoteId!).Distinct(StringComparer.Ordinal))
            known.Add(OpenBudget.Connectors.Sync.BankStatementStore.AccountHasDocumentsMarker + account);
        rig.Portal.Documents.Add(D(2026, 11, 9, "Kontoauszug monatlich", "0123456789abcdef0123456789abcd07"));
        rig.Portal.FilterPosts.Clear();

        var second = await rig.FetchAsync(known);

        Assert.Equal(1, rig.TanRequests);
        Assert.Empty(rig.Portal.FilterPosts);
        Assert.Equal(Id(7), Assert.Single(second.Value!).Identifier);
        Assert.Equal(1, rig.Portal.Downloads["0123456789abcdef0123456789abcd01"]);
    }

    [Fact]
    public async Task A_wrong_tan_lists_nothing_and_is_reported()
    {
        var rig = new Rig();
        Seed(rig.Portal);
        var step = await rig.FetchAsync(tan: "000000");
        Assert.Equal(StepStatus.Done, step.Status);
        Assert.Empty(step.Value!);
        Assert.Contains(rig.Logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("after the TAN", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Documents_of_accounts_whose_statements_are_off_are_skipped()
    {
        var rig = new Rig();
        Seed(rig.Portal);
        var docs = (await rig.FetchAsync(accounts: [Depot])).Value!;
        Assert.DoesNotContain(docs, d => d.Identifier == Id(1));
        Assert.Equal("depot-1", docs.Single(d => d.Identifier == Id(3)).AccountRemoteId);
    }

    [Fact]
    public async Task A_document_that_is_not_a_pdf_is_left_for_the_next_sync()
    {
        var rig = new Rig();
        Seed(rig.Portal);
        rig.Portal.BrokenDownloads.Add("0123456789abcdef0123456789abcd02");
        var docs = (await rig.FetchAsync()).Value!;
        Assert.DoesNotContain(docs, d => d.Identifier == Id(2));
        Assert.Contains(docs, d => d.Identifier == Id(1));
        Assert.Contains(rig.Logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("did not return a PDF", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_sign_in_that_does_not_complete_fails()
    {
        var rig = new Rig();
        var ex = await Assert.ThrowsAsync<ConnectorException>(() => rig.FetchAsync(userSignsIn: false));
        Assert.Contains("did not complete", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_session_that_ends_while_listing_fails_the_fetch()
    {
        var rig = new Rig();
        Seed(rig.Portal);
        var client = new BaaderPortal(rig.Http, rig.Logs.CreateLogger("baader"));
        var ask = await client.FetchAsync([Cash], new HashSet<string>(StringComparer.Ordinal), Credentials, null, default);
        var browser = rig.Portal.SignInBrowser();
        await client.FetchAsync([Cash], new HashSet<string>(StringComparer.Ordinal), Credentials, new ChallengeAnswer(ask.Challenge!, null) { Browser = browser }, default);
        rig.Portal.ExpireSessions();
        var ex = await Assert.ThrowsAsync<ConnectorException>(() => client.FetchAsync([Cash], new HashSet<string>(StringComparer.Ordinal), Credentials,
            new ChallengeAnswer(ConnectorChallenge.Code("TAN", reference: BaaderPortal.TanReference), FakeBaaderPortal.Tan), default));
        Assert.Contains("ended the session", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_expired_session_asks_for_a_new_sign_in_at_the_next_sync()
    {
        var rig = new Rig();
        Seed(rig.Portal);
        var first = await rig.FetchAsync();
        rig.Portal.ExpireSessions();
        var step = await rig.FetchAsync(first.Value!.Select(d => d.Identifier).ToHashSet(StringComparer.Ordinal));
        Assert.Equal(StepStatus.Done, step.Status);
        Assert.Equal(2, rig.WebLogins);
    }

    [Fact]
    public async Task Secrets_never_reach_the_log()
    {
        var rig = new Rig();
        Seed(rig.Portal);
        await rig.FetchAsync();
        var all = string.Join("\n", rig.Logs.Entries.Select(e => e.Message));
        Assert.DoesNotContain("Example-Password1!", all, StringComparison.Ordinal);
        Assert.DoesNotContain("secretvalue", all, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeBaaderPortal.PageToken, all, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeBaaderPortal.Tan, all, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeBaaderPortal.Signature, all, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeBaaderPortal.CustomerId, all, StringComparison.Ordinal);
        Assert.DoesNotContain("abcd01", all, StringComparison.Ordinal);
    }

    [Fact]
    public void Rows_parse_and_ignore_malformed_entries()
    {
        var json = JsonElement.Parse("""[{"2026-10-01":[[1,"Statement","abc123"],[0,"Bad id","../x"],["short"]],"not-a-date":[[1,"x","y"]]}]""");
        var doc = Assert.Single(BaaderPortal.ParseRows(json));
        Assert.Equal(("abc123", new DateOnly(2026, 10, 1), "Statement"), (doc.Id, doc.Date, doc.Title));
        Assert.Empty(BaaderPortal.ParseRows(JsonElement.Parse("[]")));
        string? shape = null;
        Assert.Empty(BaaderPortal.ParseRows(JsonElement.Parse("""{"do":"mfa"}"""), s => shape = s));
        Assert.Equal("do:String", shape);
    }

    [Theory]
    [InlineData("Kontoauszug monatlich", "Cash")]
    [InlineData("Zinsstaffel", "Cash")]
    [InlineData("Wertpapierabrechnung Kauf", "Depot")]
    [InlineData("Dividendengutschrift", "Depot")]
    [InlineData("Jahressteuerbescheinigung 2025", "Login")]
    [InlineData("Quartalsaufstellung", "Login")]
    [InlineData("Depotauszug planmässig", "Depot")]
    [InlineData("Vorabpauschale 2025", "Depot")]
    public void Documents_belong_to_an_account_by_their_description(string title, string target) =>
        Assert.Equal(target, BaaderPortal.TargetOf(title).ToString());

    [Fact]
    public void Baader_offers_statements()
    {
        var registration = BaaderConnector.Registration(new FinTsOptions("X", "1"));
        Assert.True(registration.Statements);
    }
}
