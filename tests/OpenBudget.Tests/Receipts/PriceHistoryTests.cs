using OpenBudget.Connectors.Receipts;
using OpenBudget.Models;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests.Receipts;

/// <summary>
/// Price history from receipt items (PriceAnalysis, PriceHistoryService): product keys, sizes, unit prices, the price
/// index, merges, regular purchases, and which receipts each user's history reads.
/// </summary>
public sealed class PriceHistoryTests
{
    private static readonly DateOnly Today = new(2026, 6, 30);

    private static PriceLine Line(string name, DateOnly date, long total, string quantity = "1", long? unit = null, string shop = ReceiptShops.Lidl, long receipt = 1) =>
        new(receipt, shop, date, name, quantity, unit, total, true);

    [Theory]
    [InlineData("Bio Vollmilch 3,8%", "bio vollmilch 3 8")]
    [InlineData("BIO VOLLMILCH 3.8% 1L", "bio vollmilch 3 8")]
    [InlineData("  Äpfel   Rot  ", "aepfel rot")]
    [InlineData("Testkekse Hafer 500g", "testkekse hafer")]
    [InlineData("Sprudel Classic 6x1,5l", "sprudel classic")]
    [InlineData("Beispiel-Brötchen 10 St.", "beispiel broetchen")]
    [InlineData("Kaffee Bohne 1kg", "kaffee bohne")]
    public void Product_keys_fold_names_and_drop_trailing_pack_sizes(string name, string key) =>
        Assert.Equal(key, PriceAnalysis.ProductKey(ReceiptShops.Lidl, name));

    [Fact]
    public void Amazon_keys_cut_long_titles_at_a_word()
    {
        var a = PriceAnalysis.ProductKey(ReceiptShops.Amazon, "Beispielmarke Kabel USB-C auf USB-C 2m geflochten, schnellladen, für viele Geräte, schwarz");
        var b = PriceAnalysis.ProductKey(ReceiptShops.Amazon, "Beispielmarke Kabel USB-C auf USB-C 2m geflochten, schnellladen, für viele Geräte, weiß");
        Assert.Equal(a, b);
        Assert.True(a.Length <= PriceAnalysis.AmazonKeyLength);
    }

    [Theory]
    [InlineData("Testkekse 500g", 500, PriceUnit.Kg)]
    [InlineData("Saft Orange 1,5 l", 1500, PriceUnit.Litre)]
    [InlineData("Limo 6x0,33l", 1980, PriceUnit.Litre)]
    [InlineData("Käse 2 x 150 g", 300, PriceUnit.Kg)]
    [InlineData("Reis 1kg", 1000, PriceUnit.Kg)]
    [InlineData("Sahne 200ml", 200, PriceUnit.Litre)]
    public void Sizes_parse_from_names(string name, int amount, PriceUnit unit) =>
        Assert.Equal(new PackSize(amount, unit), PriceAnalysis.ParseSize(name));

    [Theory]
    [InlineData("Bananen")]
    [InlineData("Vollmilch 3,8%")]
    [InlineData("Mix 500g 1l")]
    [InlineData("Glas 2g")]
    public void Unreliable_or_missing_sizes_give_none(string name) => Assert.Null(PriceAnalysis.ParseSize(name));

    [Fact]
    public void A_weighed_item_is_priced_per_kg()
    {
        var withUnit = PriceAnalysis.ToPurchase(Line("Tomaten", Today, 299, "500g", 598))!;
        Assert.True(withUnit.Weighed);
        Assert.Equal(598, withUnit.Price);
        Assert.Equal(0.5m, withUnit.Quantity);
        var fromTotal = PriceAnalysis.ToPurchase(Line("Tomaten", Today, 300, "250g"))!;
        Assert.Equal(1200, fromTotal.BaseCents);
        var fraction = PriceAnalysis.ToPurchase(Line("Trauben", Today, 520, "0.521", 998))!;
        Assert.True(fraction.Weighed);
        Assert.Equal(998, fraction.Price);
    }

    [Fact]
    public void A_pack_item_is_priced_per_piece_and_per_size()
    {
        var p = PriceAnalysis.ToPurchase(Line("Testkekse 500g", Today, 398, "2"))!;
        Assert.False(p.Weighed);
        Assert.Equal(199, p.PieceCents);
        Assert.Equal(398, p.BaseCents);
        Assert.Equal(PriceUnit.Kg, p.BaseUnit);
        Assert.Null(PriceAnalysis.ToPurchase(Line("Pfandrückgabe", Today, -25)));
    }

