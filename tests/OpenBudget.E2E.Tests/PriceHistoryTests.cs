using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using OpenBudget.Connectors.Receipts;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.E2E.Tests;

/// <summary>
/// A project's Prices view with made-up Lidl receipts: the sidebar link opens it, the period switch changes the URL
/// and the figures, a product opens its detail with chart and purchases (each opens the receipt panel), the receipts
/// settings page has no Prices tab, and a phone shows the page without horizontal scrolling.
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

        await Page.GetByTestId("price-row").Filter(new() { HasText = "Testbananen" }).ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex("product=lidl"));
        await Expect(Page.GetByTestId("price-detail-name")).ToHaveTextAsync("Testbananen");
        await Expect(Page.GetByTestId("price-row").Filter(new() { HasText = "Testbananen" })).ToHaveAttributeAsync("aria-current", "true");
        await Expect(Page.GetByTestId("price-chart")).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("price-now")).ToContainTextAsync("1,49");
        var purchases = await Page.GetByTestId("price-purchase").CountAsync();
        Assert.That(purchases, Is.InRange(26, 28));

        await Page.GetByTestId("price-purchase-receipt").First.ClickAsync();
        await Expect(Page.GetByText("Lidl Teststadt").First).ToBeVisibleAsync();
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
