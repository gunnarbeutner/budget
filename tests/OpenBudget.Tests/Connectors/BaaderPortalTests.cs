#pragma warning disable CA2000 // test objects live for the test
using System.Text.Json;
using Microsoft.Extensions.Logging;
using OpenBudget.Connectors;
using OpenBudget.Connectors.FinTs;
using Xunit;

namespace OpenBudget.Tests.Connectors;

/// <summary>The Baader portal's document download against a made-up portal (no network).</summary>
public sealed class BaaderPortalTests
{
    private static readonly DateTime Today = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
    private static readonly RemoteAccount Cash = new("cash-1", "Verrechnungskonto", RemoteAccountType.Giro, "DE00000000000000000004", "EUR");
    private static readonly RemoteAccount Depot = new("depot-1", "Depot", RemoteAccountType.Depot, null, "EUR");
    private static readonly ConnectorCredentials Credentials = new("ALICE1", "Example-Password1!");

    private sealed class Rig
    {
        public FakeBaaderPortal Portal { get; } = new();

        public CapturingLoggerFactory Logs { get; } = new();

        public BankHttpClient Http { get; }

        public TestClock Clock { get; } = new();

        public BaaderPortal Client { get; }

        public int WebLogins { get; private set; }

        public Rig()
        {
            Http = Portal.CreateClient();
            Clock.Set(Today);
            Client = new BaaderPortal(Http, Clock, Logs.CreateLogger("baader"));
        }

        /// <summary>Drives the fetch like the sync runner; a web login is "done" by the user signing in in the browser.</summary>
        public async Task<ConnectorStep<IReadOnlyList<RemoteStatement>>> FetchAsync(
            IReadOnlySet<string>? known = null, IReadOnlyList<RemoteAccount>? accounts = null, bool userSignsIn = true)
        {
            ChallengeAnswer? answer = null;
            for (var i = 0; i < 5; i++)
            {
                var step = await Client.FetchAsync(accounts ?? [Cash, Depot], known ?? new HashSet<string>(StringComparer.Ordinal), Credentials, answer, default);
                if (step.Status != StepStatus.Challenge) return step;
                Assert.Equal(ChallengeKind.WebLogin, step.Challenge!.Kind);
                WebLogins++;
                if (userSignsIn) Http.ReplaceCookies(step.Challenge.WebLoginSpec!, [Portal.SignIn()]);
                answer = new ChallengeAnswer(step.Challenge, null);
            }
            throw new InvalidOperationException("too many rounds");
        }
    }

    private static FakeBaaderPortal.Doc D(string section, int y, int m, int d, string title, string id) => new(section, new DateOnly(y, m, d), title, id);

    private static void Seed(FakeBaaderPortal portal) => portal.Documents.AddRange(
    [
        D("ACCOUNT_STATEMENT", 2026, 10, 31, "Kontoauszug monatlich", "a1"),
        D("ACCOUNT_STATEMENT", 2026, 10, 1, "Kontoauszug monatlich", "a2"),
        D("ACCOUNT_STATEMENT", 2026, 10, 1, "Zinsstaffel", "a3"),
        D("NOTIFICATIONS", 2026, 9, 15, "Wertpapierabrechnung Kauf", "n1"),
        D("TAXES", 2026, 3, 1, "Jahressteuerbescheinigung 2025", "t1"), // two periods back: found by paging
        D("MISC", 2026, 10, 14, "Quartalsaufstellung", "m1"),
    ]);

