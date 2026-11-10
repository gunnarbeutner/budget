#pragma warning disable CA2000 // test objects live for the test
using OpenBudget.Connectors;
using OpenBudget.Connectors.FinTs;
using OpenBudget.Connectors.Ing;
using OpenBudget.Connectors.Sync;
using OpenBudget.Models;
using OpenBudget.Services;
using OpenBudget.Tests.Connectors.FinTs;
using Xunit;

namespace OpenBudget.Tests.Connectors;

/// <summary>The ING connector (FinTS + web banking for VL-Sparen and the Postbox) against a made-up ING (no network).</summary>
public sealed class IngConnectorTests
{
    private const string Pin = "654321PIN";

    private sealed class Rig : IAsyncDisposable
    {
        public required BankWorld World { get; init; }

        public required FakeIng Web { get; init; }

        public required CapturingLoggerFactory Logs { get; init; }

        public int LoginId { get; set; }

        public BankLoginService Service => World.For(BankWorld.Alice);

        public ValueTask DisposeAsync() => World.DisposeAsync();

        public Task<BankSyncStatus> SyncAsync(bool transactions, bool statements, params string[] answers) =>
            World.SyncAsync(BankWorld.Alice, LoginId, new SyncRequest(LoginId, transactions, statements), answers);
    }

    private static async Task<Rig> CreateAsync(Action<FakeIng>? setup = null, string pin = Pin)
    {
        var bank = new SyntheticBank { Pin = Pin };
        bank.UseCamt();
        bank.UseBalance();
        bank.Handlers["HKWPD"] = (s, rb, _) => rb.Segment("HIWPD", 6, s.Number, SyntheticBank.Bin(":16R:GENL\r\n:16S:GENL\r\n-"));
        var server = new MockFinTsServer(bank.Respond);
        var web = new FakeIng();
        setup?.Invoke(web);
        var logs = new CapturingLoggerFactory();
        var world = await BankWorld.CreateAsync(extras: new BankWorldExtras
        {
            Registrations = [IngConnector.Registration(FinTsHarness.Options, _ => new FinTsHttpTransport(new BankHttpClient(server), IngConnector.Config.Url))],
            HttpFactory = web.CreateClient,
            Loggers = logs,
        });
        var view = await world.For(BankWorld.Alice).CreateLoginAsync(IngConnector.TypeName, "ING", FakeIng.Alias, pin);
        return new Rig { World = world, Web = web, Logs = logs, LoginId = view.Id };
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
        await using var rig = await CreateAsync();
        var status = await rig.SyncAsync(true, false, "1");
        Assert.True(status.Succeeded, status.Error);
        Assert.Equal("tablet / Example Tablet", rig.Web.ChosenDevice);
        Assert.Equal(1, rig.Web.Count("POST access.ing.de/delogin/w/login?x=submit"));
        Assert.Equal(3, rig.Web.Count("x=status")); // two "still waiting" polls, then the redirect
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
        Assert.True((await rig.SyncAsync(true, false)).Succeeded);
        Assert.Equal(before, rig.Web.Requests.Count);

        Assert.DoesNotContain(Pin, rig.Logs.All, StringComparison.Ordinal);
        Assert.DoesNotContain("secretvalue", rig.Logs.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VL_transactions_and_balance_come_from_the_CSV_export()
    {
        await using var rig = await CreateAsync();
        Assert.True((await rig.SyncAsync(true, false, "0")).Succeeded);
        await rig.World.LinkAllAsync(BankWorld.Alice, rig.LoginId);

        // A new sync: the remembered device is used again without asking, the session cookie is reused.
        var status = await rig.SyncAsync(true, false);
        Assert.True(status.Succeeded, status.Error);
        Assert.Equal(1, rig.Web.Count("x=submit")); // still the first sign-in only
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
        await using var rig = await CreateAsync(w => { w.RequestBlockMs = 300; w.PendingPolls = 4; });
        Assert.True((await rig.SyncAsync(true, false, "0")).Succeeded);
        Assert.Equal(1, rig.Web.Pushes);
        await rig.World.LinkAllAsync(BankWorld.Alice, rig.LoginId);

        // Next sync: a fresh sign-in where the remembered device is submitted without a question. Sent the moment the
        // page arrived, ING would create the confirmation without pushing it; it waits like a person instead.
        rig.Web.ExpireSessions();
        Assert.True((await rig.SyncAsync(true, false)).Succeeded);
        Assert.Equal(2, rig.Web.Count("x=submit"));
        Assert.Equal(2, rig.Web.Count("x=device"));
        Assert.Equal(2, rig.Web.Pushes);
        Assert.Equal(0, rig.Web.SilentApprovals);
        Assert.Equal(10, rig.Web.Count("x=status")); // polling never re-submits the device choice
    }

    [Fact]
    public async Task A_single_device_is_pushed_by_the_paced_sign_in_itself()
    {
        await using var rig = await CreateAsync(w => { w.Devices = ["phone / Example Phone"]; w.RequestBlockMs = 300; });
        Assert.True((await rig.SyncAsync(true, false)).Succeeded);
        Assert.Equal(0, rig.Web.Count("x=device"));
        Assert.Equal((1, 0), (rig.Web.Pushes, rig.Web.SilentApprovals));
    }

    [Fact]
    public void The_submit_block_is_read_from_the_page()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(1000), IngHtml.RequestBlock("$(document).submit(function(event) { return RequestBlocker.checkEvent(event, 1000); });"));
        Assert.Equal(TimeSpan.FromMilliseconds(1000), IngHtml.RequestBlock("<p>no blocker</p>"));
    }

