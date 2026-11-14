#pragma warning disable CA2000 // test harness: lifetimes are owned by the world
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenBudget.Connectors.OneDrive;
using OpenBudget.Connectors.Receipts;
using OpenBudget.Connectors.Sync;
using OpenBudget.Models;
using OpenBudget.Services;
using OpenBudget.Tests.Connectors;
using Xunit;

namespace OpenBudget.Tests.Receipts;

/// <summary>Granting mail access, reading invoices from the (fake) Microsoft 365 mailbox and moving receipt items.</summary>
public sealed class MailReceiptTests
{
    private const string Alice = ReceiptWorld.Alice;
    private static readonly long Debit = ReceiptWorld.Id(500);

    private sealed class World : IAsyncDisposable
    {
        private World(OneDriveWorld w)
        {
            W = w;
            Store = new ReceiptStore();
            Sync = new MailReceiptSync(w.T.System, w.Service, Store, new BackgroundSyncOptions(), new MailReceiptOptions(), w.Clock, w.T.Notifier,
                w.Loggers.CreateLogger<MailReceiptSync>());
        }

        public OneDriveWorld W { get; }
        public ReceiptStore Store { get; }
        public MailReceiptSync Sync { get; }

        public static async Task<World> CreateAsync()
        {
            var w = await OneDriveWorld.CreateAsync();
            await ReceiptWorld.SeedAsync(w.T, users: false);
            await ReceiptWorld.AllowAsync(w.T, ReceiptWorld.Drugstore);
            return new World(w);
        }

        public async Task<ConnectOutcome> GrantMailAsync()
        {
            var url = W.Service.BeginConnect(Alice, OneDriveWorld.Callback, mail: true);
            Assert.Contains("Mail.Read", Uri.UnescapeDataString(url.Query), StringComparison.Ordinal);
            var (code, state, _) = W.Fake.Authorize(url);
            return await W.Service.CompleteConnectAsync(Alice, state, code, null, default);
        }

        public ReceiptService ServiceFor(string user)
        {
            var view = W.T.ForUser(user);
            return new ReceiptService(view.Factory, W.T.System, view.Access, W.Service, Store, Sync, W.T.Notifier, new ChangeOrigin(), W.Clock);
        }

        public async ValueTask DisposeAsync()
        {
            Sync.Dispose();
            await W.DisposeAsync();
        }
    }

    private static FakeMicrosoftCloud.FakeMail InvoiceMail(string id, byte[] pdf, string name = "Rechnung_PN10000000000001.pdf") =>
        new(id, "Deine Rechnung zu B-AAA-BBB-CC1", new DateTime(2026, 11, 9, 18, 0, 0, DateTimeKind.Utc), [("att-" + id, name, pdf), ("agb-" + id, "AGB.pdf", [1, 2, 3])]);

