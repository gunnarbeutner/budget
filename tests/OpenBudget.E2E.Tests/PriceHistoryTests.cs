using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using OpenBudget.Connectors.Receipts;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.E2E.Tests;

/// <summary>
/// The receipts page's Prices tab with made-up Lidl receipts: a product whose price rose lists its change, its detail
/// shows the purchases and opens a receipt, and the personal inflation shows a value.
/// </summary>
[TestFixture]
public sealed class PriceHistoryTests : EndToEndTestBase
{
    [SetUp]
    public async Task SeedAsync()
    {
        var factory = Factory.Services.GetRequiredService<SystemDbContextFactory>();
        await using var db = await factory.CreateDbContextAsync();
        var today = DateTime.UtcNow.Date;
        // A purchase every 14 days for two years: the butter costs 1.99 € in the earlier year and 2.29 € in the later.
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
                State = ReceiptMatchState.Matched,
                Items =
                {
                    new ReceiptItem { OwnerUserId = E2ESeed.OwnerUserId, Position = 0, Name = "Testbutter Mild 250g", Quantity = "1", UnitCents = later ? 229 : 199, TotalCents = later ? 229 : 199 },
                    new ReceiptItem { OwnerUserId = E2ESeed.OwnerUserId, Position = 1, Name = "Testbananen", Quantity = "820g", UnitCents = 149, TotalCents = 122 },
                },
            });
        }
        await db.SaveChangesAsync();
        Factory.Services.GetRequiredService<PriceHistoryCache>().Clear();
    }

    [Test]
    public async Task Products_detail_and_index()
    {
        await GoToAsync("/receipts?tab=prices");
        var row = Page.GetByTestId("price-row").Filter(new() { HasText = "Testbutter Mild 250g" });
        await Expect(row).ToBeVisibleAsync();
        await Expect(row.GetByTestId("price-row-change")).ToContainTextAsync("+15");

        await row.ClickAsync();
        await Expect(Page.GetByTestId("price-detail-name")).ToHaveTextAsync("Testbutter Mild 250g");
        await Expect(Page.GetByTestId("price-purchase")).ToHaveCountAsync(52);
        await Expect(Page.GetByTestId("price-chart")).ToBeVisibleAsync();
        await Page.GetByTestId("price-purchase-receipt").First.ClickAsync();
        await Expect(Page.GetByText("Lidl Teststadt").First).ToBeVisibleAsync();
        await Page.Keyboard.PressAsync("Escape");

        await Page.GetByTestId("prices-section-index").ClickAsync();
        var overall = Page.Locator("[data-testid=price-index-row][data-period='12M'][data-shop=all]");
        await Expect(overall.GetByTestId("price-index-value")).ToContainTextAsync("+");
        await Expect(Page.GetByTestId("price-movers-up")).ToContainTextAsync("Testbutter Mild 250g");

        await Page.GetByTestId("prices-section-regulars").ClickAsync();
        await Expect(Page.GetByTestId("price-regular").Filter(new() { HasText = "Testbananen" })).ToBeVisibleAsync();
    }
}
