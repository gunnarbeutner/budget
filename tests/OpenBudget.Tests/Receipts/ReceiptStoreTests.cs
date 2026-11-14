using Microsoft.EntityFrameworkCore;
using OpenBudget.Connectors.Receipts;
using OpenBudget.Connectors.Sync;
using OpenBudget.Models;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests.Receipts;

/// <summary>Alice's categories, account and REWE direct debits for the receipt tests.</summary>
internal static class ReceiptWorld
{
    public const string Alice = "u-alice";
    public const string Bob = "u-bob";

    public static long Id(int row) => RowIds.Namespace(2, row);

    public static long Account => Id(1);
    public static long Groceries => Id(200);
    public static long Drugstore => Id(201);
    public static long Drinks => Id(202);

    public static readonly DateTime Delivered = new(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Users, id spaces, Alice's categories and account (no bookings).</summary>
    public static async Task SeedAsync(TestDb t, bool users = true)
    {
        if (users)
        {
            t.AddUser(Alice, "alice");
            t.AddUser(Bob, "bob");
            t.AddIdSpace(Alice, 2);
            t.AddIdSpace(Bob, 3);
        }
        t.AddGroup(Id(100), name: "Lebensmittel", ownerId: Alice);
        t.AddLeaf(Groceries, Id(100), "Supermarkt", ownerId: Alice);
        t.AddLeaf(Drugstore, Id(100), "Drogerie", ownerId: Alice);
        t.AddLeaf(Drinks, Id(100), "Getränke", ownerId: Alice);
        t.AddAccount(Account, "Joint account", Alice);
        await t.Db.SaveChangesAsync();
    }

    /// <summary>A REWE direct debit of Alice, booked <paramref name="days"/> after delivery, categorized by a rule.</summary>
    public static async Task AddDebitAsync(TestDb t, long id, long cents, int days = 3, string setBy = TransactionCategorySource.Rule)
    {
        await using var db = await t.System.CreateDbContextAsync();
        db.Transactions.Add(new Transaction
        {
            Id = id,
            OwnerUserId = Alice,
            AccountId = Account,
            Amount = cents,
            Timestamp = new DateTimeOffset(Delivered.AddDays(days)).ToUnixTimeSeconds(),
            Name = "REWE Markt GmbH",
            Purpose = "48466003261102300881",
            CategoryId = Groceries,
            CategorySetBy = setBy,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>The standard synthetic invoice with a bonus of 1,00 € (payment 14,28 €).</summary>
    public static ParsedReceipt Invoice(string number = "PN10000000000001") =>
        ReweInvoice.Parse(SyntheticInvoice.Pdf(SyntheticInvoice.StandardLines(), bonus: -1.00m, invoice: number));

    public static async Task SetNonFoodAsync(TestDb t, long? category)
    {
        await using var db = await t.System.CreateDbContextAsync();
        db.ReceiptSettings.Add(new ReceiptSettings { OwnerUserId = Alice, NonFoodCategoryId = category });
        await db.SaveChangesAsync();
    }
}

/// <summary>Storing receipts, matching them with bookings and splitting those by category.</summary>
public sealed class ReceiptStoreTests
{
    private const long Payment = -1428;
    private static readonly long Debit = ReceiptWorld.Id(500);

    private static async Task<TestDb> WorldAsync(long? nonFood = null)
    {
        var t = new TestDb();
        await ReceiptWorld.SeedAsync(t);
        await ReceiptWorld.SetNonFoodAsync(t, nonFood ?? ReceiptWorld.Drugstore);
        return t;
    }

    private static async Task AddAndMatchAsync(TestDb t, ReceiptStore store, ParsedReceipt? receipt = null)
    {
        await using var db = await t.System.CreateDbContextAsync();
        await ReceiptStore.AddAsync(db, ReceiptWorld.Alice, ReceiptShops.Rewe, receipt ?? ReceiptWorld.Invoice(), "mail-1", DateTime.UtcNow, default);
        await store.MatchAsync(db, ReceiptWorld.Alice, default);
    }

    private static async Task<List<Transaction>> PartsAsync(TestDb t, long parent)
    {
        await using var db = await t.System.CreateDbContextAsync();
        return await db.Transactions.AsNoTracking().Where(x => x.SplitParentId == parent && x.Splitted == SplitState.Child).OrderBy(x => x.Id).ToListAsync();
    }

    private static async Task<Transaction> RowAsync(TestDb t, long id)
    {
        await using var db = await t.System.CreateDbContextAsync();
        return await db.Transactions.AsNoTracking().SingleAsync(x => x.Id == id);
    }

    [Fact]
    public async Task A_matched_booking_is_split_by_category_once()
    {
        await using var t = await WorldAsync();
        await ReceiptWorld.AddDebitAsync(t, Debit, Payment);
        var store = new ReceiptStore();
        await AddAndMatchAsync(t, store);

        var parts = await PartsAsync(t, Debit);
        Assert.Equal(2, parts.Count);
        Assert.Equal(Payment, parts.Sum(p => p.Amount));
        var drugstore = parts.Single(p => p.CategoryId == ReceiptWorld.Drugstore);
        Assert.Equal("Musterdeo Roll-On Fresh 50ml; Spülmittel Zitrone 450ml", drugstore.OrderItems);
        Assert.Contains("Leergut Glas", parts.Single(p => p.CategoryId == ReceiptWorld.Groceries).OrderItems, StringComparison.Ordinal);
        var parent = await RowAsync(t, Debit);
        Assert.Equal(SplitState.Parent, parent.Splitted);
        Assert.Contains("ja! Kochschinken 200g ×2", parent.OrderItems, StringComparison.Ordinal);

        // Matching again changes nothing.
        await using (var db = await t.System.CreateDbContextAsync())
            await store.MatchAsync(db, ReceiptWorld.Alice, default);
        Assert.Equal(parts.Select(p => p.Id), (await PartsAsync(t, Debit)).Select(p => p.Id));

        await using (var db = await t.System.CreateDbContextAsync())
        {
            var invoice = await db.ReceiptInvoices.Include(i => i.Items).SingleAsync();
            Assert.Equal((ReceiptMatchState.Matched, Debit), (invoice.State, invoice.TransactionId!.Value));
            Assert.NotNull(invoice.SplitSignature);
            Assert.Equal(ReceiptCategorySource.Keyword, invoice.Items.Single(i => i.Name.StartsWith("Musterdeo", StringComparison.Ordinal)).CategorySource);
        }
    }

    [Fact]
    public async Task The_same_invoice_is_stored_once()
    {
        await using var t = await WorldAsync();
        await using var db = await t.System.CreateDbContextAsync();
        Assert.True(await ReceiptStore.AddAsync(db, ReceiptWorld.Alice, ReceiptShops.Rewe, ReceiptWorld.Invoice(), "m1", DateTime.UtcNow, default));
        Assert.False(await ReceiptStore.AddAsync(db, ReceiptWorld.Alice, ReceiptShops.Rewe, ReceiptWorld.Invoice(), "m2", DateTime.UtcNow, default));
        Assert.Equal(1, await db.ReceiptInvoices.CountAsync());
    }

    [Fact]
    public async Task A_booking_categorized_by_hand_gets_the_items_but_no_split()
    {
        await using var t = await WorldAsync();
        await ReceiptWorld.AddDebitAsync(t, Debit, Payment, setBy: TransactionCategorySource.Manual);
        await AddAndMatchAsync(t, new ReceiptStore());

        Assert.Empty(await PartsAsync(t, Debit));
        var row = await RowAsync(t, Debit);
        Assert.Equal(SplitState.Normal, row.Splitted ?? SplitState.Normal);
        Assert.Contains("Musterdeo", row.OrderItems, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_non_food_items_nothing_is_split()
    {
        await using var t = new TestDb();
        await ReceiptWorld.SeedAsync(t);
        await ReceiptWorld.AddDebitAsync(t, Debit, Payment);
        await AddAndMatchAsync(t, new ReceiptStore());

        Assert.Empty(await PartsAsync(t, Debit));
        Assert.NotNull((await RowAsync(t, Debit)).OrderItems);
    }

    [Fact]
    public async Task A_receipt_waits_for_its_booking()
    {
        await using var t = await WorldAsync();
        var store = new ReceiptStore();
        await AddAndMatchAsync(t, store);
        await using (var db = await t.System.CreateDbContextAsync())
            Assert.Equal(ReceiptMatchState.Unmatched, (await db.ReceiptInvoices.SingleAsync()).State);

        await ReceiptWorld.AddDebitAsync(t, Debit, Payment, days: 6);
        await using (var db = await t.System.CreateDbContextAsync())
            await store.MatchAsync(db, ReceiptWorld.Alice, default);
        Assert.Equal(2, (await PartsAsync(t, Debit)).Count);
    }

    [Fact]
    public async Task Another_owners_booking_is_never_matched()
    {
        await using var t = await WorldAsync();
        await using (var db = await t.System.CreateDbContextAsync())
        {
            db.Transactions.Add(new Transaction
            {
                Id = RowIds.Namespace(3, 500),
                OwnerUserId = ReceiptWorld.Bob,
                Amount = Payment,
                Name = "REWE Markt GmbH",
                Timestamp = new DateTimeOffset(ReceiptWorld.Delivered.AddDays(2)).ToUnixTimeSeconds(),
            });
            await db.SaveChangesAsync();
        }
        await AddAndMatchAsync(t, new ReceiptStore());
        await using (var db = await t.System.CreateDbContextAsync())
            Assert.Null((await db.ReceiptInvoices.SingleAsync()).TransactionId);
    }

    [Fact]
    public async Task A_split_changed_by_hand_is_left_alone()
    {
        await using var t = await WorldAsync();
        await ReceiptWorld.AddDebitAsync(t, Debit, Payment);
        var store = new ReceiptStore();
        await AddAndMatchAsync(t, store);
        await using (var db = await t.System.CreateDbContextAsync())
        {
            var part = await db.Transactions.FirstAsync(x => x.SplitParentId == Debit && x.CategoryId == ReceiptWorld.Drugstore);
            part.CategoryId = ReceiptWorld.Drinks;
            await db.SaveChangesAsync();
            await store.MatchAsync(db, ReceiptWorld.Alice, default);
        }
        var parts = await PartsAsync(t, Debit);
        Assert.Contains(parts, p => p.CategoryId == ReceiptWorld.Drinks);
        Assert.DoesNotContain(parts, p => p.CategoryId == ReceiptWorld.Drugstore);
    }

    [Fact]
    public async Task Remembered_items_and_ai_answers_place_later_receipts()
    {
        await using var t = await WorldAsync();
        await ReceiptWorld.AddDebitAsync(t, Debit, Payment);
        await using (var db = await t.System.CreateDbContextAsync())
        {
            db.ItemCategoryMemory.Add(new ItemCategoryMemory { OwnerUserId = ReceiptWorld.Alice, NormalizedName = "apfelschorle", CategoryId = ReceiptWorld.Drinks });
            await db.SaveChangesAsync();
        }
        var ai = new FakeClassifier(name => name.StartsWith("Beispiel Banane", StringComparison.Ordinal) ? ReceiptWorld.Drinks : null);
        var store = new ReceiptStore(ai);
        await AddAndMatchAsync(t, store);

        var drinks = (await PartsAsync(t, Debit)).Single(p => p.CategoryId == ReceiptWorld.Drinks);
        Assert.Equal("Beispiel Banane ca. 200g; Apfelschorle 0,5l ×2", drinks.OrderItems);
        // Only products the memory and keywords left open were asked about, and only once.
        Assert.Equal(["Beispiel Gouda gerieben 150g", "ja! Kochschinken 200g", "Beispiel Banane ca. 200g"], ai.Asked);
        await using (var db = await t.System.CreateDbContextAsync())
            await store.MatchAsync(db, ReceiptWorld.Alice, default);
        Assert.Equal(3, ai.Asked.Count);
    }

    [Fact]
    public async Task Amazon_matching_keeps_the_receipt_items()
    {
        await using var t = await WorldAsync();
        await ReceiptWorld.AddDebitAsync(t, Debit, Payment);
        await AddAndMatchAsync(t, new ReceiptStore());
        await using (var db = await t.System.CreateDbContextAsync())
        {
            db.BankLogins.Add(new BankLogin { Id = 7, OwnerUserId = ReceiptWorld.Alice, ConnectorType = "amazon", DisplayName = "Shop" });
            await db.SaveChangesAsync();
            db.AmazonCharges.Add(new AmazonCharge { OwnerUserId = ReceiptWorld.Alice, BankLoginId = 7, ChargeKey = "k", OrderId = "1", Date = ReceiptWorld.Delivered, AmountCents = -100 });
            await db.SaveChangesAsync();
            await new AmazonOrderStore().MatchAsync(db, ReceiptWorld.Alice, default);
        }
        Assert.NotNull((await RowAsync(t, Debit)).OrderItems);
        Assert.All(await PartsAsync(t, Debit), p => Assert.NotNull(p.OrderItems));
    }

    private sealed class FakeClassifier(Func<string, long?> answer) : IReceiptItemClassifier
    {
        public List<string> Asked { get; } = [];

        public Task<Dictionary<long, long>> ClassifyAsync(string ownerUserId, DateOnly date, IReadOnlyList<ReceiptLine> lines, CancellationToken ct)
        {
            Asked.AddRange(lines.Select(l => l.Name));
            return Task.FromResult(lines.Where(l => answer(l.Name) != null).ToDictionary(l => l.Id, l => answer(l.Name)!.Value));
        }
    }
}
