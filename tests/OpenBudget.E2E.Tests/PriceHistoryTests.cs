using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using OpenBudget.Connectors.Receipts;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.E2E.Tests;

/// <summary>
/// A project's Prices view with made-up Lidl receipts: the sidebar link opens it, the period switch changes the URL
/// and the figures, a product opens its detail with chart and all purchases (each opens the receipt panel), one-time
/// purchases hide until asked for, pack formats with stored identities split into products with an odd purchase marked,
/// the receipts settings page has no Prices tab, and a phone shows the page without horizontal scrolling.
/// </summary>
[TestFixture]
public sealed class PriceHistoryTests : EndToEndTestBase
{
    private static int Pid => E2ESeed.ProjectId;

    [SetUp]
    public async Task SeedAsync()
    {
        var factory = Factory.Services.GetRequiredService<SystemDbContextFactory>();
        await EditingSeed.SeedAsync(factory);
        await using var db = await factory.CreateDbContextAsync();
        var today = DateTime.UtcNow.Date;
        // A purchase every 14 days for two years: the butter costs 1.99 € in the earlier year and 2.29 € in the later.
        // The newest receipt is on a booking of the project, so Lidl's open receipts belong to it too.
        for (var i = 0; i < 52; i++)
        {
            var day = today.AddDays(-14 * i);
            var later = day > today.AddMonths(-12);
            db.ReceiptInvoices.Add(new ReceiptInvoice
            {
                OwnerUserId = E2ESeed.OwnerUserId,
                Shop = ReceiptShops.Lidl,
                InvoiceNumber = $"E2E-PRICE-{i:000}",
                Store = "Teststadt",
                OrderDate = day,
                TotalCents = 500,
                PaymentCents = 500,
                ReadAt = DateTime.UtcNow,
                State = i == 0 ? ReceiptMatchState.Matched : ReceiptMatchState.Unmatched,
                TransactionId = i == 0 ? 101 : null,
                ManualMatch = i == 0,
                Items =
                {
                    new ReceiptItem { OwnerUserId = E2ESeed.OwnerUserId, Position = 0, Name = "Testbutter Mild 250g", Quantity = "1", UnitCents = later ? 229 : 199, TotalCents = later ? 229 : 199 },
                    new ReceiptItem { OwnerUserId = E2ESeed.OwnerUserId, Position = 1, Name = "Testbananen", Quantity = "820g", UnitCents = 149, TotalCents = 122 },
                },
            });
        }
        await db.SaveChangesAsync();
        Factory.BrowserHostServices.GetRequiredService<PriceHistoryCache>().Clear();
    }

    [Test]
    public async Task The_sidebar_opens_prices_and_the_period_changes_the_figures()
    {
        await GoToAsync($"/projects/{Pid}/browse");
        var link = Page.GetByTestId("view-prices");
        await link.ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex($"/projects/{Pid}/prices$"));
        await Expect(link).ToHaveAttributeAsync("aria-current", "page");