    private static IEnumerable<PriceLine> Repeated(string name, DateOnly from, int count, int everyDays, long cents, long receiptBase = 0) =>
        Enumerable.Range(0, count).Select(i => Line(name, from.AddDays(i * everyDays), cents, receipt: receiptBase + i));

    [Fact]
    public void The_index_shows_a_price_increase_weighted_by_spend()
    {
        var lines = Repeated("Testbrot", Today.AddMonths(-20), 4, 30, 200, 100)
            .Concat(Repeated("Testbrot", Today.AddMonths(-6), 4, 30, 220, 200))   // +10 %
            .Concat(Repeated("Testquark", Today.AddMonths(-20), 4, 30, 100, 300))
            .Concat(Repeated("Testquark", Today.AddMonths(-6), 4, 30, 100, 400))  // ±0
            .Concat(Repeated("Einmalig", Today.AddMonths(-6), 1, 30, 999, 500));
        var products = PriceAnalysis.Products(lines);
        var index = PriceAnalysis.Index(products, Today);
        var overall = index.Rows.Single(r => r.Label == "12M" && r.Shop == null);
        Assert.Equal(2, overall.Products);
        // weights 800 and 400: (0.10 * 800 + 0 * 400) / 1200
        Assert.Equal(0.0667m, Math.Round(overall.Change, 4));
        Assert.Contains(index.Rows, r => r.Label == "12M" && r.Shop == ReceiptShops.Lidl);
        Assert.Equal("Testbrot", index.Increases.Single().Name);
        Assert.Empty(index.Decreases);
        var brot = products.Single(p => p.Name == "Testbrot");
        Assert.Equal(0.10m, brot.Change12M(Today));
    }

