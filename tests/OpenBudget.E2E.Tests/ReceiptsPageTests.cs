using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.E2E.Tests;

/// <summary>
/// The receipts page with many made-up receipts: status chips with counts, search, a page of rows at a time, the
/// assign dialog, the settings tab, and no horizontal scroll at phone width.
/// </summary>
[TestFixture]
public sealed class ReceiptsPageTests : EndToEndTestBase
{
    [SetUp]
    public async Task SeedAsync()
    {
        var factory = Factory.Services.GetRequiredService<SystemDbContextFactory>();
        await using var db = await factory.CreateDbContextAsync();
        var day = DateTime.UtcNow.Date.AddDays(-40);
        for (var i = 0; i < 60; i++)
        {
            db.ReceiptInvoices.Add(new ReceiptInvoice
            {
                OwnerUserId = E2ESeed.OwnerUserId,
                Shop = ReceiptShops.Lidl,
                InvoiceNumber = $"E2E-LIDL-{i:000}",
                Store = i == 7 ? "Musterstadt Sample Street With A Rather Long Name 12345" : $"Teststadt {i:000}",
                OrderDate = day.AddDays(-i),
                TotalCents = 1000 + i,
                PaymentCents = 1000 + i,
                ReadAt = DateTime.UtcNow,
                State = i < 3 ? ReceiptMatchState.Unmatched : ReceiptMatchState.Matched,
            });
        }
        await db.SaveChangesAsync();
    }

    [Test]
    public async Task Chips_search_paging_and_the_assign_dialog()
    {
        await GoToAsync("/receipts");
        var rows = Page.GetByTestId("receipt-row");
        await Expect(rows).ToHaveCountAsync(50);
        await Expect(Page.GetByTestId("receipts-shown")).ToHaveTextAsync("50 of 60");
        await Page.GetByTestId("receipts-more").ClickAsync();
        await Expect(rows).ToHaveCountAsync(60);

        var review = Page.Locator("[data-testid=receipts-chip][data-group=review]");
        await Expect(review).ToContainTextAsync("3");
        await review.ClickAsync();
        await Expect(rows).ToHaveCountAsync(3);

        await Page.Locator("[data-testid=receipts-chip][data-group=all]").ClickAsync();
        await Page.GetByTestId("receipts-search").FillAsync("Teststadt 012");
        await Expect(rows).ToHaveCountAsync(1);
        await Page.GetByTestId("receipts-search").FillAsync("");

        await review.ClickAsync();
        await rows.First.GetByTestId("receipt-assign").ClickAsync();
        await Expect(Page.GetByTestId("receipt-assign-summary")).ToContainTextAsync("Lidl Teststadt 000");
        await Expect(Page.GetByTestId("receipt-dismiss")).ToBeVisibleAsync();
    }

    [Test]
    public async Task The_settings_tab_holds_the_item_categories_and_memory()
    {
        await GoToAsync("/receipts");
        await Expect(Page.GetByTestId("receipts-categories")).ToHaveCountAsync(0);
        await Page.GetByTestId("receipts-tab-settings").ClickAsync();
        await Expect(Page.GetByTestId("receipts-categories")).ToContainTextAsync("Split receipt items into");
        await Expect(Page.GetByTestId("source-categories-explain").First).ToContainTextAsync("stay in the booking's category");
        await Expect(Page.GetByTestId("receipts-memory")).ToBeVisibleAsync();
    }

    [Test]
    public async Task No_horizontal_scroll_at_phone_width()
    {
        await Page.SetViewportSizeAsync(375, 740);
        await GoToAsync("/receipts");
        await Expect(Page.GetByTestId("receipt-row")).ToHaveCountAsync(50);
        Assert.That(await Page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= window.innerWidth"), Is.True);
    }
}
