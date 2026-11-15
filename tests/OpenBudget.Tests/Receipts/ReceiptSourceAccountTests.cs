using Microsoft.EntityFrameworkCore;
using OpenBudget.Connectors.Receipts;
using OpenBudget.Models;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests.Receipts;

/// <summary>
/// The account a receipt source (Lidl Plus, the mailbox) is paid from: matching only bookings there, and its open
/// receipts in the projects of those accounts for everyone who can view them; Amazon's open charges placed by their
/// payment method's account. All data is made up.
/// </summary>
public sealed class ReceiptSourceAccountTests
{
    private static readonly DateTime Day = new(2026, 3, 12, 0, 0, 0, DateTimeKind.Utc);

    private static ReceiptService Receipts(TestDb t, string user)
    {
        var view = t.ForUser(user);
        return new ReceiptService(view.Factory, t.System, view.Access, null!, new ReceiptStore(), null!, t.Notifier, new ChangeOrigin(), TimeProvider.System);
    }

    private static ReceiptInvoice Lidl(string owner, long cents, string key) => new()
    {
        OwnerUserId = owner,
        Shop = ReceiptShops.Lidl,
        InvoiceNumber = key,
        Store = "Teststadt " + key,
        OrderDate = Day,
        TotalCents = cents,
        PaymentCents = cents,
        ReadAt = Day,
    };

    private static void Booking(TestDb t, long id, long account, string owner, long cents) => t.Db.Transactions.Add(new Transaction
    {
        Id = id,
        OwnerUserId = owner,
        AccountId = account,
        Amount = -cents,
        Name = "LIDL SAGT DANKE",
        Booked = true,
        Timestamp = new DateTimeOffset(Day).ToUnixTimeSeconds(),
    });

    [Fact]
    public async Task A_mapped_source_only_matches_bookings_on_its_accounts()
    {
        await using var t = await AccessWorld.BuildAsync();
        Booking(t, AccessWorld.Id(AccessWorld.SourceAlice, 60), AccessWorld.AccountPrivate, AccessWorld.Alice, 1234);
        var receipt = Lidl(AccessWorld.Alice, 1234, "L1");
        t.Db.ReceiptInvoices.Add(receipt);
        await t.Db.SaveChangesAsync();

        var alice = Receipts(t, AccessWorld.Alice);
        await alice.SetSourceAccountAsync(ReceiptSources.Lidl, AccessWorld.AccountWp, add: true);
        Assert.Null(await TransactionOfAsync(t, receipt.Id));

        await alice.SetSourceAccountAsync(ReceiptSources.Lidl, AccessWorld.AccountWp, add: false);
        Assert.Equal(AccessWorld.Id(AccessWorld.SourceAlice, 60), await TransactionOfAsync(t, receipt.Id)); // unmapped: as before

        await alice.SetSourceAccountAsync(ReceiptSources.Lidl, AccessWorld.AccountPrivate, add: true);
        Assert.Equal(AccessWorld.Id(AccessWorld.SourceAlice, 60), await TransactionOfAsync(t, receipt.Id));
        Assert.Equal([AccessWorld.AccountPrivate], await alice.GetSourceAccountsAsync(ReceiptSources.Lidl));
    }

    private static async Task<long?> TransactionOfAsync(TestDb t, long receiptId)
    {
        await using var db = await t.System.CreateDbContextAsync();
        return await db.ReceiptInvoices.Where(r => r.Id == receiptId).Select(r => r.TransactionId).SingleAsync();
    }