    [Fact]
    public async Task Reads_invoices_from_the_mailbox_and_splits_the_booking()
    {
        await using var x = await World.CreateAsync();
        Assert.Equal(ConnectOutcome.Connected, await x.GrantMailAsync());
        Assert.True((await x.W.ConnectionRowAsync(Alice))!.MailAccess);
        await ReceiptWorld.AddDebitAsync(x.W.T, Debit, -1428);

        var pdf = SyntheticInvoice.Pdf(SyntheticInvoice.StandardLines(), bonus: -1.00m);
        x.W.Fake.AddMail(InvoiceMail("m1", pdf));
        x.W.Fake.AddMail(new("m2", "Dein Newsletter", new DateTime(2026, 11, 9, 8, 0, 0, DateTimeKind.Utc), [("a", "Rechnung_PN1.pdf", pdf)]));
        x.W.Fake.AddMail(new("m3", "Deine Rechnung zu B-AAA-BBB-CC2", new DateTime(2026, 11, 9, 8, 0, 0, DateTimeKind.Utc), [("b", "Info.pdf", pdf)]));

        var result = await x.Sync.CheckNowAsync(Alice);
        Assert.Equal(new MailCheckResult(2, 1, 0, null), result);
        Assert.Equal(1, x.W.Fake.AttachmentDownloads); // only the invoice PDF

        await using (var db = await x.W.T.System.CreateDbContextAsync())
        {
            var invoice = await db.ReceiptInvoices.SingleAsync();
            Assert.Equal(("m1", Debit), (invoice.MessageId, invoice.TransactionId!.Value));
            Assert.Equal(2, await db.Transactions.CountAsync(t => t.SplitParentId == Debit));
            Assert.NotNull((await db.ReceiptSettings.SingleAsync(s => s.OwnerUserId == Alice)).MailCheckedAt);
        }

        // A second read finds the same mail but neither downloads nor stores it again.
        Assert.Equal(new MailCheckResult(2, 0, 0, null), await x.Sync.CheckNowAsync(Alice));
        Assert.Equal(1, x.W.Fake.AttachmentDownloads);

        // Nothing of the mail, the invoice or the tokens reaches the log.
        Assert.DoesNotContain(x.W.Logged, l => l.Contains("Gouda", StringComparison.Ordinal) || l.Contains("Rechnung zu", StringComparison.Ordinal));
        foreach (var secret in x.W.Fake.IssuedSecrets) Assert.DoesNotContain(x.W.Logged, l => l.Contains(secret, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Refreshes_keep_mail_access_and_the_background_reads_due_mailboxes()
    {
        await using var x = await World.CreateAsync();
        await x.GrantMailAsync();
        x.W.Fake.AddMail(InvoiceMail("m1", SyntheticInvoice.Pdf(SyntheticInvoice.StandardLines())));
        x.W.Fake.RevokeAccessTokens();

        Assert.Equal(1, await x.Sync.TickAsync(default));
        Assert.Equal(0, await x.Sync.TickAsync(default)); // read within the interval
        Assert.True(x.W.Fake.TokenRefreshes >= 1);
        await using var db = await x.W.T.System.CreateDbContextAsync();
        Assert.Equal(1, await db.ReceiptInvoices.CountAsync());
        Assert.Equal(ReceiptMatchState.Unmatched, (await db.ReceiptInvoices.SingleAsync()).State);
    }

    [Fact]
    public async Task Without_mail_access_the_mailbox_is_not_read()
    {
        await using var x = await World.CreateAsync();
        await x.W.ConnectAsync(Alice);
        Assert.False((await x.W.ConnectionRowAsync(Alice))!.MailAccess);
        Assert.Equal("Mail access is not granted.", (await x.Sync.CheckNowAsync(Alice)).Error);
        Assert.Equal(0, await x.Sync.TickAsync(default));

        // The app registration lacks Mail.Read: the sign-in succeeds, mail access stays off.
        x.W.Fake.WithholdMail = true;
        Assert.Equal(ConnectOutcome.Connected, await x.GrantMailAsync());
        Assert.False((await x.W.ConnectionRowAsync(Alice))!.MailAccess);
        Assert.False((await x.ServiceFor(Alice).GetSettingsAsync()).Connection!.MailAccess);
    }

    [Fact]
    public async Task Stopping_mail_access_stops_reading()
    {
        await using var x = await World.CreateAsync();
        await x.GrantMailAsync();
        await x.ServiceFor(Alice).StopMailAsync();
        Assert.False((await x.W.ConnectionRowAsync(Alice))!.MailAccess);
        Assert.Equal(0, await x.Sync.TickAsync(default));
    }

    [Fact]
    public async Task Moving_an_item_resplits_and_is_remembered_for_later_receipts()
    {
        await using var x = await World.CreateAsync();
        await ReceiptWorld.AddDebitAsync(x.W.T, Debit, -1428);
        await using (var db = await x.W.T.System.CreateDbContextAsync())
        {
            await ReceiptStore.AddAsync(db, Alice, ReceiptShops.Rewe, ReceiptWorld.Invoice(), null, DateTime.UtcNow, default);
            await x.Store.MatchAsync(db, Alice, default);
        }
        var alice = x.ServiceFor(Alice);
        var view = (await alice.GetForBookingAsync(Debit))!;
        Assert.Equal([ReceiptWorld.Groceries, ReceiptWorld.Drugstore], view.Groups.Select(g => g.CategoryId!.Value));
        var schorle = view.Groups.SelectMany(g => g.Lines).Single(l => l.Name == "Apfelschorle 0,5l");

        // Bob sees nothing of Alice's receipt and cannot move its lines.
        Assert.Null(await x.ServiceFor(ReceiptWorld.Bob).GetForBookingAsync(Debit));
        await Assert.ThrowsAsync<AccessDeniedException>(() => x.ServiceFor(ReceiptWorld.Bob).MoveItemAsync(schorle.Id, ReceiptWorld.Drinks));

        await alice.MoveItemAsync(schorle.Id, ReceiptWorld.Drinks);
        view = (await alice.GetForBookingAsync(Debit))!;
        Assert.Equal(3, view.Groups.Count);
        Assert.Equal(-1428, view.Groups.Sum(g => g.AmountCents));
        await using (var db = await x.W.T.System.CreateDbContextAsync())
        {
            var parts = await db.Transactions.Where(t => t.SplitParentId == Debit).ToListAsync();
            Assert.Equal(3, parts.Count);
            Assert.Equal("Apfelschorle 0,5l ×2", parts.Single(p => p.CategoryId == ReceiptWorld.Drinks).OrderItems);
            Assert.Equal(ReceiptWorld.Drinks, (await db.ItemCategoryMemory.SingleAsync()).CategoryId);
        }

        // A later receipt with the same product in another size goes to the remembered category.
        var second = ReweInvoice.Parse(SyntheticInvoice.Pdf(
            [SyntheticInvoice.Item("Beispiel Gouda gerieben 150g", "1", "B", 1.89m, 1.89m), SyntheticInvoice.Item("Apfelschorle 1,0l", "1", "A", 1.50m, 1.50m)],
            invoice: "PN10000000000002", order: "B-AAA-BBB-CC2"));
        await ReceiptWorld.AddDebitAsync(x.W.T, ReceiptWorld.Id(501), -339, days: 4);
        await using (var db = await x.W.T.System.CreateDbContextAsync())
        {
            await ReceiptStore.AddAsync(db, Alice, ReceiptShops.Rewe, second, null, DateTime.UtcNow, default);
            await x.Store.MatchAsync(db, Alice, default);
            var parts = await db.Transactions.Where(t => t.SplitParentId == ReceiptWorld.Id(501)).ToListAsync();
            Assert.Equal(-150, parts.Single(p => p.CategoryId == ReceiptWorld.Drinks).Amount);
        }

        // The receipts page lists both.
        Assert.Equal(2, (await alice.ListAsync()).Count(r => r.State == ReceiptMatchState.Matched && r.Split));
    }

    [Fact]
    public async Task A_receipt_without_a_fitting_booking_is_assigned_by_hand_or_dismissed()
    {
        await using var x = await World.CreateAsync();
        // a booking of another amount: not matched automatically
        await ReceiptWorld.AddDebitAsync(x.W.T, Debit, -1500);
        await using (var db = await x.W.T.System.CreateDbContextAsync())
        {
            await ReceiptStore.AddAsync(db, Alice, ReceiptShops.Rewe, ReceiptWorld.Invoice(), null, DateTime.UtcNow, default);
            await x.Store.MatchAsync(db, Alice, default);
        }
        var alice = x.ServiceFor(Alice);
        var row = Assert.Single(await alice.ListAllAsync(ReceiptSources.Mail));
        Assert.Contains(row.State, new[] { ReceiptReviewState.Unmatched, ReceiptReviewState.Waiting });
        Assert.Equal(Debit, (await alice.CandidatesAsync(row.Id)).First().TransactionId);
        await Assert.ThrowsAsync<AccessDeniedException>(() => x.ServiceFor(ReceiptWorld.Bob).AssignAsync(row.Id, Debit));

        await alice.AssignAsync(row.Id, Debit);
        Assert.Equal(ReceiptReviewState.Split, Assert.Single(await alice.ListAllAsync()).State);
        Assert.Equal(-1500, (await alice.GetForBookingAsync(Debit))!.Groups.Sum(g => g.AmountCents));
        // the manual match stays on the next sync
        await using (var db = await x.W.T.System.CreateDbContextAsync())
            await x.Store.MatchAsync(db, Alice, default);
        Assert.Equal(ReceiptReviewState.Split, Assert.Single(await alice.ListAllAsync()).State);

        await alice.AssignAsync(row.Id, null);
        Assert.Equal(ReceiptReviewState.Dismissed, Assert.Single(await alice.ListAllAsync(state: ReceiptReviewState.Dismissed)).State);
        await alice.ResetAssignmentAsync(row.Id);
        Assert.Empty(await alice.ListAllAsync(state: ReceiptReviewState.Dismissed));
    }

    [Fact]
    public async Task Reads_ebon_mails_too()
    {
        await using var x = await World.CreateAsync();
        await x.GrantMailAsync();
        x.W.Fake.AddMail(new("e1", "Dein REWE eBon vom 03.11.2026", new DateTime(2026, 11, 9, 18, 0, 0, DateTimeKind.Utc),
            [("e1-pdf", "REWE-eBon.pdf", ReweEbonTests.Pdf()), ("e1-ad", "Angebote.pdf", [1, 2])]));

        Assert.Equal(new MailCheckResult(1, 1, 0, null), await x.Sync.CheckNowAsync(Alice));
        Assert.Equal(1, x.W.Fake.AttachmentDownloads);
        await using var db = await x.W.T.System.CreateDbContextAsync();
        var receipt = await db.ReceiptInvoices.SingleAsync();
        Assert.Equal((ReceiptShops.ReweEbon, 1800L, "REWE Beispielweg 9, 12345 Musterstadt"), (receipt.Shop, receipt.PaymentCents, receipt.Store));
    }
}
