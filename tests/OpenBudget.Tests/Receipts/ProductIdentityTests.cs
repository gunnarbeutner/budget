using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OpenBudget.Assist;
using OpenBudget.Connectors.Receipts;
using OpenBudget.Models;
using OpenBudget.Services;
using OpenBudget.Tests.Assist;
using Xunit;

namespace OpenBudget.Tests.Receipts;

/// <summary>
/// Product identities (ProductIdentityResolver, PriceAnalysis on identities, PriceHistoryService edits): the AI is
/// asked in batches with names only and its answers stored, a failure leaves names open, the user's rows win; products
/// group by canonical name and compare by unit price across pack sizes, or split when sizes cannot compare; unusual
/// purchases, the minimum evidence for the lists and things that are not products.
/// </summary>
public sealed class ProductIdentityTests
{
    private static readonly DateOnly Today = new(2026, 6, 30);

    private static ProductIdentity Id(string name, int? count = null, decimal? size = null, ProductSizeUnit? unit = null,
        ProductIdentityKind kind = ProductIdentityKind.Product) => new(name, null, count, size, unit, kind, ProductIdentitySource.Ai);

    private static PriceLine Line(string name, DateOnly date, long total, ProductIdentity? identity = null, string quantity = "1", long? unit = null, long receipt = 1) =>
        new(receipt, ReceiptShops.Lidl, date, name, quantity, unit, total, true, identity);

    // ---- grouping and unit prices ----

    [Fact]
    public void Different_pack_sizes_of_one_product_compare_per_litre()
    {
        var crate = Id("Testcola", 12, 0.5m, ProductSizeUnit.Litre);
        var bottle = Id("Testcola", null, 1m, ProductSizeUnit.Litre);
        var lines = new[]
        {
            Line("TESTCOLA 12X0,5L", Today.AddDays(-40), 1548, crate, receipt: 1),
            Line("Testcola 1l", Today.AddDays(-20), 159, bottle, receipt: 2),
            Line("Testcola 1l", Today.AddDays(-5), 159, bottle, receipt: 3),
        };
        var p = Assert.Single(PriceAnalysis.Products(lines));
        Assert.Equal("Testcola", p.Name);
        Assert.Equal(PriceUnit.Litre, p.Unit);
        Assert.False(p.HasPackPrice);
        Assert.Equal([258L, 159L, 159L], p.Purchases.Select(x => x.Price));
        Assert.Null(p.Size);
    }

    [Fact]
    public void Sizes_that_cannot_compare_make_separate_products()
    {
        var lines = new[]
        {
            Line("TESTSAFT 6ER", Today.AddDays(-40), 599, Id("Testsaft", 6, null, ProductSizeUnit.Piece), receipt: 1),
            Line("Testsaft 1l", Today.AddDays(-20), 189, Id("Testsaft", null, 1m, ProductSizeUnit.Litre), receipt: 2),
        };
        var products = PriceAnalysis.Products(lines).OrderBy(p => p.Key, StringComparer.Ordinal).ToList();
        Assert.Equal(["testsaft#l", "testsaft#pcs"], products.Select(p => p.Key));
        Assert.Equal(new ItemSize(1, 1m, ProductSizeUnit.Litre), products[0].Size);
        Assert.Equal(PriceUnit.Each, products[1].Unit);
        Assert.Equal(100, products[1].Last.Price);                   // 5.99 € for six
        Assert.Equal("lidl|testsaft", PriceAnalysis.WithoutKind(products[0].Id));
    }

    [Fact]
    public void Lines_without_an_identity_fall_back_to_the_name_rules()
    {
        var lines = new[]
        {
            Line("Testkekse Hafer 500g", Today.AddDays(-40), 199, receipt: 1),
            Line("TESTKEKSE HAFER 1kg", Today.AddDays(-20), 379, receipt: 2),
        };
        var p = Assert.Single(PriceAnalysis.Products(lines));
        Assert.Equal("testkekse hafer", p.Key);
        Assert.Equal(PriceUnit.Kg, p.Unit);
        Assert.Equal([398L, 379L], p.Purchases.Select(x => x.Price));
    }