    [Fact]
    public async Task Signs_in_through_a_web_login_with_autofill_and_downloads_every_document()
    {
        var rig = new Rig();
        Seed(rig.Portal);
        var step = await rig.FetchAsync();
        Assert.Equal(StepStatus.Done, step.Status);
        Assert.Equal(1, rig.WebLogins);

        var docs = step.Value!;
        Assert.Equal(["baader:a1", "baader:a2", "baader:a3", "baader:m1", "baader:n1", "baader:t1"], docs.Select(d => d.Identifier).Order(StringComparer.Ordinal));
        Assert.All(docs, d => Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(d.Content, 0, 4), StringComparison.Ordinal));
        Assert.Equal("cash-1", docs.Single(d => d.Identifier == "baader:a1").AccountRemoteId);
        Assert.Equal(new DateOnly(2026, 10, 31), docs.Single(d => d.Identifier == "baader:a1").Date);
        Assert.Equal("Kontoauszug monatlich", docs.Single(d => d.Identifier == "baader:a1").Name);
        Assert.Equal("depot-1", docs.Single(d => d.Identifier == "baader:n1").AccountRemoteId);
        Assert.Null(docs.Single(d => d.Identifier == "baader:t1").AccountRemoteId);
        Assert.Null(docs.Single(d => d.Identifier == "baader:m1").AccountRemoteId);

