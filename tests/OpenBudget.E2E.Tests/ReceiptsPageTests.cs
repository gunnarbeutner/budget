using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using NUnit.Framework;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.E2E.Tests;

/// <summary>
/// The receipts page with many made-up receipts: status chips with counts over all rows, search, the virtualized list,
/// the "Done" chip (nothing to pay, instalment plans), the assign dialog, the settings tab, and no horizontal scroll at
/// phone width.
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
        // Paid entirely with a gift card, and the full price of an order whose two instalments are both charged.
        db.ReceiptInvoices.Add(new ReceiptInvoice
        {
            OwnerUserId = E2ESeed.OwnerUserId,
            Shop = ReceiptShops.Amazon,
            InvoiceNumber = "E2E-AMZ-GIFT",
            SourceKey = "E2E-AMZ-GIFT",
            OrderNumber = "302-0000000-0000100",
            OrderDate = day.AddDays(-70),
            TotalCents = 1500,
            PaymentCents = 0,
            ReadAt = DateTime.UtcNow,
        });
        db.ReceiptInvoices.Add(Instalment("E2E-AMZ-FULL", 9000, ReceiptMatchState.Instalments, day.AddDays(-71)));
        db.ReceiptInvoices.Add(Instalment("E2E-AMZ-I1", 4500, ReceiptMatchState.Matched, day.AddDays(-71)));
        db.ReceiptInvoices.Add(Instalment("E2E-AMZ-I2", 4500, ReceiptMatchState.Matched, day.AddDays(-72)));
        // A plan of three instalments with two charged, and a cancelled one: one instalment charged, then returned (its
        // refund and the plan's reversal of the full price).
        var now = DateTime.UtcNow.Date;
        db.ReceiptInvoices.Add(Instalment("E2E-AMZ-RUN", 27900, ReceiptMatchState.Instalments, now.AddDays(-35), RunningOrder));
        db.ReceiptInvoices.Add(Instalment("E2E-AMZ-RUN1", 9300, ReceiptMatchState.Matched, now.AddDays(-35), RunningOrder));
        db.ReceiptInvoices.Add(Instalment("E2E-AMZ-RUN2", 9300, ReceiptMatchState.Matched, now.AddDays(-5), RunningOrder));
        db.ReceiptInvoices.Add(Instalment("E2E-AMZ-CAN", 6246, ReceiptMatchState.Instalments, now.AddDays(-30), CancelledOrder));
        db.ReceiptInvoices.Add(Instalment("E2E-AMZ-CAN1", 2082, ReceiptMatchState.Matched, now.AddDays(-30), CancelledOrder));
        db.ReceiptInvoices.Add(Instalment("E2E-AMZ-CANR", -2082, ReceiptMatchState.Matched, now.AddDays(-20), CancelledOrder));
        db.ReceiptInvoices.Add(Instalment("E2E-AMZ-CANF", -6246, ReceiptMatchState.Unmatched, now.AddDays(-20), CancelledOrder));
        await db.SaveChangesAsync();
    }

    private const string RunningOrder = "302-0000000-0000210";
    private const string CancelledOrder = "302-0000000-0000220";

    private static ReceiptInvoice Instalment(string key, long cents, ReceiptMatchState state, DateTime day, string order = "302-0000000-0000200") => new()
    {
        OwnerUserId = E2ESeed.OwnerUserId,
        Shop = ReceiptShops.Amazon,
        InvoiceNumber = key,
        SourceKey = key,
        OrderNumber = order,
        OrderDate = day,
        DeliveryDate = day,
        TotalCents = Math.Abs(cents),
        PaymentCents = cents,
        ReadAt = DateTime.UtcNow,
        State = state,
    };

    /// <summary>Scrolls the receipt list's scroll container to its end until the row with the text renders.</summary>
    private async Task ScrollListToAsync(IPage page, string text)
    {
        var row = page.GetByTestId("receipt-row").Filter(new() { HasText = text });
        for (var i = 0; i < 30 && await row.CountAsync() == 0; i++)
        {
            await page.EvaluateAsync(@"() => {
                let e = document.querySelector('[data-testid=receipts-table]');
                while (e && !(e.scrollHeight > e.clientHeight && /auto|scroll/.test(getComputedStyle(e).overflowY))) e = e.parentElement;
                (e || document.scrollingElement).scrollTop = 1e9;
            }");
            await page.WaitForTimeoutAsync(100);
        }
        await Expect(row.First).ToBeVisibleAsync();
    }

    [Test]
    public async Task Chips_search_the_virtualized_list_and_the_assign_dialog()
    {
        await GoToAsync("/receipts");
        var rows = Page.GetByTestId("receipt-row");
        await Expect(Page.GetByTestId("receipts-shown")).ToHaveTextAsync("71 receipts");
        await Expect(rows.First).ToBeVisibleAsync();
        Assert.That(await rows.CountAsync(), Is.LessThan(71), "only the rows near the viewport render");
        await ScrollListToAsync(Page, "Teststadt 059");

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
    public async Task Nothing_to_pay_and_a_completed_instalment_plan_are_done()
    {
        await GoToAsync("/receipts");
        await Expect(Page.Locator("[data-testid=receipts-chip][data-group=nobooking]")).ToHaveCountAsync(0);
        var done = Page.Locator("[data-testid=receipts-chip][data-group=done]");
        await Expect(done).ToContainTextAsync("Done");
        await Expect(done.Locator(".receipts-chip-count")).ToHaveTextAsync("67");
        await done.ClickAsync();
        var cancelled = Page.GetByTestId("receipt-row").Filter(new() { HasText = CancelledOrder });
        await Expect(cancelled.GetByTestId("receipt-status").Filter(new() { HasText = "Instalments cancelled" })).ToHaveCountAsync(2);
        await ScrollListToAsync(Page, "302-0000000-0000100");
        await ScrollListToAsync(Page, "302-0000000-0000200");
        var complete = Page.GetByTestId("receipt-row").Filter(new() { HasText = "302-0000000-0000200" }).Filter(new() { HasText = "90,00" });
        await Expect(complete.GetByTestId("receipt-status")).ToHaveTextAsync("Paid in 2 instalments");
    }

    [Test]
    public async Task A_running_instalment_plan_shows_its_progress()
    {
        await GoToAsync("/receipts");
        await Page.Locator("[data-testid=receipts-chip][data-group=waiting]").ClickAsync();
        var row = Page.GetByTestId("receipt-row").Filter(new() { HasText = RunningOrder });
        await Expect(row.GetByTestId("receipt-status")).ToHaveTextAsync("Instalments 2 of 3");
        var next = DateTime.UtcNow.Date.AddDays(25).ToString("dd.MM.", System.Globalization.CultureInfo.InvariantCulture);
        await Expect(row.GetByTestId("receipt-instalments")).ToHaveTextAsync($"186,00 € paid · next ~{next}");

        await row.GetByTestId("receipt-open").ClickAsync();
        await Expect(Page.GetByTestId("receipt-panel-plan-charge")).ToHaveCountAsync(2);
        await Expect(Page.GetByTestId("receipt-panel-plan-due")).ToContainTextAsync("Instalment 3 of 3");
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
        await Expect(Page.GetByTestId("receipt-row").First).ToBeVisibleAsync();
        Assert.That(await Page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= window.innerWidth"), Is.True);
    }
}
