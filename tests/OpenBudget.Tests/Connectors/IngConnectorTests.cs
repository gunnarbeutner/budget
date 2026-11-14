#pragma warning disable CA2000 // test objects live for the test
using OpenBudget.Browser;
using OpenBudget.Connectors;
using OpenBudget.Connectors.FinTs;
using OpenBudget.Connectors.Ing;
using OpenBudget.Connectors.Sync;
using OpenBudget.Models;
using OpenBudget.Services;
using OpenBudget.Tests.Connectors.FinTs;
using Xunit;

namespace OpenBudget.Tests.Connectors;

/// <summary>
/// The ING connector (FinTS + web banking for VL-Sparen and the Postbox) against a made-up ING (no network): the web
/// banking's sign-in runs as a web login in a browser that plays ING's sign-in page, and the web requests run in that
/// kept browser.
/// </summary>
public sealed class IngConnectorTests
{
    private const string Pin = "654321PIN";

    /// <summary>The pause before each sign-in step is submitted: long enough to beat <see cref="FakeIng.RequestBlockMs"/>, short for the suite.</summary>
    private static readonly TimeSpan SignInPause = TimeSpan.FromMilliseconds(300);

    private sealed class Rig : IAsyncDisposable
    {
        public required BankWorld World { get; init; }

        public required FakeIng Web { get; init; }

        public required FakeIngBrowserHost Browser { get; init; }

        public required CapturingLoggerFactory Logs { get; init; }

        public int LoginId { get; set; }

        public BankLoginService Service => World.For(BankWorld.Alice);

        public ValueTask DisposeAsync() => World.DisposeAsync();

        public Task<BankSyncStatus> SyncAsync(bool transactions, bool statements, params string[] answers) =>
            World.SyncAsync(BankWorld.Alice, LoginId, new SyncRequest(LoginId, transactions, statements), answers);
    }

    /// <summary>A rig; <paramref name="userPicks"/> is the device the user picks in the page when the app has no remembered one.</summary>
    private static async Task<Rig> CreateAsync(Action<FakeIng>? setup = null, string pin = Pin, int? userPicks = 0)
    {
        var bank = new SyntheticBank { Pin = Pin };
        bank.UseCamt();
        bank.UseBalance();
        bank.Handlers["HKWPD"] = (s, rb, _) => rb.Segment("HIWPD", 6, s.Number, SyntheticBank.Bin(":16R:GENL\r\n:16S:GENL\r\n-"));
        var server = new MockFinTsServer(bank.Respond);
        var web = new FakeIng();
        setup?.Invoke(web);
        var logs = new CapturingLoggerFactory();
        var browser = new FakeIngBrowserHost(web) { UserPicksDevice = userPicks, Pause = SignInPause };
        var world = await BankWorld.CreateAsync(extras: new BankWorldExtras
        {
            Registrations = [IngConnector.Registration(FinTsHarness.Options, _ => new FinTsHttpTransport(new BankHttpClient(server), IngConnector.Config.Url), signInPause: SignInPause)],
            HttpFactory = () => new BankHttpClient(jar => new CookieRedirectHandler(jar, web)),
            Loggers = logs,
            BrowserHost = browser,
            WebLoginOptions = new WebLoginOptions { PollInterval = TimeSpan.FromMilliseconds(10), SettleDelay = TimeSpan.Zero, DismissWait = TimeSpan.FromMilliseconds(50), StuckAfter = TimeSpan.FromSeconds(1), Timeout = TimeSpan.FromSeconds(15) },
        });
        var view = await world.For(BankWorld.Alice).CreateLoginAsync(IngConnector.TypeName, "ING", FakeIng.Alias, pin);
        return new Rig { World = world, Web = web, Browser = browser, Logs = logs, LoginId = view.Id };
    }

    [Fact]
    public void Ing_offers_statements()
    {
        var registration = IngConnector.Registration(FinTsHarness.Options);
        Assert.True(registration.Statements);
        Assert.Equal(CredentialRequirement.UsernameAndSecret, registration.Credentials);
    }