    [Fact]
    public void Things_that_are_not_products_are_left_out()
    {
        var lines = new[]
        {
            Line("Beispiel Mitgliedsgebühr", Today.AddDays(-40), 899, Id("Mitgliedschaft", kind: ProductIdentityKind.NotAProduct), receipt: 1),
            Line("Beispiel Mitgliedsgebühr", Today.AddDays(-10), 899, Id("Mitgliedschaft", kind: ProductIdentityKind.NotAProduct), receipt: 2),
            Line("Testtee", Today.AddDays(-10), 199, receipt: 2),
        };
        Assert.Equal(["Testtee"], PriceAnalysis.Products(lines).Select(p => p.Name));
    }

    // ---- unusual purchases and minimum evidence ----

    [Fact]
    public void A_single_odd_purchase_is_unusual_and_left_out_of_the_change()
    {
        var start = Today.AddMonths(-12);
        // Four rolls at a time for 0.30 € each, then 0.32 €; once a single line of 1.59 € for "one".
        var lines = new List<PriceLine>();
        for (var i = 0; i < 4; i++) lines.Add(Line("Testbrezel", start.AddDays(-120 + i * 25), 120, quantity: "4", unit: 30, receipt: 10 + i));
        for (var i = 0; i < 3; i++) lines.Add(Line("Testbrezel", start.AddDays(30 + i * 60), 128, quantity: "4", unit: 32, receipt: 20 + i));
        lines.Add(Line("Testbrezel", Today.AddDays(-3), 159, receipt: 30));
        var p = Assert.Single(PriceAnalysis.Products(lines));
        var d = PriceAnalysis.Detail(p, start, Today)!;
        var odd = Assert.Single(d.All, x => x.Unusual);
        Assert.Equal(159, odd.Price);
        Assert.False(odd.Offer);
        Assert.Equal(32, d.Now);
        Assert.Equal(30, d.Before);
        Assert.Equal(32m / 30m - 1m, d.Change);
        Assert.Equal(32, d.Cheapest.Price);
        Assert.DoesNotContain(d.Rises, r => r.After == 159);
        Assert.Equal(8, d.All.Count);
        Assert.Equal(4, d.Points.Count);
    }

    [Fact]
    public void Unusual_needs_three_purchases_and_a_factor_of_two()
    {
        Assert.All(PriceAnalysis.Unusual([30, 159]), Assert.False);
        Assert.Equal([false, false, false, true], PriceAnalysis.Unusual([30, 30, 32, 65]));
        Assert.Equal([false, false, false, false], PriceAnalysis.Unusual([30, 30, 32, 60]));
        Assert.Equal([true, false, false, false], PriceAnalysis.Unusual([14, 30, 30, 32]));
    }

    [Fact]
    public void The_lists_show_products_bought_twice_unless_one_time_purchases_are_asked_for()
    {
        var start = Today.AddMonths(-12);
        var lines = new[]
        {
            Line("Testhemd", Today.AddDays(-30), 2499, receipt: 1),
            Line("Testmilch", start.AddDays(-30), 99, receipt: 2),
            Line("Testmilch", Today.AddDays(-30), 109, receipt: 3),
            Line("Testquark", Today.AddDays(-60), 79, receipt: 4),
            Line("Testquark", Today.AddDays(-30), 89, receipt: 5),
        };
        var summary = PriceAnalysis.Summarise(PriceAnalysis.Products(lines), [], PricePeriod.TwelveMonths, Today);
        Assert.Equal(3, summary.Rows.Count);
        Assert.Equal(["Testmilch", "Testquark"], PriceAnalysis.Listed(summary.Rows, oneTime: false).Select(r => r.Product.Name).Order(StringComparer.Ordinal));
        Assert.Equal(3, PriceAnalysis.Listed(summary.Rows, oneTime: true).Count());
        // A change needs a purchase before the period and one in it: the quark was bought in the period only.
        Assert.NotNull(summary.Rows.Single(r => r.Product.Name == "Testmilch").Detail.Change);
        Assert.Null(summary.Rows.Single(r => r.Product.Name == "Testquark").Detail.Change);
        Assert.Null(summary.Rows.Single(r => r.Product.Name == "Testquark").Detail.Before);
    }

    // ---- the resolver ----