    [Fact]
    public async Task Another_users_open_receipt_of_a_mapped_source_shows_read_only_in_the_accounts_project()
    {
        await using var t = await AccessWorld.BuildAsync(ProjectRole.Editor);
        var receipt = Lidl(AccessWorld.Bob, 999, "B1");
        t.Db.ReceiptInvoices.Add(receipt);
        await t.Db.SaveChangesAsync();
        var alice = Receipts(t, AccessWorld.Alice);
        Assert.DoesNotContain(await alice.ListProjectAsync(AccessWorld.Shared), r => r.Id == receipt.Id);

        await Receipts(t, AccessWorld.Bob).SetSourceAccountAsync(ReceiptSources.Lidl, AccessWorld.AccountWp, add: true);
        var row = Assert.Single(await alice.ListProjectAsync(AccessWorld.Shared), r => r.Id == receipt.Id);
        Assert.False(row.IsOwn);
        Assert.Equal("bob", row.OwnerName);
        Assert.True(ReceiptService.NeedsReview(row));
        Assert.Equal(0, await alice.CountProjectReviewAsync(AccessWorld.Shared)); // not Alice's to assign
        Assert.Equal(1, await Receipts(t, AccessWorld.Bob).CountProjectReviewAsync(AccessWorld.Shared));
        Assert.NotNull(await alice.GetReceiptAsync(receipt.Id));
        Assert.DoesNotContain(await alice.ListProjectAsync(AccessWorld.Private), r => r.Id == receipt.Id);

        await Receipts(t, AccessWorld.Bob).SetSourceAccountAsync(ReceiptSources.Lidl, AccessWorld.AccountWp, add: false);
        Assert.DoesNotContain(await alice.ListProjectAsync(AccessWorld.Shared), r => r.Id == receipt.Id);
    }

    [Fact]
    public async Task An_open_amazon_charge_shows_in_the_project_of_its_payment_methods_account()
    {
        await using var t = await AccessWorld.BuildAsync(ProjectRole.Editor);
        await using (var db = await t.System.CreateDbContextAsync())
        {
            var login = new BankLogin { OwnerUserId = AccessWorld.Bob, ConnectorType = "amazon", DisplayName = "Shop" };
            db.BankLogins.Add(login);
            await db.SaveChangesAsync();
            db.BankLoginAccounts.Add(new BankLoginAccount { BankLoginId = login.Id, RemoteId = "visa-5555", Name = "Visa", EnrichAccountId = AccessWorld.AccountWp });
            db.BankLoginAccounts.Add(new BankLoginAccount { BankLoginId = login.Id, RemoteId = "visa-6666", Name = "Visa", EnrichAccountId = AccessWorld.AccountBob });
            foreach (var (key, card) in new[] { ("A1", "visa-5555"), ("A2", "visa-6666") })
                db.ReceiptInvoices.Add(new ReceiptInvoice
                {
                    OwnerUserId = AccessWorld.Bob,
                    Shop = ReceiptShops.Amazon,
                    BankLoginId = login.Id,
                    PaymentMethodId = card,
                    SourceKey = key,
                    InvoiceNumber = key,
                    OrderNumber = "302-0000000-00007" + key[1],
                    PaymentCents = 2500,
                    TotalCents = 2500,
                    DeliveryDate = Day,
                    ReadAt = Day,
                });
            await db.SaveChangesAsync();
        }
        var shared = await Receipts(t, AccessWorld.Alice).ListProjectAsync(AccessWorld.Shared);
        Assert.Equal(["Amazon 302-0000000-000071"], shared.Where(r => r.Source == ReceiptSources.Amazon).Select(r => r.Title));
        var bobs = await Receipts(t, AccessWorld.Bob).ListProjectAsync(AccessWorld.BobProject);
        Assert.Equal(["Amazon 302-0000000-000072"], bobs.Where(r => r.Source == ReceiptSources.Amazon).Select(r => r.Title));
    }

    [Fact]
    public async Task A_user_without_access_to_the_project_sees_nothing()
    {
        await using var t = await AccessWorld.BuildAsync(ProjectRole.Editor);
        var receipt = Lidl(AccessWorld.Alice, 999, "A1");
        t.Db.ReceiptInvoices.Add(receipt);
        await t.Db.SaveChangesAsync();
        await Receipts(t, AccessWorld.Alice).SetSourceAccountAsync(ReceiptSources.Lidl, AccessWorld.AccountPrivate, add: true);
        Assert.Contains(await Receipts(t, AccessWorld.Alice).ListProjectAsync(AccessWorld.Private), r => r.Id == receipt.Id);
        await Assert.ThrowsAsync<AccessDeniedException>(() => Receipts(t, AccessWorld.Bob).ListProjectAsync(AccessWorld.Private));
        Assert.Null(await Receipts(t, AccessWorld.Bob).GetReceiptAsync(receipt.Id));
    }
}