    [Fact]
    public void A_merge_joins_two_products_of_a_shop()
    {
        var lines = new[]
        {
            Line("Bio Testmilch 3,8%", Today.AddDays(-60), 119, receipt: 1),
            Line("TESTMILCH BIO 3.8% 1L", Today.AddDays(-10), 129, receipt: 2),
        };
        Assert.Equal(2, PriceAnalysis.Products(lines).Count);
        var from = ReceiptShops.Lidl + "|" + PriceAnalysis.ProductKey(ReceiptShops.Lidl, lines[0].Name);
        var to = ReceiptShops.Lidl + "|" + PriceAnalysis.ProductKey(ReceiptShops.Lidl, lines[1].Name);
        var merged = PriceAnalysis.Products(lines, new Dictionary<string, string>(StringComparer.Ordinal) { [from] = to });
        var p = Assert.Single(merged);
        Assert.Equal(to, p.Id);
        Assert.Equal(2, p.TimesBought);
        Assert.Equal(119, p.First.Price);
        Assert.Equal([from, to], p.MergedKeys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Regulars_are_products_bought_at_least_monthly()
    {
        var lines = Repeated("Testjoghurt", Today.AddDays(-84), 13, 7, 89, 100)
            .Concat(Repeated("Testkaffee", Today.AddDays(-300), 4, 90, 799, 200))
            .Concat(Repeated("Testsaft", Today.AddDays(-400), 6, 7, 199, 300));   // stopped long ago
        var regulars = PriceAnalysis.Regulars(PriceAnalysis.Products(lines), Today);
        var r = Assert.Single(regulars);
        Assert.Equal("Testjoghurt", r.Name);
        Assert.Equal(7, r.IntervalDays);
        Assert.Equal(13, r.Times);
        Assert.InRange(r.MonthlyCents, 400, 430);
    }

    // ---- access ----

    private static readonly long WpBooking = AccessWorld.Id(AccessWorld.SourceAlice, 60);
    private static readonly long PrivateBooking = AccessWorld.Id(AccessWorld.SourceAlice, 61);

    private static async Task<TestDb> WorldAsync(ProjectRole? bobOnWp)
    {
        var t = await AccessWorld.BuildAsync(bobOnWp);
        await using var db = await t.System.CreateDbContextAsync();
        var day = new DateTime(2026, 5, 2, 0, 0, 0, DateTimeKind.Utc);
        db.Transactions.Add(new Transaction { Id = WpBooking, OwnerUserId = AccessWorld.Alice, AccountId = AccessWorld.AccountWp, Amount = -500, Timestamp = new DateTimeOffset(day).ToUnixTimeSeconds(), Name = "LIDL" });
        db.Transactions.Add(new Transaction { Id = PrivateBooking, OwnerUserId = AccessWorld.Alice, AccountId = AccessWorld.AccountPrivate, Amount = -300, Timestamp = new DateTimeOffset(day).ToUnixTimeSeconds(), Name = "LIDL" });
        db.ReceiptInvoices.Add(Receipt(AccessWorld.Alice, "A-WP", day, WpBooking, "Geteilter Testkäse"));
        db.ReceiptInvoices.Add(Receipt(AccessWorld.Alice, "A-PRIV", day, PrivateBooking, "Privater Testtee"));
        db.ReceiptInvoices.Add(Receipt(AccessWorld.Alice, "A-OPEN", day, null, "Offene Testnudeln"));
        db.ReceiptInvoices.Add(Receipt(AccessWorld.Bob, "B-OWN", day, null, "Bobs Testapfel"));
        await db.SaveChangesAsync();
        return t;
    }

    private static ReceiptInvoice Receipt(string owner, string number, DateTime day, long? booking, string item) => new()
    {
        OwnerUserId = owner,
        Shop = ReceiptShops.Lidl,
        InvoiceNumber = number,
        OrderDate = day,
        TotalCents = 500,
        PaymentCents = 500,
        ReadAt = day,
        State = booking == null ? ReceiptMatchState.Unmatched : ReceiptMatchState.Matched,
        TransactionId = booking,
        Items =
        {
            new ReceiptItem { OwnerUserId = owner, Name = item, Quantity = "1", TotalCents = 500, Kind = ReceiptItemKind.Product },
            new ReceiptItem { OwnerUserId = owner, Position = 1, Name = "Pfand", Quantity = "1", TotalCents = 25, Kind = ReceiptItemKind.Deposit },
        },
    };

    private static PriceHistoryService Service(TestDb t, string user)
    {
        var view = t.ForUser(user);
        return new PriceHistoryService(view.Factory, t.System, view.Access, new PriceHistoryCache(t.Notifier), t.Notifier, new ChangeOrigin(), TimeProvider.System);
    }

    [Fact]
    public async Task Each_user_sees_their_own_receipts_and_those_on_bookings_they_can_see()
    {
        await using var t = await WorldAsync(ProjectRole.Viewer);
        var alice = (await Service(t, AccessWorld.Alice).GetAsync()).Products.Select(p => p.Name).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["Geteilter Testkäse", "Offene Testnudeln", "Privater Testtee"], alice);
        var bob = (await Service(t, AccessWorld.Bob).GetAsync()).Products.ToList();
        Assert.Equal(["Bobs Testapfel", "Geteilter Testkäse"], bob.Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.False(bob.Single(p => p.Name == "Geteilter Testkäse").Last.IsOwn);
    }

    [Fact]
    public async Task A_user_without_access_sees_nothing_of_another_users_receipts()
    {
        await using var t = await WorldAsync(bobOnWp: null);
        var bob = (await Service(t, AccessWorld.Bob).GetAsync()).Products;
        Assert.Equal(["Bobs Testapfel"], bob.Select(p => p.Name));
        await Assert.ThrowsAsync<AccessDeniedException>(() => Service(t, null!).GetAsync());
    }

    [Fact]
    public async Task Merges_are_stored_per_user_and_can_be_undone()
    {
        await using var t = await WorldAsync(ProjectRole.Viewer);
        var alice = Service(t, AccessWorld.Alice);
        var products = (await alice.GetAsync()).Products;
        var tee = products.Single(p => p.Name == "Privater Testtee").Id;
        var nudeln = products.Single(p => p.Name == "Offene Testnudeln").Id;
        await alice.MergeAsync(tee, nudeln);
        var merged = (await alice.GetAsync()).Products;
        Assert.Equal(2, merged.Count);
        Assert.Equal(2, merged.Single(p => p.Id == nudeln).TimesBought);
        Assert.Equal(2, (await Service(t, AccessWorld.Bob).GetAsync()).Products.Count);
        await alice.UnmergeAsync(tee);
        Assert.Equal(3, (await alice.GetAsync()).Products.Count);
    }
}