    private static async Task<TestDb> ReceiptsAsync(bool optIn, params string[] names)
    {
        var t = new TestDb();
        await using var db = await t.System.CreateDbContextAsync();
        var day = new DateTime(2026, 5, 2, 0, 0, 0, DateTimeKind.Utc);
        var r = new ReceiptInvoice
        {
            OwnerUserId = TestDb.DefaultOwner,
            Shop = ReceiptShops.Lidl,
            InvoiceNumber = "T-1",
            OrderDate = day,
            TotalCents = 98765,
            PaymentCents = 98765,
            ReadAt = day,
        };
        foreach (var (name, i) in names.Select((n, i) => (n, i)))
            r.Items.Add(new ReceiptItem { OwnerUserId = TestDb.DefaultOwner, Position = i, Name = name, Quantity = "1", TotalCents = 4321, Kind = ReceiptItemKind.Product });
        r.Items.Add(new ReceiptItem { OwnerUserId = TestDb.DefaultOwner, Position = names.Length, Name = "Pfand", Quantity = "1", TotalCents = 25, Kind = ReceiptItemKind.Deposit });
        db.ReceiptInvoices.Add(r);
        if (optIn) (await db.UserDataStates.SingleAsync(s => s.UserId == TestDb.DefaultOwner)).UseAiSuggestions = true;
        await db.SaveChangesAsync();
        return t;
    }

    /// <summary>Answers every item: "Versand…" is not a product, everything else a crate of 12 × 0.5 l named in title case.</summary>
    private static string Answer(AssistRequest r)
    {
        var items = (JsonNode.Parse(r.UserContent)!["items"] as JsonArray)!;
        var answers = items.Select(i =>
        {
            var name = (string)i!["name"]!;
            return name.StartsWith("Versand", StringComparison.Ordinal)
                ? new JsonObject { ["id"] = (string)i["id"]!, ["product"] = "Versand", ["brand"] = null, ["packCount"] = null, ["unitSize"] = null, ["unit"] = null, ["kind"] = "not_a_product" }
                : new JsonObject { ["id"] = (string)i["id"]!, ["product"] = "Produkt " + name, ["brand"] = "Beispiel", ["packCount"] = 12, ["unitSize"] = 0.5, ["unit"] = "l", ["kind"] = "product" };
        });
        return new JsonObject { ["items"] = new JsonArray(answers.Select(a => (JsonNode)a).ToArray()) }.ToJsonString();
    }

    private static ProductIdentityResolver Resolver(TestDb t, IAssistClient client) =>
        new(t.System, client, TimeProvider.System, NullLogger<ProductIdentityResolver>.Instance);

    private static async Task<List<ReceiptProductIdentity>> RowsAsync(TestDb t)
    {
        await using var db = await t.System.CreateDbContextAsync();
        return await db.ReceiptProductIdentities.AsNoTracking().OrderBy(i => i.NameKey).ToListAsync();
    }

    [Fact]
    public async Task The_resolver_asks_in_batches_with_names_only_and_stores_the_answers()
    {
        var names = Enumerable.Range(1, 119).Select(i => $"Testartikel {i:000}").Append("Versandkosten").Append("TESTARTIKEL 001").ToArray();
        await using var t = await ReceiptsAsync(optIn: true, names);
        var ai = new FakeAssistClient(Answer);
        Assert.Equal(120, await Resolver(t, ai).ResolveAsync(TestDb.DefaultOwner, 10, TimeSpan.Zero, default));

        Assert.Equal([50, 50, 20], ai.Requests.Select(r => Regex.Count(r.UserContent, "\"id\"")));
        Assert.All(ai.Requests, r =>
        {
            Assert.Equal(ProductIdentityResolver.SchemaName, r.SchemaName);
            Assert.DoesNotContain("4321", r.UserContent, StringComparison.Ordinal);
            Assert.DoesNotContain("98765", r.UserContent, StringComparison.Ordinal);
            Assert.DoesNotContain("2026", r.UserContent, StringComparison.Ordinal);
            Assert.DoesNotContain("Pfand", r.UserContent, StringComparison.Ordinal);
            Assert.DoesNotContain(TestDb.DefaultOwner, r.UserContent, StringComparison.Ordinal);
        });
        var rows = await RowsAsync(t);
        Assert.Equal(120, rows.Count);                                    // "TESTARTIKEL 001" folds like "Testartikel 001"
        var first = rows.Single(r => r.NameKey == "testartikel 001");
        Assert.Equal(("Produkt Testartikel 001", "Beispiel", 12, 0.5m, ProductSizeUnit.Litre, ProductIdentitySource.Ai, "fake"),
            (first.ProductName, first.Brand, first.PackCount, first.UnitSize, first.Unit, first.Source, first.Model));
        Assert.Equal(ProductIdentityKind.NotAProduct, rows.Single(r => r.NameKey == "versandkosten").Kind);

        // Nothing left: a second run asks nothing.
        Assert.Equal(0, await Resolver(t, ai).ResolveAsync(TestDb.DefaultOwner, 10, TimeSpan.Zero, default));
        Assert.Equal(3, ai.Requests.Count);
    }

