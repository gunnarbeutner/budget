using Microsoft.EntityFrameworkCore;
using OpenBudget.Connectors;
using OpenBudget.Connectors.Amazon;
using OpenBudget.Connectors.Receipts;
using OpenBudget.Models;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests.Receipts;

/// <summary>Amazon orders as receipts: one per charge with its shipment's items, matched by account and split like any receipt.</summary>
public sealed class AmazonReceiptStoreTests
{
    private const string Card = "visa-1111";
    private const string Gift = "gift-card";
    private const string OrderA = "301-0000000-0000001";
    private const string OrderB = "301-0000000-0000002";
    private static readonly DateOnly Day = new(2026, 11, 3);
    private static readonly long Snacks = ReceiptWorld.Id(600);
    private static readonly long Fridge = ReceiptWorld.Id(601);
    private static readonly long Refund = ReceiptWorld.Id(602);

    private static async Task<(TestDb T, BankLogin Login)> WorldAsync()
    {
        var t = new TestDb();
        await ReceiptWorld.SeedAsync(t);
        await ReceiptWorld.AllowAsync(t, ReceiptWorld.Drugstore);
        await using var db = await t.System.CreateDbContextAsync();
        var login = new BankLogin { OwnerUserId = ReceiptWorld.Alice, ConnectorType = "amazon", DisplayName = "Shop" };
        db.BankLogins.Add(login);
        await db.SaveChangesAsync();
        db.BankLoginAccounts.Add(new BankLoginAccount { BankLoginId = login.Id, RemoteId = Card, Name = "Visa •••• 1111", EnrichAccountId = ReceiptWorld.Account });
        db.Transactions.AddRange(Booking(Snacks, -1247, 1, "AMAZON EU"), Booking(Fridge, -64900, 2, "Card payment"), Booking(Refund, 1247, 12, "AMAZON EU"));
        await db.SaveChangesAsync();
        return (t, login);
    }