    [Fact]
    public async Task Rahmenkredit_transactions_come_from_the_CSV_export_and_its_balance_is_negative()
    {
        await using var rig = await CreateAsync();
        Assert.True((await rig.SyncAsync(true, false, "0")).Succeeded);
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
        Assert.True((await rig.SyncAsync(true, false, "0")).Succeeded);
        Assert.Equal(1, rig.Web.Count("x=submit"));
        // Simulate a session stored by the older version: VL only, no products version.
        await rig.World.EditSessionAsync(rig.LoginId, values =>
        {
            values[IngConnector.WebAccountsKey] = "[{\"Iban\":\"" + FakeIng.VlIban + "\",\"Name\":\"VL-Sparen ...0077\"}]";
            values.Remove(IngConnector.WebProductsKey);
        });
        rig.Web.ExpireSessions();
        Assert.True((await rig.SyncAsync(true, false, "0")).Succeeded);
        Assert.Equal(2, rig.Web.Count("x=submit"));
        var accounts = await rig.Service.GetAccountsAsync(rig.LoginId);
        Assert.Equal(RemoteAccountType.Savings, Assert.Single(accounts, a => a.RemoteId == FakeIng.VlIban).Type);
        Assert.Single(accounts, a => a.RemoteId == FakeIng.CreditIban);
    }

    [Fact]
    public async Task Postbox_documents_are_downloaded_once_assigned_by_IBAN_and_broken_ones_skipped()
    {
        await using var rig = await CreateAsync();
        Assert.True((await rig.SyncAsync(true, false, "0")).Succeeded);
        await rig.World.LinkAllAsync(BankWorld.Alice, rig.LoginId);
        var giro = await rig.World.RemoteAsync(BankWorld.Alice, rig.LoginId, SyntheticBank.Iban);
        await rig.Service.SetStatementsAsync(giro.Id, true, StatementsInterval.ManualOnly);
        rig.Web.ExpireSessions(); // a fresh sign-in, the device is remembered

        var status = await rig.SyncAsync(false, true);
        Assert.True(status.Succeeded, status.Error);
        Assert.Equal(["MONATE120", "MONATE120"], rig.Web.Ranges); // the first fetch reads the whole ten years, both postboxes

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
        Assert.Equal(["MONATE120", "MONATE120", "MONATE12", "MONATE12"], rig.Web.Ranges);
        Assert.Equal(1, rig.Web.Downloads["giro-nov"]);
        Assert.Equal(1, rig.Web.Downloads["giro-dec"]);
        Assert.Equal(5, (await rig.Service.GetLoginStatementsAsync(rig.LoginId)).Count);
        Assert.Contains("did not download as a PDF", rig.Logs.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rejected_confirmation_does_not_fail_the_FinTS_sync_and_is_not_retried_at_once()
    {
        await using var rig = await CreateAsync(w => w.Reject = true);
        var status = await rig.SyncAsync(true, false, "0");
        Assert.True(status.Succeeded, status.Error);
        Assert.DoesNotContain(await rig.Service.GetAccountsAsync(rig.LoginId), a => a.RemoteId == FakeIng.VlIban);
        Assert.Contains("did not confirm", rig.Logs.All, StringComparison.Ordinal);

        rig.Web.Reject = false;
        Assert.True((await rig.SyncAsync(true, false)).Succeeded);
        Assert.Equal(1, rig.Web.Count("x=submit"));

        // A day later discovery is tried again.
        rig.World.Clock.Offset += TimeSpan.FromDays(2);
        Assert.True((await rig.SyncAsync(true, false, "0")).Succeeded);
        Assert.Contains(await rig.Service.GetAccountsAsync(rig.LoginId), a => a.RemoteId == FakeIng.VlIban);
    }

    [Fact]
    public async Task A_refused_web_PIN_fails_the_statements_with_a_clear_message()
    {
        await using var rig = await CreateAsync(w => w.Devices = ["phone / Example Phone"]);
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
    public void Script_forwards_ignore_the_logout_handler()
    {
        const string html = "window.addEventListener('x', function() { window.location.replace('https://banking.ing.de/app/logout'); });"
            + "Wicket.Event.add(window, \"load\", function(event) { window.location.replace('https://banking.ing.de/app/postlogin/process?x=1'); });";
        Assert.Equal("https://banking.ing.de/app/postlogin/process?x=1", IngHtml.ScriptRedirect(html));
        Assert.Null(IngHtml.ScriptRedirect("window.location.replace('/app/logout')"));
    }

    [Fact]
    public void IBANs_are_found_in_postbox_subjects()
    {
        Assert.Equal("DE00000000000000000011", IngHtml.FindIban("Girokonto 1234567 / DE00 0000 0000 0000 0000 11"));
        Assert.Null(IngHtml.FindIban("Sparkonten"));
    }
}