    [Fact]
    public async Task A_failing_request_leaves_its_names_unresolved()
    {
        var names = Enumerable.Range(1, 70).Select(i => $"Testartikel {i:000}").ToArray();
        await using var t = await ReceiptsAsync(optIn: true, names);
        var calls = 0;
        var ai = new FakeAssistClient(r => ++calls == 1 ? Answer(r) : throw new AssistException("down"));
        Assert.Equal(50, await Resolver(t, ai).ResolveAsync(TestDb.DefaultOwner, 10, TimeSpan.Zero, default));
        Assert.Equal(50, (await RowsAsync(t)).Count);
        Assert.Equal(20, (await Resolver(t, ai).UnresolvedAsync(TestDb.DefaultOwner, default)).Count);

        // Unreadable answers fail the same way.
        var broken = new FakeAssistClient(_ => "{\"nope\":1}");
        Assert.Equal(0, await Resolver(t, broken).ResolveAsync(TestDb.DefaultOwner, 10, TimeSpan.Zero, default));
        Assert.Equal(50, (await RowsAsync(t)).Count);
    }

    [Fact]
    public void Answers_with_unknown_ids_or_implausible_fields_are_cleaned()
    {
        var json = """
            {"items":[
              {"id":"n1","product":"  Testcola ","brand":null,"packCount":-3,"unitSize":0.5,"unit":"barrel","kind":"product"},
              {"id":"n9","product":"Fremd","brand":null,"packCount":null,"unitSize":null,"unit":null,"kind":"product"},
              {"id":"n2","product":"","brand":null,"packCount":null,"unitSize":null,"unit":null,"kind":"product"}
            ]}
            """;
        var answers = ProductIdentityResolver.ParseAnswer(new AssistResponse(json, null, "fake", 1, 0, 1, TimeSpan.Zero), new HashSet<string>(["n1", "n2"], StringComparer.Ordinal));
        var a = Assert.Single(answers);
        Assert.Equal(new ProductIdentityAnswer("n1", "Testcola", null, null, null, null, ProductIdentityKind.Product), a);
    }

    [Fact]
    public async Task Without_an_ai_or_the_owners_consent_nothing_is_sent()
    {
        await using var t = await ReceiptsAsync(optIn: false, "Testartikel");
        var ai = new FakeAssistClient(Answer);
        Assert.Equal(0, await Resolver(t, ai).ResolveAsync(TestDb.DefaultOwner, 10, TimeSpan.Zero, default));
        await using var u = await ReceiptsAsync(optIn: true, "Testartikel");
        var off = new FakeAssistClient(Answer, enabled: false);
        Assert.Equal(0, await Resolver(u, off).ResolveAsync(TestDb.DefaultOwner, 10, TimeSpan.Zero, default));
        Assert.Empty(ai.Requests);
        Assert.Empty(off.Requests);
        Assert.Empty(await RowsAsync(u));
    }