    private static Transaction Booking(long id, long cents, int days, string name) => new()
    {
        Id = id,
        OwnerUserId = ReceiptWorld.Alice,
        AccountId = ReceiptWorld.Account,
        Amount = cents,
        Timestamp = new DateTimeOffset(Day.AddDays(days).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)).ToUnixTimeSeconds(),
        Name = name,
        CategoryId = ReceiptWorld.Groceries,
        CategorySetBy = TransactionCategorySource.Rule,
    };

    private static RemoteCharge Charge(string order, int days, decimal amount, string instrument = Card) =>
        new(order, Day.AddDays(days), amount, "EUR", instrument, instrument == Card ? "Visa •••• 1111" : "Amazon gift card", "Approved", null);

    /// <summary>Order A: snacks in one shipment, a fridge and toothpaste in another, charged separately; order B paid with a gift card.</summary>
    private static OrderSourceResult FirstSync() => new(
        [Charge(OrderA, 0, -12.47m), Charge(OrderA, 1, -649.00m), Charge(OrderB, 1, -5.00m, Gift)],
        [
            new RemoteOrder(OrderA, Day, 661.47m, "EUR", [
                new RemoteOrderItem("Salted snack mix", 1, 12.47m, null, 0),
                new RemoteOrderItem("Fridge freezer combination", 1, 600.00m, null, 1),
                new RemoteOrderItem("Zahnpasta Mint", 1, 49.00m, null, 1),
            ]),
            new RemoteOrder(OrderB, Day, 5.00m, "EUR", [new RemoteOrderItem("Gift wrap", 1, 5.00m, null, 0)]),
        ],
        [], Day.AddDays(-30));

    private static async Task SyncAsync(TestDb t, BankLogin login, OrderSourceResult result)
    {
        await using var db = await t.System.CreateDbContextAsync();
        await AmazonReceiptStore.ApplyAsync(db, login, result, DateTime.UtcNow, new ReceiptStore(), default);
    }

    [Fact]
    public async Task Orders_stored_without_their_items_are_asked_for_until_a_sync_reads_them()
    {
        var (t, login) = await WorldAsync();
        await using var _ = t;
        var first = FirstSync();
        await SyncAsync(t, login, first with { Orders = [] });

        await using (var db = await t.System.CreateDbContextAsync())
            Assert.Contains(OrderA, await AmazonReceiptStore.UnreadOrdersAsync(db, login.Id, default));

        await SyncAsync(t, login, first);
        await using (var db = await t.System.CreateDbContextAsync())
            Assert.DoesNotContain(OrderA, await AmazonReceiptStore.UnreadOrdersAsync(db, login.Id, default));
    }

    [Fact]
    public async Task Each_charge_becomes_a_receipt_with_its_shipments_items_matched_by_account_and_split()
    {
        var (t, login) = await WorldAsync();
        await using var _ = t;
        await SyncAsync(t, login, FirstSync());

        await using var db = await t.System.CreateDbContextAsync();
        var receipts = await db.ReceiptInvoices.Include(r => r.Items).Where(r => r.Shop == ReceiptShops.Amazon).ToListAsync();
        Assert.Equal(3, receipts.Count);
        var snacks = receipts.Single(r => r.PaymentCents == 1247);
        Assert.Equal((ReceiptMatchState.Matched, Snacks), (snacks.State, snacks.TransactionId!.Value));
        Assert.Equal(["Salted snack mix"], snacks.Items.Select(i => i.Name));
        var fridge = receipts.Single(r => r.PaymentCents == 64900);
        Assert.Equal(Fridge, fridge.TransactionId); // on the mapped account, though the booking does not name the shop
        Assert.Equal(ReceiptMatchState.Skipped, receipts.Single(r => r.PaymentMethodId == Gift).State);

        var parts = await db.Transactions.AsNoTracking().Where(x => x.SplitParentId == Fridge && x.Splitted == SplitState.Child).ToListAsync();
        Assert.Equal([(-60000L, ReceiptWorld.Groceries), (-4900L, ReceiptWorld.Drugstore)],
            parts.OrderBy(p => p.Amount).Select(p => (p.Amount, p.CategoryId!.Value)));
        Assert.Equal("Fridge freezer combination; Zahnpasta Mint", (await db.Transactions.AsNoTracking().SingleAsync(x => x.Id == Fridge)).OrderItems);
        Assert.Equal([OrderA, OrderB], (await AmazonReceiptStore.KnownOrdersAsync(db, login.Id, default)).Order(StringComparer.Ordinal));
        Assert.Empty(await AmazonReceiptStore.UnreadOrdersAsync(db, login.Id, default));
    }

    [Fact]
    public async Task A_later_refund_takes_the_known_items_and_matches_money_coming_back()
    {
        var (t, login) = await WorldAsync();
        await using var _ = t;
        await SyncAsync(t, login, FirstSync());
        var first = FirstSync();
        await SyncAsync(t, login, first with { Charges = [.. first.Charges, Charge(OrderA, 11, 12.47m)], Orders = [] });

        await using var db = await t.System.CreateDbContextAsync();
        var refund = await db.ReceiptInvoices.Include(r => r.Items).SingleAsync(r => r.PaymentCents == -1247);
        Assert.Equal(Refund, refund.TransactionId);
        Assert.Equal(3, refund.Items.Count);
        Assert.Equal(4, await db.ReceiptInvoices.CountAsync(r => r.Shop == ReceiptShops.Amazon));
    }

    [Fact]
    public async Task Unmapping_the_payment_method_lets_go_of_the_bookings()
    {
        var (t, login) = await WorldAsync();
        await using var _ = t;
        await SyncAsync(t, login, FirstSync());
        await using var db = await t.System.CreateDbContextAsync();
        (await db.BankLoginAccounts.SingleAsync()).EnrichAccountId = null;
        await db.SaveChangesAsync();
        await new ReceiptStore().MatchAsync(db, ReceiptWorld.Alice, default);

        Assert.All(await db.ReceiptInvoices.Where(r => r.Shop == ReceiptShops.Amazon).ToListAsync(), r => Assert.Equal(ReceiptMatchState.Skipped, r.State));
        Assert.Null((await db.Transactions.AsNoTracking().SingleAsync(x => x.Id == Snacks)).OrderItems);
    }
}