    [Fact]
    public async Task The_first_sync_signs_in_to_the_web_banking_once_and_lists_the_VL_account()
    {
        await using var rig = await CreateAsync(userPicks: 1);
        var status = await rig.SyncAsync(true, false);
        Assert.True(status.Succeeded, status.Error);
        // two devices and none remembered: the page is shown and the user picks one there
        Assert.Contains("device choice: 2 devices, none remembered", rig.Browser.ShownToUser);
        Assert.Equal("tablet / Example Tablet", rig.Web.ChosenDevice);
        Assert.Equal(1, rig.Web.Count("POST /app/login/api/authenticate"));
        Assert.Equal(3, rig.Web.Count("/app/login/api/status")); // two "still waiting" polls, then the forward
        Assert.Equal(1, rig.Web.SignIns);
        Assert.Equal(1, rig.Web.Pushes); // the device choice pushed the confirmation once, the polls did not
        Assert.Equal(0, rig.Web.SilentApprovals);
        Assert.Equal(0, rig.Web.Count("/app/logout"));

        var accounts = await rig.Service.GetAccountsAsync(rig.LoginId);
        var vl = Assert.Single(accounts, a => a.RemoteId == FakeIng.VlIban);
        Assert.Equal(RemoteAccountType.Savings, vl.Type);
        Assert.Equal("VL-Sparen ...0077", vl.Name);
        var credit = Assert.Single(accounts, a => a.RemoteId == FakeIng.CreditIban);
        Assert.Equal(RemoteAccountType.Loan, credit.Type);
        Assert.Equal("Rahmenkredit ...0099", credit.Name);
        // The Girokonto is on the overview too, but it comes from FinTS: listed once.
        Assert.Single(accounts, a => a.Iban == FakeIng.GiroIban);
        Assert.Equal(accounts.Count, accounts.Select(a => a.RemoteId).Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain(FakeIng.CreditIban, rig.Logs.All, StringComparison.Ordinal);

        // Discovery is remembered: the next transactions-only sync of the FinTS accounts does not touch the web banking.
        rig.Web.ExpireSessions();
        var before = rig.Web.Requests.Count;
        var browsers = rig.Browser.Opened;
        Assert.True((await rig.SyncAsync(true, false)).Succeeded);
        Assert.Equal(before, rig.Web.Requests.Count);
        Assert.Equal(browsers, rig.Browser.Opened);

        Assert.DoesNotContain(Pin, rig.Logs.All, StringComparison.Ordinal);
        Assert.DoesNotContain("secretvalue", rig.Logs.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VL_transactions_and_balance_come_from_the_CSV_export()
    {
        await using var rig = await CreateAsync();
        Assert.True((await rig.SyncAsync(true, false)).Succeeded);
        await rig.World.LinkAllAsync(BankWorld.Alice, rig.LoginId);

        // A new sync: the stored session still opens the overview in the browser, so there is no new sign-in.
        var status = await rig.SyncAsync(true, false);
        Assert.True(status.Succeeded, status.Error);
        Assert.Equal(1, rig.Web.Count("/app/login/api/authenticate")); // still the first sign-in only
        Assert.Equal(1, rig.Web.SignIns);
        var vl = await rig.World.RemoteAsync(BankWorld.Alice, rig.LoginId, FakeIng.VlIban);
        var txs = await rig.World.TransactionsAsync(vl.LinkedAccountId!.Value);
        Assert.Equal(2, txs.Count);
        Assert.Single(txs, t => t.Amount == 4000 && t.ExternalId == "id:REF-2");
        Assert.Single(txs, t => t.Amount == 100000 && t.Purpose == "VL Rate; Oktober");
        Assert.Contains("1040", (await rig.World.AccountAsync(vl.LinkedAccountId.Value)).Balances, StringComparison.Ordinal);
        // The date filter never asks for more than the page allows.
        Assert.All(rig.Web.Filters, f => Assert.Equal("09.11.2026", f.To));
        Assert.Equal("15.10.2025", rig.Web.Filters[0].From);
    }

    [Fact]
    public async Task Every_sign_in_pushes_the_confirmation_once_also_with_the_remembered_device()
    {
        await using var rig = await CreateAsync(w => { w.RequestBlockMs = 150; w.PendingPolls = 4; });
        Assert.True((await rig.SyncAsync(true, false)).Succeeded);
        Assert.Equal(1, rig.Web.Pushes);
        await rig.World.LinkAllAsync(BankWorld.Alice, rig.LoginId);

        // Next sync: a fresh sign-in where the remembered device is chosen in the page without the user. Sent the moment
        // the page arrived, ING would create the confirmation without pushing it; it waits like a person instead.
        rig.Web.ExpireSessions();
        rig.Browser.UserPicksDevice = null;
        rig.Browser.ShownToUser.Clear();
        Assert.True((await rig.SyncAsync(true, false)).Succeeded);
        Assert.Empty(rig.Browser.ShownToUser);
        Assert.Equal(2, rig.Web.Count("/app/login/api/authenticate"));
        Assert.Equal(2, rig.Web.Count("/app/login/api/device"));
        Assert.Equal("phone / Example Phone", rig.Web.ChosenDevice);
        Assert.Equal(2, rig.Web.Pushes);
        Assert.Equal(0, rig.Web.SilentApprovals);
        Assert.Equal(10, rig.Web.Count("/app/login/api/status")); // polling never re-submits the device choice
    }

    [Fact]
    public async Task A_single_device_is_pushed_by_the_paced_sign_in_itself()
    {
        await using var rig = await CreateAsync(w => { w.Devices = ["phone / Example Phone"]; w.RequestBlockMs = 150; }, userPicks: null);
        Assert.True((await rig.SyncAsync(true, false)).Succeeded);
        Assert.Equal(0, rig.Web.Count("/app/login/api/device"));
        Assert.Empty(rig.Browser.ShownToUser);
        Assert.Equal((1, 0), (rig.Web.Pushes, rig.Web.SilentApprovals));
    }

    [Fact]
    public async Task Rahmenkredit_transactions_come_from_the_CSV_export_and_its_balance_is_negative()
    {
        await using var rig = await CreateAsync();
        Assert.True((await rig.SyncAsync(true, false)).Succeeded);
        await rig.World.LinkAllAsync(BankWorld.Alice, rig.LoginId);
        var status = await rig.SyncAsync(true, false);
        Assert.True(status.Succeeded, status.Error);

        var credit = await rig.World.RemoteAsync(BankWorld.Alice, rig.LoginId, FakeIng.CreditIban);
        var txs = await rig.World.TransactionsAsync(credit.LinkedAccountId!.Value);
        Assert.Equal(2, txs.Count);
        Assert.Single(txs, t => t.Amount == -200000 && t.ExternalId == "id:CR-1");
        Assert.Single(txs, t => t.Amount == 50000 && t.ExternalId == "id:CR-2");
        var account = await rig.World.AccountAsync(credit.LinkedAccountId.Value);
        Assert.Contains("-1500", account.Balances, StringComparison.Ordinal);

        // The next sync asks only for what is new since the last one.
        var filters = rig.Web.Filters.Count;
        Assert.True((await rig.SyncAsync(true, false)).Succeeded);
        Assert.True(rig.Web.Filters.Count > filters);
        Assert.NotEqual("15.10.2025", rig.Web.Filters[^1].From);
    }

    [Fact]
    public async Task Accounts_discovered_before_the_Rahmenkredit_was_supported_are_discovered_again_once()
    {
        await using var rig = await CreateAsync();
        Assert.True((await rig.SyncAsync(true, false)).Succeeded);
        Assert.Equal(1, rig.Web.Count("/app/login/api/authenticate"));
        // Simulate a session stored by the older version: VL only, no products version.
        await rig.World.EditSessionAsync(rig.LoginId, values =>
        {
            values[IngConnector.WebAccountsKey] = "[{\"Iban\":\"" + FakeIng.VlIban + "\",\"Name\":\"VL-Sparen ...0077\"}]";
            values.Remove(IngConnector.WebProductsKey);
        });
        rig.Web.ExpireSessions();
        Assert.True((await rig.SyncAsync(true, false)).Succeeded);
        Assert.Equal(2, rig.Web.Count("/app/login/api/authenticate"));
        var accounts = await rig.Service.GetAccountsAsync(rig.LoginId);
        Assert.Equal(RemoteAccountType.Savings, Assert.Single(accounts, a => a.RemoteId == FakeIng.VlIban).Type);
        Assert.Single(accounts, a => a.RemoteId == FakeIng.CreditIban);
    }

    [Fact]
    public async Task Postbox_documents_are_downloaded_once_assigned_by_IBAN_and_broken_ones_skipped()
    {
        await using var rig = await CreateAsync();
        Assert.True((await rig.SyncAsync(true, false)).Succeeded);
        await rig.World.LinkAllAsync(BankWorld.Alice, rig.LoginId);
        var giro = await rig.World.RemoteAsync(BankWorld.Alice, rig.LoginId, SyntheticBank.Iban);
        await rig.Service.SetStatementsAsync(giro.Id, true, StatementsInterval.ManualOnly);
        rig.Web.ExpireSessions(); // a fresh sign-in, the device is remembered

        var status = await rig.SyncAsync(false, true);
        Assert.True(status.Succeeded, status.Error);
        // the first fetch reads the whole ten years a year at a time ("Freier Zeitraum"), both postboxes
        Assert.Equal(2, rig.Web.Ranges.Count(r => r == "DATUM"));
        Assert.DoesNotContain(rig.Web.Ranges, r => r.StartsWith("MONATE", StringComparison.Ordinal));
        Assert.Contains("2026-11-09", rig.Web.Ranges[1], StringComparison.Ordinal);
        Assert.Contains("2016-11-09..", rig.Web.Ranges[rig.Web.Ranges.IndexOf("DATUM", 1) - 1], StringComparison.Ordinal);
        var firstPass = rig.Web.Ranges.Count;

        var list = await rig.Service.GetLoginStatementsAsync(rig.LoginId);
        Assert.Equal(4, list.Count);
        Assert.Single(list, s => s.AccountId == giro.LinkedAccountId && s.Name.StartsWith("Kontoauszug: Girokonto", StringComparison.Ordinal));
        Assert.Single(list, s => s.AccountId == null && s.Name == "Jahressteuerbescheinigung");
        Assert.Single(list, s => s.AccountId == null && s.Name == "Depotauszug: Depot 0000000001");
        Assert.Single(list, s => s.AccountId == null && s.Name == "Information: Example letter & notes");
        Assert.DoesNotContain("vl-oct", rig.Web.Downloads.Keys, StringComparer.Ordinal); // its account has statements off
        Assert.Equal(1, rig.Web.Downloads["broken"]);

        // Again, with a new document: only the new one (and the broken one, retried) is downloaded; the last year is enough now.
        rig.Web.Documents.Add(new FakeIng.Doc("giro-dec", 1, "Kontoauszug", "Girokonto 1234567 / DE00 0000 0000 0000 0000 11", new DateOnly(2026, 11, 8)));
        Assert.True((await rig.SyncAsync(false, true)).Succeeded);
        Assert.Equal(["MONATE12", "MONATE12"], rig.Web.Ranges.Skip(firstPass));
        Assert.Equal(1, rig.Web.Downloads["giro-nov"]);
        Assert.Equal(1, rig.Web.Downloads["giro-dec"]);
        Assert.Equal(5, (await rig.Service.GetLoginStatementsAsync(rig.LoginId)).Count);
        Assert.Contains("did not download as a PDF", rig.Logs.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_large_Postbox_is_downloaded_in_batches_within_one_sync()
    {
        await using var rig = await CreateAsync();
        Assert.True((await rig.SyncAsync(true, false)).Succeeded);
        await rig.World.LinkAllAsync(BankWorld.Alice, rig.LoginId);
        var giro = await rig.World.RemoteAsync(BankWorld.Alice, rig.LoginId, SyntheticBank.Iban);
        await rig.Service.SetStatementsAsync(giro.Id, true, StatementsInterval.ManualOnly);
        var before = (await rig.Service.GetLoginStatementsAsync(rig.LoginId)).Count;
        var extra = IngConnector.PostboxBatch * 2 + 5;
        for (var i = 0; i < extra; i++)
            rig.Web.Documents.Add(new FakeIng.Doc($"letter-{i}", 1, "Information", $"Example letter {i}", new DateOnly(2024, 1, 1).AddDays(i)));
        rig.Web.ExpireSessions();

        Assert.True((await rig.SyncAsync(false, true)).Succeeded);
        // every document arrives in this one sync, a batch at a time, and the next sync only reads the recent listing
        Assert.True((await rig.Service.GetLoginStatementsAsync(rig.LoginId)).Count >= before + extra);
        Assert.Contains("to follow", rig.Logs.All, StringComparison.Ordinal);
        Assert.Equal(2, rig.Web.Ranges.Count(r => r == "DATUM")); // one listing for all batches
        Assert.True((await rig.SyncAsync(false, true)).Succeeded);
        Assert.Equal("MONATE12", rig.Web.Ranges[^1]);
    }

    [Fact]
    public async Task A_Postbox_beyond_ING_listing_cap_is_read_in_smaller_windows()
    {
        await using var rig = await CreateAsync();
        Assert.True((await rig.SyncAsync(true, false)).Succeeded);
        await rig.World.LinkAllAsync(BankWorld.Alice, rig.LoginId);
        var giro = await rig.World.RemoteAsync(BankWorld.Alice, rig.LoginId, SyntheticBank.Iban);
        await rig.Service.SetStatementsAsync(giro.Id, true, StatementsInterval.ManualOnly);
        var before = (await rig.Service.GetLoginStatementsAsync(rig.LoginId)).Count;
        // 520 letters within one year (more than ING lists at once), 20 more spread over older years; two a day
        for (var i = 0; i < 520; i++)
            rig.Web.Documents.Add(new FakeIng.Doc($"recent-{i}", 1, "Information", $"Recent letter {i % 2}", new DateOnly(2025, 1, 1).AddDays(i / 2)));
        for (var i = 0; i < 20; i++)
            rig.Web.Documents.Add(new FakeIng.Doc($"older-{i}", 2, "Information", "Older letter", new DateOnly(2017, 1, 1).AddDays(i * 100)));
        rig.Web.ExpireSessions();

        Assert.True((await rig.SyncAsync(false, true)).Succeeded);
        var list = await rig.Service.GetLoginStatementsAsync(rig.LoginId);
        Assert.Equal(before + 540 + 4, list.Count); // plus the fixture's four (giro, tax, depot, joint letter)
        Assert.All(rig.Web.Documents.Where(d => d.Id.StartsWith("recent-", StringComparison.Ordinal) || d.Id.StartsWith("older-", StringComparison.Ordinal)),
            d => Assert.Equal(1, rig.Web.Downloads[d.Id]));
        Assert.Contains("listed in 2 postbox(es) over all years", rig.Logs.All, StringComparison.Ordinal);

        // Nothing is downloaded twice by the next syncs.
        Assert.True((await rig.SyncAsync(false, true)).Succeeded);
        Assert.Equal(before + 544, (await rig.Service.GetLoginStatementsAsync(rig.LoginId)).Count);
    }

    [Fact]
    public async Task Switching_statements_on_later_fetches_the_older_documents_of_that_account()
    {
        await using var rig = await CreateAsync();
        Assert.True((await rig.SyncAsync(true, false)).Succeeded);
        await rig.World.LinkAllAsync(BankWorld.Alice, rig.LoginId);
        var giro = await rig.World.RemoteAsync(BankWorld.Alice, rig.LoginId, SyntheticBank.Iban);
        await rig.Service.SetStatementsAsync(giro.Id, true, StatementsInterval.ManualOnly);
        rig.Web.Documents.Add(new FakeIng.Doc("vl-2019", 1, "Kontoauszug", "VL-Sparen 7777777 / DE00 0000 0000 0000 0000 77", new DateOnly(2019, 12, 31)));
        rig.Web.ExpireSessions();
        Assert.True((await rig.SyncAsync(false, true)).Succeeded);
        Assert.DoesNotContain("vl-oct", rig.Web.Downloads.Keys, StringComparer.Ordinal);
        Assert.Contains("2 for accounts with statements off", rig.Logs.All, StringComparison.Ordinal);

        // The pass is complete: the next sync only reads the last year.
        Assert.True((await rig.SyncAsync(false, true)).Succeeded);
        Assert.Equal("MONATE12", rig.Web.Ranges[^1]);

        // Switching the VL account's statements on brings a full pass again, with its document from years ago.
        var vl = await rig.World.RemoteAsync(BankWorld.Alice, rig.LoginId, FakeIng.VlIban);
        await rig.Service.SetStatementsAsync(vl.Id, true, StatementsInterval.ManualOnly);
        var ranges = rig.Web.Ranges.Count;
        Assert.True((await rig.SyncAsync(false, true)).Succeeded);
        Assert.Contains("DATUM", rig.Web.Ranges.Skip(ranges));
        Assert.Equal(1, rig.Web.Downloads["vl-oct"]);
        Assert.Equal(1, rig.Web.Downloads["vl-2019"]);
        var list = await rig.Service.GetLoginStatementsAsync(rig.LoginId);
        Assert.Equal(2, list.Count(s => s.AccountId == vl.LinkedAccountId));
        Assert.Equal(1, rig.Web.Downloads["giro-nov"]);

        // And then the recent listing is enough again.
        Assert.True((await rig.SyncAsync(false, true)).Succeeded);
        Assert.Equal("MONATE12", rig.Web.Ranges[^1]);
    }

    [Fact]
    public async Task A_rejected_confirmation_does_not_fail_the_FinTS_sync_and_is_not_retried_at_once()
    {
        await using var rig = await CreateAsync(w => w.Reject = true);
        var status = await rig.SyncAsync(true, false);
        Assert.True(status.Succeeded, status.Error);
        Assert.DoesNotContain(await rig.Service.GetAccountsAsync(rig.LoginId), a => a.RemoteId == FakeIng.VlIban);
        Assert.Contains("did not confirm", rig.Logs.All, StringComparison.Ordinal);

        rig.Web.Reject = false;
        Assert.True((await rig.SyncAsync(true, false)).Succeeded);
        Assert.Equal(1, rig.Web.Count("/app/login/api/authenticate"));

        // A day later discovery is tried again.
        rig.World.Clock.Offset += TimeSpan.FromDays(2);
        Assert.True((await rig.SyncAsync(true, false)).Succeeded);
        Assert.Contains(await rig.Service.GetAccountsAsync(rig.LoginId), a => a.RemoteId == FakeIng.VlIban);
    }

    [Fact]
    public async Task A_refused_web_PIN_fails_the_statements_with_a_clear_message()
    {
        await using var rig = await CreateAsync(w => w.Devices = ["phone / Example Phone"], userPicks: null);
        Assert.True((await rig.SyncAsync(true, false)).Succeeded); // one device: no question
        await rig.World.LinkAllAsync(BankWorld.Alice, rig.LoginId);
        var giro = await rig.World.RemoteAsync(BankWorld.Alice, rig.LoginId, SyntheticBank.Iban);
        await rig.Service.SetStatementsAsync(giro.Id, true, StatementsInterval.ManualOnly);
        rig.Web.ExpireSessions();
        rig.Web.Pin = "other";

        var status = await rig.SyncAsync(false, true);
        Assert.False(status.Succeeded);
        Assert.Contains("did not accept the access number or PIN", status.Error, StringComparison.Ordinal);
        Assert.Empty(rig.Web.Downloads);
    }

    // ---- the web client in a kept browser ----

    /// <summary>A kept browser showing a fixed page whose requests all end where <see cref="Answer"/> says.</summary>
    private sealed class StaticBrowser(BrowserPage page) : IBrowserFetcher
    {
        public bool IsOpen { get; set; } = true;

        public Func<BrowserFetchRequest, BrowserFetchResponse> Answer { get; set; } = r => new BrowserFetchResponse(200, r.Url, "text/html", []);

        public List<Uri> Navigated { get; } = [];

        public Task<BrowserFetchResponse> SendAsync(BrowserFetchRequest request, CancellationToken ct) => Task.FromResult(Answer(request));

        public Task<Uri?> ResolveRedirectAsync(Uri url, CancellationToken ct) => Task.FromResult<Uri?>(null);

        public Task NavigateAsync(Uri url, CancellationToken ct)
        {
            Navigated.Add(url);
            return Task.CompletedTask;
        }

        public Task<BrowserPage> PageAsync(CancellationToken ct) => Task.FromResult(page);
    }

    private static readonly Uri OverviewUrl = new("https://banking.ing.de/app/obligo/obligo?x=ov");

    [Fact]
    public async Task The_web_client_reads_the_overview_in_the_kept_browser_and_notices_an_ended_session()
    {
        var logs = new CapturingLoggerFactory();
        var client = new IngWebClient(null, TimeProvider.System, logs.CreateLogger("ing"));
        var browser = new StaticBrowser(new BrowserPage(OverviewUrl, FakeIng.Overview()))
        {
            // ING sends a request of an ended session to its sign-in page
            Answer = r => new BrowserFetchResponse(200, new Uri("https://banking.ing.de/app/login/oauth"), "text/html", "<html></html>"u8.ToArray()),
        };
        await client.AttachAsync(browser, default);
        Assert.True(client.LoggedIn);
        Assert.Equal(new Uri("https://banking.ing.de/app/obligo"), Assert.Single(browser.Navigated));
        Assert.Equal([FakeIng.GiroIban, FakeIng.VlIban, FakeIng.CreditIban], client.Accounts.Select(a => a.Iban));

        var ex = await Assert.ThrowsAsync<ConnectorException>(() => client.TransactionsAsync(FakeIng.VlIban, null, default));
        Assert.Contains("ended the web banking session", ex.Message, StringComparison.Ordinal);
        Assert.False(client.LoggedIn); // the next step signs in again

        browser.IsOpen = false;
        await Assert.ThrowsAsync<ConnectorException>(() => client.DownloadAsync(new Uri("https://banking.ing.de/app/postbox/postbox?x=dl-1"), default));
    }

    [Fact]
    public async Task A_page_that_is_not_the_overview_is_logged_by_its_shape_only()
    {
        var logs = new CapturingLoggerFactory();
        var client = new IngWebClient(null, TimeProvider.System, logs.CreateLogger("ing"));
        var notice = new BrowserPage(new Uri("https://banking.ing.de/app/notice?x=1"),
            "<html><head><title>Hinweis 2026</title></head><body><h1>Neue Bedingungen</h1><form id=\"f1\"><input name=\"iban\" value=\"DE00000000000000000077\"></form>"
            + "<button class=\"button orange\">Zustimmen</button></body></html>");
        var ex = await Assert.ThrowsAsync<ConnectorException>(() => client.AttachAsync(new StaticBrowser(notice), default));
        Assert.Contains("did not show the account overview", ex.Message, StringComparison.Ordinal);
        Assert.Contains("path=/app/notice; title=Hinweis ####; headings=Neue Bedingungen; forms=f#; buttons=Zustimmen", logs.All, StringComparison.Ordinal);
        Assert.DoesNotContain("0077", logs.All, StringComparison.Ordinal);
        Assert.False(client.LoggedIn);
    }

    [Fact]
    public void The_web_login_keeps_the_browser_resumes_the_overview_and_reports_failures()
    {
        var client = new IngWebClient(null, TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance) { PreferredDevice = "phone / Example Phone" };
        var spec = client.WebLogin(new ConnectorCredentials(FakeIng.Alias, Pin));
        Assert.True(spec.KeepBrowser);
        Assert.True(spec.ReportFailure);
        Assert.Equal(new Uri("https://banking.ing.de/app/obligo"), spec.ResumeUrl);
        Assert.Equal(["ing.de"], spec.CookieDomains);
        Assert.Contains("phone / Example Phone", spec.WaitingArgument, StringComparison.Ordinal);
        Assert.DoesNotContain("__COOKIE__", spec.WaitingScript, StringComparison.Ordinal);
        Assert.DoesNotContain("__PAUSE__", spec.WaitingScript, StringComparison.Ordinal);
        Assert.DoesNotContain(Pin, spec.Autofill!.ToString(), StringComparison.Ordinal);
        Assert.Equal(IngWebClient.PasswordSelector, spec.Autofill!.PasswordSelector);
    }

    // ---- parsing pieces ----

    [Fact]
    public void The_CSV_parser_reads_Latin1_quotes_and_the_balance()
    {
        var csv = IngHtml.ParseCsv(IngHtml.DecodeCsv(System.Text.Encoding.Latin1.GetBytes(
            "Saldo;-1.234,56;EUR\nBuchung;Valuta;Auftraggeber/Empfänger;Buchungstext;Verwendungszweck;Saldo;Währung;Betrag;Währung\n"
            + "03.11.2026;04.11.2026;Bäckerei Example;Lastschrift;\"Brot \"\"frisch\"\"\";-1.234,56;EUR;-3,20;EUR\n")));
        Assert.Equal(-1234.56m, csv.Balance);
        var t = Assert.Single(csv.Transactions);
        Assert.Equal((-3.20m, "Bäckerei Example", "Brot \"frisch\"", new DateOnly(2026, 11, 4)), (t.Amount, t.Name, t.Purpose, t.ValueDate!.Value));
        Assert.Throws<ConnectorException>(() => IngHtml.ParseCsv("nothing here"));
    }

    [Fact]
    public void IBANs_are_found_in_postbox_subjects()
    {
        Assert.Equal("DE00000000000000000011", IngHtml.FindIban("Girokonto 1234567 / DE00 0000 0000 0000 0000 11"));
        Assert.Null(IngHtml.FindIban("Sparkonten"));
    }
}