    [Fact]
    public async Task The_users_rows_are_never_sent_or_overwritten()
    {
        await using var t = await ReceiptsAsync(optIn: true, "Testartikel A", "Testartikel B");
        await using (var db = await t.System.CreateDbContextAsync())
        {
            db.ReceiptProductIdentities.Add(new ReceiptProductIdentity
            {
                OwnerUserId = TestDb.DefaultOwner,
                Shop = ReceiptShops.Lidl,
                NameKey = PriceAnalysis.NameKey("Testartikel A"),
                ProductName = "Mein Name",
                Source = ProductIdentitySource.User,
                ResolvedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        var ai = new FakeAssistClient(Answer);
        Assert.Equal(1, await Resolver(t, ai).ResolveAsync(TestDb.DefaultOwner, 10, TimeSpan.Zero, default));
        Assert.DoesNotContain("Testartikel A", Assert.Single(ai.Requests).UserContent, StringComparison.Ordinal);
        var rows = await RowsAsync(t);
        Assert.Equal(("Mein Name", ProductIdentitySource.User), (rows[0].ProductName, rows[0].Source));
        Assert.Equal(ProductIdentitySource.Ai, rows[1].Source);
    }

    // ---- the service: identities and edits per user ----

    private static PriceHistoryService Service(TestDb t, string user)
    {
        var view = t.ForUser(user);
        var receipts = new ReceiptService(view.Factory, t.System, view.Access, null!, new ReceiptStore(), null!, t.Notifier, new ChangeOrigin(), TimeProvider.System, TestRegistry.Connectors);
        return new PriceHistoryService(view.Factory, t.System, view.Access, receipts, new PriceHistoryCache(t.Notifier), t.Notifier, new ChangeOrigin(), TimeProvider.System);
    }

    [Fact]
    public async Task The_owners_identities_apply_to_viewers_and_each_users_edit_to_themselves()
    {
        await using var t = await AccessWorld.BuildAsync(ProjectRole.Viewer);
        var booking = AccessWorld.Id(AccessWorld.SourceAlice, 60);
        var day = new DateTime(2026, 5, 2, 0, 0, 0, DateTimeKind.Utc);
        await using (var db = await t.System.CreateDbContextAsync())
        {
            db.Transactions.Add(new Transaction { Id = booking, OwnerUserId = AccessWorld.Alice, AccountId = AccessWorld.AccountWp, Amount = -500, Timestamp = new DateTimeOffset(day).ToUnixTimeSeconds(), Name = "LIDL" });
            var r = new ReceiptInvoice
            {
                OwnerUserId = AccessWorld.Alice,
                Shop = ReceiptShops.Lidl,
                InvoiceNumber = "A-1",
                OrderDate = day,
                TotalCents = 500,
                PaymentCents = 500,
                ReadAt = day,
                State = ReceiptMatchState.Matched,
                TransactionId = booking,
            };
            r.Items.Add(new ReceiptItem { OwnerUserId = AccessWorld.Alice, Position = 0, Name = "TESTBTR MILD", Quantity = "1", TotalCents = 229, Kind = ReceiptItemKind.Product });
            r.Items.Add(new ReceiptItem { OwnerUserId = AccessWorld.Alice, Position = 1, Name = "Testgebühr", Quantity = "1", TotalCents = 99, Kind = ReceiptItemKind.Product });
            db.ReceiptInvoices.Add(r);
            db.ReceiptProductIdentities.Add(new ReceiptProductIdentity
            {
                OwnerUserId = AccessWorld.Alice,
                Shop = ReceiptShops.Lidl,
                NameKey = PriceAnalysis.NameKey("TESTBTR MILD"),
                ProductName = "Testbutter mild",
                PackCount = null,
                UnitSize = 250,
                Unit = ProductSizeUnit.Gram,
                Source = ProductIdentitySource.Ai,
                ResolvedAt = day,
            });
            await db.SaveChangesAsync();
        }

        var bob = (await Service(t, AccessWorld.Bob).GetAsync(AccessWorld.Shared)).Products;
        var butter = bob.Single(p => p.Name == "Testbutter mild");
        Assert.Equal((916L, PriceUnit.Kg), (butter.Last.Price, butter.Unit));

        await Service(t, AccessWorld.Bob).EditAsync(ReceiptShops.Lidl, ["Testgebühr"], new ProductEdit("Gebühr", null, null, null, NotAProduct: true));
        await Service(t, AccessWorld.Alice).EditAsync(ReceiptShops.Lidl, ["TESTBTR MILD"], new ProductEdit("Butter", null, null, null, NotAProduct: false));

        // Bob left the fee out for himself only; Alice's edit of her own line is its identity for everyone who sees it.
        Assert.Equal(["Butter"], (await Service(t, AccessWorld.Bob).GetAsync(AccessWorld.Shared)).Products.Select(p => p.Name));
        var alice = (await Service(t, AccessWorld.Alice).GetAsync(AccessWorld.Shared)).Products;
        Assert.Equal(["Butter", "Testgebühr"], alice.Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Equal(PriceUnit.Piece, alice.Single(p => p.Name == "Butter").Unit);  // her edit has no size
    }
}