        // The list requests carry the type, the period and the page token.
        var first = rig.Portal.ListRequests[0];
        Assert.Equal("ACCOUNT_STATEMENT", first["section"]);
        Assert.Equal("10.11.2026", first["to"]);
        Assert.Equal("12.08.2026", first["from"]);
        Assert.Equal(FakeBaaderPortal.PageToken, first["token"]);
    }

    [Fact]
    public async Task The_web_login_starts_on_the_portal_login_and_fills_in_the_stored_credentials()
    {
        var rig = new Rig();
        var step = await rig.Client.FetchAsync([Cash], new HashSet<string>(StringComparer.Ordinal), Credentials, null, default);
        Assert.Equal(StepStatus.Challenge, step.Status);
        var spec = step.Challenge!.WebLoginSpec!;
        Assert.Equal("https://konto.baaderbank.de/login", spec.StartUrl.ToString());
        Assert.True(spec.IsSuccess(new Uri("https://konto.baaderbank.de/dashboard"), []));
        Assert.False(spec.IsSuccess(new Uri("https://konto.baaderbank.de/login?referer=/fstatus"), []));
        Assert.Equal("ALICE1", spec.Autofill!.Username);
        Assert.Equal(BaaderPortal.UsernameSelector, spec.Autofill.UsernameSelector);
        Assert.DoesNotContain("Example-Password1!", spec.Autofill.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_stored_session_is_reused_and_known_documents_are_not_downloaded_again()
    {
        var rig = new Rig();
        Seed(rig.Portal);
        var first = await rig.FetchAsync();
        var known = first.Value!.Select(d => d.Identifier).ToHashSet(StringComparer.Ordinal);

        rig.Portal.Documents.Add(D("ACCOUNT_STATEMENT", 2026, 11, 9, "Kontoauszug monatlich", "a4"));
        rig.Portal.ListRequests.Clear();
        var second = await rig.FetchAsync(known);
        Assert.Equal(1, rig.WebLogins); // the session cookie still works
        Assert.Equal("baader:a4", Assert.Single(second.Value!).Identifier);
        Assert.Equal(1, rig.Portal.Downloads["a1"]);
        // Incremental: one period per type, plus one more where the newest held something new; not the whole history.
        Assert.Equal(BaaderPortal.Sections.Count + 1, rig.Portal.ListRequests.Count);
    }

    [Fact]
    public async Task The_first_download_pages_back_through_the_history_and_stops_at_the_limit()
    {
        var rig = new Rig();
        rig.Portal.Documents.Add(D("TAXES", 2024, 2, 1, "Old tax certificate", "old"));
        rig.Portal.Documents.Add(D("TAXES", 2020, 2, 1, "Too old", "ancient"));
        var docs = (await rig.FetchAsync()).Value!;
        Assert.Equal("baader:old", Assert.Single(docs).Identifier);
        Assert.Equal(BaaderPortal.FirstSyncWindows * BaaderPortal.Sections.Count, rig.Portal.ListRequests.Count);
        // Periods join without gaps or overlaps.
        var taxes = rig.Portal.ListRequests.Where(r => r["section"] == "TAXES").ToList();
        Assert.Equal(DateOnly.Parse(taxes[0]["from"], System.Globalization.CultureInfo.GetCultureInfo("de-DE")).AddDays(-1),
            DateOnly.Parse(taxes[1]["to"], System.Globalization.CultureInfo.GetCultureInfo("de-DE")));
    }

    [Fact]
    public async Task Documents_of_accounts_whose_statements_are_off_are_skipped()
    {
        var rig = new Rig();
        Seed(rig.Portal);
        var docs = (await rig.FetchAsync(accounts: [Depot])).Value!;
        Assert.DoesNotContain(docs, d => d.Identifier == "baader:a1");
        Assert.Equal("depot-1", docs.Single(d => d.Identifier == "baader:n1").AccountRemoteId);
        Assert.DoesNotContain(rig.Portal.ListRequests, r => r["section"] == "ACCOUNT_STATEMENT");
    }

    [Fact]
    public async Task A_document_that_is_not_a_pdf_is_left_for_the_next_sync()
    {
        var rig = new Rig();
        Seed(rig.Portal);
        rig.Portal.BrokenDownloads.Add("a2");
        var docs = (await rig.FetchAsync()).Value!;
        Assert.DoesNotContain(docs, d => d.Identifier == "baader:a2");
        Assert.Contains(docs, d => d.Identifier == "baader:a3");
        Assert.Contains(rig.Logs.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("did not return a PDF", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_portal_error_fails_the_fetch_with_its_message()
    {
        var rig = new Rig();
        rig.Portal.FailingSections.Add("TAXES");
        var ex = await Assert.ThrowsAsync<ConnectorException>(() => rig.FetchAsync());
        Assert.Contains("nicht verfügbar", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_sign_in_that_does_not_complete_fails()
    {
        var rig = new Rig();
        var ex = await Assert.ThrowsAsync<ConnectorException>(() => rig.FetchAsync(userSignsIn: false));
        Assert.Contains("did not complete", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_expired_session_asks_for_a_new_sign_in()
    {
        var rig = new Rig();
        Seed(rig.Portal);
        await rig.FetchAsync();
        rig.Portal.ExpireSessions();
        var step = await rig.FetchAsync();
        Assert.Equal(StepStatus.Done, step.Status);
        Assert.Equal(2, rig.WebLogins);
    }

    [Fact]
    public async Task Secrets_never_reach_the_log()
    {
        var rig = new Rig();
        Seed(rig.Portal);
        await rig.FetchAsync();
        Assert.DoesNotContain("Example-Password1!", rig.Logs.All, StringComparison.Ordinal);
        Assert.DoesNotContain("secretvalue", rig.Logs.All, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeBaaderPortal.PageToken, rig.Logs.All, StringComparison.Ordinal);
    }

    [Fact]
    public void Rows_parse_and_ignore_malformed_entries()
    {
        var json = JsonElement.Parse("""[{"2026-10-01":[[1,"Statement","abc123"],[0,"Bad id","../x"],["short"]],"not-a-date":[[1,"x","y"]]}]""");
        var doc = Assert.Single(BaaderPortal.ParseRows(json, "X"));
        Assert.Equal(("abc123", new DateOnly(2026, 10, 1), "Statement"), (doc.Id, doc.Date, doc.Title));
        Assert.Empty(BaaderPortal.ParseRows(JsonElement.Parse("""{"do":"error","title":"Aktuell sind in Ihrem Postfach keine Dokumente hinterlegt"}"""), "X"));
        Assert.Throws<ConnectorException>(() => BaaderPortal.ParseRows(JsonElement.Parse("""{"do":"redirect"}"""), "X"));
    }

    [Fact]
    public void Baader_now_offers_statements()
    {
        var registration = BaaderConnector.Registration(new FinTsOptions("X", "1"));
        Assert.True(registration.Statements);
    }
}
