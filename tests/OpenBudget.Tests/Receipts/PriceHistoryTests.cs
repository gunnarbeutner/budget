using OpenBudget.Connectors.Receipts;
using OpenBudget.Models;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests.Receipts;

/// <summary>
/// Price history from receipt items (PriceAnalysis, PriceHistoryService): product keys, sizes, unit prices, merges,
/// regular purchases, the period figures (basket index, offers, rises, biggest jump, grocery spend) and which receipts
/// a project's history reads for each user.
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
        Assert.True(a.Length <= ReceiptSourceProfiles.Default.For(ReceiptShops.Amazon)!.ProductKeyLength);
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

    // ---- period figures ----

    [Fact]
    public void The_index_series_starts_at_100_and_follows_the_usual_price()
    {
        var start = Today.AddMonths(-12);
        // Bread every 30 days: 2.00 € for the first half, 2.20 € after; one 1.50 € offer in the second half is left out.
        var bread = Enumerable.Range(0, 12).Select(i => Line("Testbrot", start.AddDays(5 + i * 30), i == 9 ? 150 : i < 6 ? 200 : 220, receipt: 100 + i));
        var quark = Repeated("Testquark", start.AddDays(5), 12, 30, 100, 200);                       // ±0
        var juice = Repeated("Testsaft", start.AddDays(5), 12, 30, 300, 300).Select(l => l with { Shop = ReceiptShops.Rewe });  // ±0, another shop
        var rare = Repeated("Selten", start.AddDays(5), 2, 30, 999, 400);                            // too few purchases
        var products = PriceAnalysis.Products(bread.Concat(quark).Concat(juice).Concat(rare));

        var lines = PriceAnalysis.IndexSeries(products, start, Today);
        var overall = lines.Single(l => l.Shop == null);
        Assert.Equal(3, overall.Products);
        Assert.Equal(100m, overall.Points[0].Value);
        Assert.Equal(start, overall.Points[0].Date);
        Assert.Equal(Today, overall.Points[^1].Date);
        Assert.All(overall.Points.Skip(1).SkipLast(1), p => Assert.Equal(1, p.Date.AddDays(1).Day));
        // weights: bread 2,450, quark 1,200, juice 3,600 (spend in the period); bread +10 %.
        Assert.Equal(Math.Round(100m * (2450m * 1.1m + 1200m + 3600m) / 7250m, 1), overall.Latest);
        Assert.Equal(Math.Round(100m * (2450m * 1.1m + 1200m) / 3650m, 1), lines.Single(l => l.Shop == ReceiptShops.Lidl).Latest);
        Assert.Equal(100m, lines.Single(l => l.Shop == ReceiptShops.Rewe).Latest);
        Assert.True(overall.Points.Zip(overall.Points.Skip(1)).All(p => p.First.Value <= p.Second.Value));
    }

    [Fact]
    public void The_index_needs_products_bought_often_in_both_halves()
    {
        var products = PriceAnalysis.Products(Repeated("Testbrot", Today.AddMonths(-3), 6, 14, 200));
        Assert.Empty(PriceAnalysis.IndexSeries(products, Today.AddMonths(-12), Today));
        var summary = PriceAnalysis.Summarise(products, [], PricePeriod.TwelveMonths, Today);
        Assert.Null(summary.Overall);
    }

    [Fact]
    public void An_offer_is_a_price_well_below_the_usual_one_on_both_sides()
    {
        Assert.Equal([false, false, false, true, false, false], PriceAnalysis.Offers([199, 199, 199, 149, 199, 199]));
        // A price rise leaves the earlier, cheaper purchases alone.
        Assert.All(PriceAnalysis.Offers([199, 199, 199, 199, 229, 229, 229, 229]), Assert.False);
        // 10 % below counts, 9 % does not; the first purchase is judged by the next two.
        Assert.Equal([true, false, false], PriceAnalysis.Offers([90, 100, 100]));
        Assert.Equal([false, false, false], PriceAnalysis.Offers([91, 100, 100]));
    }

    [Fact]
    public void The_detail_marks_offers_rises_and_the_change_of_the_usual_price()
    {
        var start = Today.AddMonths(-12);
        var prices = new long[] { 129, 129, 129, 99, 129, 149, 149, 149 };
        var lines = prices.Select((c, i) => Line("Testmilch", start.AddDays(-30 + i * 45), c, receipt: i + 1));
        var p = Assert.Single(PriceAnalysis.Products(lines));
        var d = PriceAnalysis.Detail(p, start, Today)!;
        Assert.Equal(7, d.Points.Count);                       // the first purchase is before the period
        Assert.Equal(129, d.Base);
        Assert.Equal(149, d.Now);
        Assert.Equal(149m / 129m - 1m, d.Change);
        var offer = Assert.Single(d.Points, x => x.Offer);
        Assert.Equal(99, offer.Price);
        Assert.Same(offer, d.Cheapest);
        var rise = Assert.Single(d.Rises);
        Assert.Equal((129L, 149L), (rise.Before, rise.After));
    }

    [Fact]
    public void The_detail_can_price_per_kg()
    {
        var p = Assert.Single(PriceAnalysis.Products(Repeated("Testkekse 500g", Today.AddDays(-90), 4, 30, 199)));
        Assert.True(p.HasBase);
        Assert.Equal(398, PriceAnalysis.Detail(p, Today.AddMonths(-6), Today, perBase: true)!.Now);
    }

    [Fact]
    public void The_biggest_jump_is_the_largest_rise_bought_at_least_twice()
    {
        var start = Today.AddMonths(-12);
        var lines = Repeated("Testbutter", start.AddDays(-20), 2, 10, 199, 100).Concat(Repeated("Testbutter", start.AddDays(60), 3, 30, 239, 110))   // +20 %
            .Concat(Repeated("Testkaffee", start.AddDays(-20), 2, 10, 799, 200)).Concat(Repeated("Testkaffee", start.AddDays(60), 3, 30, 879, 210))  // +10 %
            .Concat(Repeated("Testöl", start.AddDays(-20), 2, 10, 299, 300)).Concat(Repeated("Testöl", start.AddDays(60), 1, 30, 499, 310));         // once only
        var summary = PriceAnalysis.Summarise(PriceAnalysis.Products(lines), [], PricePeriod.TwelveMonths, Today);
        Assert.Equal("Testbutter", summary.BiggestJump!.Product.Name);
        Assert.Equal(0.2010m, Math.Round(summary.BiggestJump.Detail.Change!.Value, 4));
        Assert.Null(PriceAnalysis.BiggestJump(summary.Rows.Where(r => r.Product.Name == "Testquark")));
    }

    [Fact]
    public void Grocery_spend_averages_rewe_and_lidl_payments_per_month()
    {
        var receipts = new[]
        {
            new ReceiptSpend(ReceiptShops.Rewe, Today.AddDays(-60), 6000),
            new ReceiptSpend(ReceiptShops.ReweEbon, Today.AddDays(-30), 2000),
            new ReceiptSpend(ReceiptShops.Lidl, Today.AddDays(-1), 4000),
            new ReceiptSpend(ReceiptShops.Amazon, Today.AddDays(-10), 99_00),        // not groceries
            new ReceiptSpend(ReceiptShops.Lidl, Today.AddMonths(-13), 50_00),        // before the period
            new ReceiptSpend(ReceiptShops.Lidl, Today.AddDays(-5), -300),            // a return
        };
        var monthly = PriceAnalysis.GroceryMonthly(receipts, Today.AddMonths(-12), Today);
        Assert.Equal((long)Math.Round(12000 / (61 / 30.44)), monthly);
        Assert.Null(PriceAnalysis.GroceryMonthly(receipts.Where(r => r.Shop == ReceiptShops.Amazon), Today.AddMonths(-12), Today));
    }

    [Fact]
    public void Periods_start_months_back_or_at_the_first_purchase()
    {
        Assert.Equal(new DateOnly(2025, 12, 30), PriceAnalysis.PeriodStart(PricePeriod.SixMonths, Today, null));
        Assert.Equal(new DateOnly(2023, 6, 30), PriceAnalysis.PeriodStart(PricePeriod.ThreeYears, Today, null));
        Assert.Equal(new DateOnly(2024, 2, 1), PriceAnalysis.PeriodStart(PricePeriod.All, Today, new DateOnly(2024, 2, 1)));
    }

    // ---- project scope ----

    private static readonly long WpBooking = AccessWorld.Id(AccessWorld.SourceAlice, 60);
    private static readonly long PrivateBooking = AccessWorld.Id(AccessWorld.SourceAlice, 61);

    private static async Task<TestDb> WorldAsync(ProjectRole? bobOnWp)
    {
        var t = await AccessWorld.BuildAsync(bobOnWp);
        await using var db = await t.System.CreateDbContextAsync();
        var day = new DateTime(2026, 5, 2, 0, 0, 0, DateTimeKind.Utc);
        db.Transactions.Add(new Transaction { Id = WpBooking, OwnerUserId = AccessWorld.Alice, AccountId = AccessWorld.AccountWp, Amount = -500, Timestamp = new DateTimeOffset(day).ToUnixTimeSeconds(), Name = "LIDL" });
        db.Transactions.Add(new Transaction { Id = PrivateBooking, OwnerUserId = AccessWorld.Alice, AccountId = AccessWorld.AccountPrivate, Amount = -300, Timestamp = new DateTimeOffset(day).ToUnixTimeSeconds(), Name = "LIDL" });
        db.ReceiptInvoices.Add(Receipt(AccessWorld.Alice, "A-WP", day, WpBooking, "Geteilter Testkäse", "Geteilte Testbutter"));
        db.ReceiptInvoices.Add(Receipt(AccessWorld.Alice, "A-PRIV", day, PrivateBooking, "Privater Testtee"));
        db.ReceiptInvoices.Add(Receipt(AccessWorld.Bob, "B-OWN", day, null, "Bobs Testapfel"));
        await db.SaveChangesAsync();
        return t;
    }

    private static ReceiptInvoice Receipt(string owner, string number, DateTime day, long? booking, params string[] items)
    {
        var r = new ReceiptInvoice
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
        };
        foreach (var (item, i) in items.Select((x, i) => (x, i)))
            r.Items.Add(new ReceiptItem { OwnerUserId = owner, Position = i, Name = item, Quantity = "1", TotalCents = 250, Kind = ReceiptItemKind.Product });
        r.Items.Add(new ReceiptItem { OwnerUserId = owner, Position = items.Length, Name = "Pfand", Quantity = "1", TotalCents = 25, Kind = ReceiptItemKind.Deposit });
        return r;
    }

    private static PriceHistoryService Service(TestDb t, string user)
    {
        var view = t.ForUser(user);
        var receipts = new ReceiptService(view.Factory, t.System, view.Access, null!, new ReceiptStore(), null!, t.Notifier, new ChangeOrigin(), TimeProvider.System, TestRegistry.Connectors);
        return new PriceHistoryService(view.Factory, t.System, view.Access, receipts, new PriceHistoryCache(t.Notifier), t.Notifier, new ChangeOrigin(), TimeProvider.System);
    }

    private static async Task<List<string>> NamesAsync(TestDb t, string user, int project) =>
        (await Service(t, user).GetAsync(project)).Products.Select(p => p.Name).Order(StringComparer.Ordinal).ToList();

    [Fact]
    public async Task A_project_shows_the_products_of_its_receipts_only()
    {
        await using var t = await WorldAsync(ProjectRole.Viewer);
        Assert.Equal(["Geteilte Testbutter", "Geteilter Testkäse"], await NamesAsync(t, AccessWorld.Alice, AccessWorld.Shared));
        Assert.Equal(["Privater Testtee"], await NamesAsync(t, AccessWorld.Alice, AccessWorld.Private));
        var view = await Service(t, AccessWorld.Alice).GetAsync(AccessWorld.Shared);
        Assert.Equal([new ReceiptSpend(ReceiptShops.Lidl, new DateOnly(2026, 5, 2), 500)], view.Receipts);
    }

    [Fact]
    public async Task A_viewer_of_the_shared_project_sees_its_products_but_not_another_projects()
    {
        await using var t = await WorldAsync(ProjectRole.Viewer);
        var bob = (await Service(t, AccessWorld.Bob).GetAsync(AccessWorld.Shared)).Products;
        Assert.Equal(["Geteilte Testbutter", "Geteilter Testkäse"], bob.Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.All(bob, p => Assert.False(p.Last.IsOwn));
        Assert.DoesNotContain("Privater Testtee", await NamesAsync(t, AccessWorld.Bob, AccessWorld.BobProject));
        await Assert.ThrowsAsync<AccessDeniedException>(() => Service(t, AccessWorld.Bob).GetAsync(AccessWorld.Private));
    }

    [Fact]
    public async Task Without_access_a_project_shows_nothing()
    {
        await using var t = await WorldAsync(bobOnWp: null);
        await Assert.ThrowsAsync<AccessDeniedException>(() => Service(t, AccessWorld.Bob).GetAsync(AccessWorld.Shared));
        await Assert.ThrowsAsync<AccessDeniedException>(() => Service(t, null!).GetAsync(AccessWorld.Shared));
    }

    [Fact]
    public async Task Merges_are_stored_per_user_and_can_be_undone()
    {
        await using var t = await WorldAsync(ProjectRole.Viewer);
        var alice = Service(t, AccessWorld.Alice);
        var products = (await alice.GetAsync(AccessWorld.Shared)).Products;
        var butter = products.Single(p => p.Name == "Geteilte Testbutter").Id;
        var cheese = products.Single(p => p.Name == "Geteilter Testkäse").Id;
        await alice.MergeAsync(butter, cheese);
        var merged = Assert.Single((await alice.GetAsync(AccessWorld.Shared)).Products);
        Assert.Equal(cheese, merged.Id);
        Assert.Equal(2, (await Service(t, AccessWorld.Bob).GetAsync(AccessWorld.Shared)).Products.Count);
        await alice.UnmergeAsync(butter);
        Assert.Equal(2, (await alice.GetAsync(AccessWorld.Shared)).Products.Count);
    }
}
