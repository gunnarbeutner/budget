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
    private const string Points = "example-points";
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
        new(order, Day.AddDays(days), amount, "EUR", instrument, instrument == Card ? "Visa •••• 1111" : instrument == Points ? "Example Points" : "Amazon gift card", "Approved", null);

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

    private const string OrderC = "301-0000000-0000003";
    private static readonly long Small = ReceiptWorld.Id(603);

    /// <summary>Order C: one item of 11.11, paid 10.60 by card and 0.51 with points on the same day.</summary>
    private static OrderSourceResult PointsSync() => new(
        [Charge(OrderC, 0, -10.60m), Charge(OrderC, 0, -0.51m, Points)],
        [new RemoteOrder(OrderC, Day, 11.11m, "EUR", [new RemoteOrderItem("Example item", 1, 11.11m, null, 0)])],
        [], Day.AddDays(-30));

    private static async Task AddSmallBookingAsync(TestDb t)
    {
        await using var db = await t.System.CreateDbContextAsync();
        db.Transactions.Add(Booking(Small, -1060, 1, "AMAZON EU"));
        await db.SaveChangesAsync();
    }

    private static async Task<List<ReceiptInvoice>> AmazonReceiptsAsync(TestDb t)
    {
        await using var db = await t.System.CreateDbContextAsync();
        return await db.ReceiptInvoices.AsNoTracking().Include(r => r.Items).Where(r => r.Shop == ReceiptShops.Amazon).ToListAsync();
    }

    private static List<(string Name, long Cents, ReceiptItemKind Kind)> Lines(ReceiptInvoice r) =>
        r.Items.OrderBy(i => i.Position).Select(i => (i.Name, i.TotalCents, i.Kind)).ToList();

    [Fact]
    public async Task Points_become_a_credit_line_of_the_card_receipt_and_go_when_the_wallet_drops_them()
    {
        var (t, login) = await WorldAsync();
        await using var _ = t;
        await AddSmallBookingAsync(t);
        await SyncAsync(t, login, PointsSync());
        await SyncAsync(t, login, PointsSync() with { Orders = [] });

        var receipt = Assert.Single(await AmazonReceiptsAsync(t));
        Assert.Equal((1060L, Small), (receipt.PaymentCents, receipt.TransactionId!.Value));
        Assert.Equal([("Example item", 1111L, ReceiptItemKind.Product), ("Example Points", -51L, ReceiptItemKind.Credit)], Lines(receipt));
        Assert.Equal((1060L, 0L), (receipt.TotalCents, receipt.BonusCents));

        var first = PointsSync();
        await SyncAsync(t, login, first with { Charges = [first.Charges[0]], Orders = [] });
        receipt = Assert.Single(await AmazonReceiptsAsync(t));
        Assert.Equal([("Example item", 1111L, ReceiptItemKind.Product)], Lines(receipt));
        Assert.Equal(Small, receipt.TransactionId);
    }

    [Fact]
    public async Task Points_go_to_the_card_charge_of_their_day_and_tell_which_items_it_paid()
    {
        var (t, login) = await WorldAsync();
        await using var _ = t;
        var first = FirstSync();
        await SyncAsync(t, login, first with { Charges = [Charge(OrderA, 0, -12.47m), Charge(OrderA, 1, -640.00m), Charge(OrderA, 1, -9.00m, Points)], Orders = [first.Orders[0]] });

        var receipts = await AmazonReceiptsAsync(t);
        Assert.Equal(2, receipts.Count);
        Assert.Equal([("Salted snack mix", 1247L, ReceiptItemKind.Product)], Lines(receipts.Single(r => r.PaymentCents == 1247)));
        var fridge = receipts.Single(r => r.PaymentCents == 64000);
        Assert.Equal([("Fridge freezer combination", 60000L, ReceiptItemKind.Product), ("Zahnpasta Mint", 4900L, ReceiptItemKind.Product), ("Example Points", -900L, ReceiptItemKind.Credit)],
            Lines(fridge));
        Assert.Equal(0, fridge.BonusCents);
    }

    [Fact]
    public async Task An_order_paid_with_points_alone_is_one_receipt_without_a_booking()
    {
        var (t, login) = await WorldAsync();
        await using var _ = t;
        var result = new OrderSourceResult([Charge(OrderB, 1, -3.00m, Points), Charge(OrderB, 1, -2.00m, Gift)],
            [new RemoteOrder(OrderB, Day, 5.00m, "EUR", [new RemoteOrderItem("Gift wrap", 1, 5.00m, null, 0)])], [], Day.AddDays(-30));
        await SyncAsync(t, login, result);
        await SyncAsync(t, login, result with { Orders = [] });

        var receipt = Assert.Single(await AmazonReceiptsAsync(t));
        Assert.Equal((ReceiptMatchState.Skipped, (long?)null, 0L), (receipt.State, receipt.TransactionId, receipt.PaymentCents));
        Assert.Equal("Example Points", receipt.PaymentMethod);
        Assert.Equal([("Gift wrap", 500L, ReceiptItemKind.Product), ("Example Points", -300L, ReceiptItemKind.Credit), ("Amazon gift card", -200L, ReceiptItemKind.Credit)],
            Lines(receipt));
        Assert.Equal((0L, 0L), (receipt.TotalCents, receipt.BonusCents));

        // A card charge arriving later takes the credits over.
        await SyncAsync(t, login, result with { Charges = [.. result.Charges, Charge(OrderB, 2, -1.00m)], Orders = [] });
        receipt = Assert.Single(await AmazonReceiptsAsync(t));
        Assert.Equal((100L, Card), (receipt.PaymentCents, receipt.PaymentMethodId));
        Assert.Equal(2, receipt.Items.Count(i => i.Kind == ReceiptItemKind.Credit));
    }

    [Fact]
    public async Task Stored_points_receipts_fold_into_their_card_receipt_keeping_its_booking_and_moves()
    {
        var (t, login) = await WorldAsync();
        await using var _ = t;
        await AddSmallBookingAsync(t);
        var now = DateTime.UtcNow;
        await using (var db = await t.System.CreateDbContextAsync())
        {
            var card = AmazonReceiptStore.NewReceipt(ReceiptWorld.Alice, login.Id, $"{OrderC}|2026-11-03|-1060|{Card}|1", OrderC, Day, -1060, Card, "Visa •••• 1111", now);
            card.State = ReceiptMatchState.Matched;
            card.TransactionId = Small;
            var points = AmazonReceiptStore.NewReceipt(ReceiptWorld.Alice, login.Id, $"{OrderC}|2026-11-03|-51|{Points}|1", OrderC, Day, -51, Points, "Example Points", now);
            points.State = ReceiptMatchState.Skipped;
            foreach (var r in new[] { card, points })
            {
                r.DetailsReadAt = now;
                r.OrderTotalCents = 1111;
            }
            card.Items.Add(new ReceiptItem
            {
                OwnerUserId = ReceiptWorld.Alice,
                Position = 0,
                Name = "Example item",
                UnitCents = 1111,
                TotalCents = 1111,
                Shipment = 0,
                CategoryId = ReceiptWorld.Drugstore,
                CategorySource = ReceiptCategorySource.Manual,
            });
            card.TotalCents = 1111;
            card.BonusCents = -51;
            db.ReceiptInvoices.AddRange(card, points);
            await db.SaveChangesAsync();
        }

        await using (var db = await t.System.CreateDbContextAsync())
        {
            Assert.Equal(1, await AmazonReceiptStore.FoldPointsReceiptsAsync(db, now));
            Assert.Equal(0, await AmazonReceiptStore.FoldPointsReceiptsAsync(db, now));
        }
        await SyncAsync(t, login, PointsSync() with { Orders = [] });

        var receipt = Assert.Single(await AmazonReceiptsAsync(t));
        Assert.Equal((ReceiptMatchState.Matched, Small), (receipt.State, receipt.TransactionId!.Value));
        Assert.Equal([("Example item", 1111L, ReceiptItemKind.Product), ("Example Points", -51L, ReceiptItemKind.Credit)], Lines(receipt));
        Assert.Equal((ReceiptWorld.Drugstore, ReceiptCategorySource.Manual), (receipt.Items.Single(i => i.Kind == ReceiptItemKind.Product).CategoryId!.Value,
            receipt.Items.Single(i => i.Kind == ReceiptItemKind.Product).CategorySource));
        Assert.Equal(0, receipt.BonusCents);
    }

    private const string OrderD = "301-0000000-0000004";

    /// <summary>Order D: a fridge and toothpaste for 279.00, paid in five instalments of 55.80; three are listed so far.</summary>
    private static OrderSourceResult InstalmentSync() => new(
        [Charge(OrderD, 0, -279.00m), Charge(OrderD, 0, -55.80m), Charge(OrderD, 30, -55.80m), Charge(OrderD, 59, -55.80m)],
        [
            new RemoteOrder(OrderD, Day, 279.00m, "EUR", [
                new RemoteOrderItem("Fridge freezer combination", 1, 200.00m, null, 0),
                new RemoteOrderItem("Zahnpasta Mint", 1, 79.00m, null, 0),
            ]),
        ],
        [], Day.AddDays(-30));

    [Fact]
    public async Task The_full_price_of_an_instalment_order_needs_no_booking_and_each_instalment_is_split_like_the_order()
    {
        var (t, login) = await WorldAsync();
        await using var _ = t;
        long[] ids = [ReceiptWorld.Id(610), ReceiptWorld.Id(611), ReceiptWorld.Id(612)];
        await using (var db = await t.System.CreateDbContextAsync())
        {
            db.Transactions.AddRange(Booking(ids[0], -5580, 0, "AMAZON EU"), Booking(ids[1], -5580, 30, "AMAZON EU"), Booking(ids[2], -5580, 59, "AMAZON EU"));
            await db.SaveChangesAsync();
        }
        await SyncAsync(t, login, InstalmentSync());

        await using var check = await t.System.CreateDbContextAsync();
        var receipts = await check.ReceiptInvoices.Include(r => r.Items).Where(r => r.OrderNumber == OrderD).ToListAsync();
        var full = receipts.Single(r => r.PaymentCents == 27900);
        Assert.Equal((ReceiptMatchState.Instalments, (long?)null), (full.State, full.TransactionId));
        Assert.Equal(2, full.Items.Count);
        var instalments = receipts.Where(r => r.PaymentCents == 5580).OrderBy(r => r.DeliveryDate).ToList();
        Assert.Equal(ids, instalments.Select(r => r.TransactionId!.Value));
        Assert.All(instalments, r => Assert.Equal(2, r.Items.Count));
        foreach (var id in ids)
        {
            var parts = await check.Transactions.AsNoTracking().Where(x => x.SplitParentId == id && x.Splitted == SplitState.Child).ToListAsync();
            Assert.Equal([(-4000L, ReceiptWorld.Groceries), (-1580L, ReceiptWorld.Drugstore)], parts.OrderBy(p => p.Amount).Select(p => (p.Amount, p.CategoryId!.Value)));
        }

    }

    private static ReceiptInvoice Entry(long cents, int days, long? orderTotal = 27900, string method = Card) => new()
    {
        OwnerUserId = ReceiptWorld.Alice,
        Shop = ReceiptShops.Amazon,
        SourceKey = $"{cents}|{days}|{method}",
        InvoiceNumber = "x",
        OrderNumber = OrderD,
        PaymentCents = cents,
        PaymentMethodId = method,
        OrderTotalCents = orderTotal,
        DeliveryDate = Day.AddDays(days).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
    };

    [Fact]
    public void An_instalment_plan_is_found_with_all_instalments_and_with_the_last_off_by_cents()
    {
        var all = AmazonReceiptStore.InstalmentPlans([Entry(27900, 0), .. Enumerable.Range(0, 5).Select(m => Entry(5580, m * 30))]);
        Assert.Equal((27900L, 5, 5), (all.Single().Full.PaymentCents, all.Single().Count, all.Single().Instalments.Count));

        var uneven = AmazonReceiptStore.InstalmentPlans([Entry(17849, 0, 17849), Entry(3570, 31), Entry(3570, 61), Entry(3570, 92), Entry(3570, 122), Entry(3569, 153)]);
        Assert.Equal((5, 5), (uneven.Single().Count, uneven.Single().Instalments.Count));
    }

    [Fact]
    public void Ordinary_shipment_charges_are_no_instalment_plan()
    {
        Assert.Empty(AmazonReceiptStore.InstalmentPlans([Entry(5000, 0, 8000), Entry(3000, 2, 8000)]));
        // one charge twice the other: the order's total is both, not the larger one
        Assert.Empty(AmazonReceiptStore.InstalmentPlans([Entry(6000, 0, 9000), Entry(3000, 2, 9000)]));
        // without the order's total one equal charge is not enough
        Assert.Empty(AmazonReceiptStore.InstalmentPlans([Entry(6000, 0, null), Entry(3000, 2, null)]));
        // instalments on another payment method or before the full price do not count
        Assert.Empty(AmazonReceiptStore.InstalmentPlans([Entry(27900, 30), Entry(5580, 0), Entry(5580, 60, method: Gift)]));
    }
}