        var inflation = Page.GetByTestId("prices-stat-inflation").GetByTestId("prices-stat-value");
        // The butter's +15 % weighted by spend against the bananas' unchanged price.
        await Expect(inflation).ToContainTextAsync("+9");
        await Expect(Page.GetByTestId("prices-stat-jump")).ToContainTextAsync("Testbutter Mild 250g");
        await Expect(Page.GetByTestId("prices-stat-regulars")).ToContainTextAsync("2");
        await Expect(Page.GetByTestId("prices-index")).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("prices-period-12m")).ToHaveAttributeAsync("aria-pressed", "true");

        await Page.GetByTestId("prices-period-6m").ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex("period=6m"));
        await Expect(Page.GetByTestId("prices-period-6m")).ToHaveAttributeAsync("aria-pressed", "true");
        await Expect(inflation).ToContainTextAsync("±0");
        await Expect(Page.GetByTestId("prices-stat-jump").GetByTestId("prices-stat-value")).ToHaveTextAsync("–");
    }

    [Test]
    public async Task A_product_shows_its_detail_and_purchases()
    {
        await GoToAsync($"/projects/{Pid}/prices");
        var butter = Page.GetByTestId("price-row").Filter(new() { HasText = "Testbutter Mild 250g" });
        await Expect(butter.GetByTestId("price-row-change")).ToContainTextAsync("+15");
        // The change compares with the last usual price before the period; the row counts all purchases.
        var start = DateOnly.FromDateTime(DateTime.UtcNow.Date).AddMonths(-12);
        var before = Enumerable.Range(0, 52).Select(i => DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(-14 * i))).Where(d => d < start).Max();
        await Expect(butter.GetByTestId("price-row-since")).ToHaveTextAsync("since " + before.ToAppMonth());
        await Expect(butter).ToContainTextAsync("52×");

        await Page.GetByTestId("price-row").Filter(new() { HasText = "Testbananen" }).ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex("product=lidl"));
        await Expect(Page.GetByTestId("price-detail-name")).ToHaveTextAsync("Testbananen");
        await Expect(Page.GetByTestId("price-row").Filter(new() { HasText = "Testbananen" })).ToHaveAttributeAsync("aria-current", "true");
        await Expect(Page.GetByTestId("price-chart")).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("price-now")).ToContainTextAsync("1,49");
        // All purchases are listed, those of the period marked.
        await Expect(Page.GetByTestId("price-purchase")).ToHaveCountAsync(52);
        var inPeriod = await Page.Locator("[data-testid=price-purchase][data-in-period=true]").CountAsync();
        Assert.That(inPeriod, Is.InRange(26, 28));
        await Expect(Page.GetByTestId("price-purchase-date").First).ToHaveTextAsync(new Regex(@"^\d{2}\.\d{2}\.\d{4}$"));

        await Page.GetByTestId("price-purchase-receipt").First.ClickAsync();
        await Expect(Page.GetByText("Lidl Teststadt").First).ToBeVisibleAsync();
    }

    /// <summary>
    /// A weekly cola in two pack sizes with stored identities (12 × 0.5 l crates and 1 l bottles), once a bottle at an
    /// odd price, and a shirt bought once.
    /// </summary>
    private async Task SeedIdentitiesAsync()
    {
        var factory = Factory.Services.GetRequiredService<SystemDbContextFactory>();
        await using var db = await factory.CreateDbContextAsync();
        var today = DateTime.UtcNow.Date;
        for (var i = 0; i < 10; i++)
        {
            var crate = i >= 8;
            var cents = crate ? 1548 : i == 3 ? 599 : 159;
            var receipt = new ReceiptInvoice
            {
                OwnerUserId = E2ESeed.OwnerUserId,
                Shop = ReceiptShops.Lidl,
                InvoiceNumber = $"E2E-COLA-{i:000}",
                OrderDate = today.AddDays(-7 * i - 3),
                TotalCents = 1000,
                PaymentCents = 1000,
                ReadAt = DateTime.UtcNow,
                Items =
                {
                    new ReceiptItem { OwnerUserId = E2ESeed.OwnerUserId, Position = 0, Name = crate ? "TESTCOLA 12X0,5L" : "Testcola 1l", Quantity = "1", UnitCents = cents, TotalCents = cents },
                },
            };
            if (i == 0) receipt.Items.Add(new ReceiptItem { OwnerUserId = E2ESeed.OwnerUserId, Position = 1, Name = "Testhemd Blau", Quantity = "1", UnitCents = 1999, TotalCents = 1999 });
            db.ReceiptInvoices.Add(receipt);
        }
        foreach (var (name, count, size) in new[] { ("TESTCOLA 12X0,5L", (int?)12, 0.5m), ("Testcola 1l", null, 1m) })
            if (!await db.ReceiptProductIdentities.AnyAsync(x => x.OwnerUserId == E2ESeed.OwnerUserId && x.NameKey == PriceAnalysis.NameKey(name)))
                db.ReceiptProductIdentities.Add(new ReceiptProductIdentity
                {
                    OwnerUserId = E2ESeed.OwnerUserId,
                    Shop = ReceiptShops.Lidl,
                    NameKey = PriceAnalysis.NameKey(name),
                    ProductName = "Testcola",
                    PackCount = count,
                    UnitSize = size,
                    Unit = ProductSizeUnit.Litre,
                    Source = ProductIdentitySource.Ai,
                    ResolvedAt = DateTime.UtcNow,
                });
        await db.SaveChangesAsync();
        Factory.BrowserHostServices.GetRequiredService<PriceHistoryCache>().Clear();
    }

    [Test]
    public async Task One_time_purchases_hide_by_default_and_pack_formats_split()
    {
        await SeedIdentitiesAsync();
        await GoToAsync($"/projects/{Pid}/prices");
        // The 1 l bottles and the 12 × 0.5 l crates differ too much in size to compare per litre.
        await Expect(Page.GetByTestId("price-row").Filter(new() { HasText = "Testcola" })).ToHaveCountAsync(2);
        await Expect(Page.GetByTestId("price-row").Filter(new() { HasText = "Testhemd" })).ToHaveCountAsync(0);

        await Page.GetByTestId("prices-once").CheckAsync();
        await Expect(Page).ToHaveURLAsync(new Regex("once=1"));
        await Expect(Page.GetByTestId("price-row").Filter(new() { HasText = "Testhemd" })).ToHaveCountAsync(1);

        var bottle = Page.Locator("[data-testid=price-row][data-product='lidl|testcola#l-1']");
        await Expect(bottle).ToContainTextAsync("8×");
        await bottle.ClickAsync();
        await Expect(Page.GetByTestId("price-detail-name")).ToHaveTextAsync(new Regex("^Testcola · 1 "));
        // 1.59 € for a litre bottle; the 5.99 € bottle is unusual.
        await Expect(Page.GetByTestId("price-now")).ToHaveTextAsync(new Regex(@"1,59.*/l"));
        await Expect(Page.GetByTestId("price-unusual")).ToHaveCountAsync(1);
        await Expect(Page.GetByTestId("price-purchase-date").First).ToHaveTextAsync(new Regex(@"^\d{2}\.\d{2}\.\d{4}$"));

        await Page.Locator("[data-testid=price-row][data-product='lidl|testcola#l-6']").ClickAsync();
        // 15.48 € for a 6 l crate.
        await Expect(Page.GetByTestId("price-purchases")).ToContainTextAsync("2,58");
    }

    [Test]
    public async Task The_receipts_settings_page_has_no_prices_tab()
    {
        await GoToAsync("/receipts?tab=prices");
        await Expect(Page.GetByTestId("receipts-tab-list")).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("receipts-tab-prices")).ToHaveCountAsync(0);
    }

    [Test]
    public async Task A_phone_shows_the_page_without_horizontal_scrolling()
    {
        await Page.SetViewportSizeAsync(390, 844);
        await GoToAsync($"/projects/{Pid}/prices");
        await Expect(Page.GetByTestId("price-detail")).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("bottom-bar").GetByTestId("bottom-prices")).ToHaveAttributeAsync("aria-current", "page");
        Assert.That(await Page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= window.innerWidth"), Is.True);
    }
}
