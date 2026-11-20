using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.E2E.Tests;

/// <summary>
/// A project's Receipts view: the sidebar link with the count of receipts to review, the list scoped to the project
/// (receipts on its bookings, receipts without a booking of sources booking into it, not receipts elsewhere), the badge following
/// an assignment, the phone bottom bar and the phone list. Set OB_RECEIPTS_SCREENSHOT_DIR to keep screenshots.
/// </summary>
[TestFixture]
public sealed class ProjectReceiptsTests : EndToEndTestBase
{
    private static int Pid => E2ESeed.ProjectId;

    private SystemDbContextFactory Db => Factory.Services.GetRequiredService<SystemDbContextFactory>();

    [SetUp]
    public async Task SeedAsync()
    {
        await EditingSeed.SeedAsync(Db);
        await using var db = await Db.CreateDbContextAsync();
        var day = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        db.ReceiptInvoices.Add(Lidl("Sampletown Booked", 2500, day.AddDays(9), ReceiptMatchState.Matched, 101));
        db.ReceiptInvoices.Add(Lidl("Sampletown Open A", 1777, day.AddDays(-20), ReceiptMatchState.Unmatched, null));
        db.ReceiptInvoices.Add(Lidl("Sampletown Open B", 1888, day.AddDays(-21), ReceiptMatchState.Unmatched, null));
        // On a booking of no account of the project.
        db.ReceiptInvoices.Add(Lidl("Elsewhere Village", 4200, new DateTime(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc), ReceiptMatchState.Matched, 1));
        await db.SaveChangesAsync();
    }

    private static ReceiptInvoice Lidl(string store, long cents, DateTime day, ReceiptMatchState state, long? booking) => new()
    {
        OwnerUserId = E2ESeed.OwnerUserId,
        Shop = ReceiptShops.Lidl,
        InvoiceNumber = "E2E-" + store,
        Store = store,
        OrderDate = day,
        TotalCents = cents,
        PaymentCents = cents,
        ReadAt = DateTime.UtcNow,
        State = state,
        TransactionId = booking,
        ManualMatch = booking != null,
    };

    [Test]
    public async Task A_sync_that_stores_a_receipt_shows_it_in_the_open_view()
    {
        await GoToAsync($"/projects/{Pid}/receipts");
        var rows = Page.GetByTestId("receipt-row");
        await Expect(rows).ToHaveCountAsync(3);

        await using (var db = await Db.CreateDbContextAsync())
        {
            db.ReceiptInvoices.Add(Lidl("Sampletown Open C", 1999, new DateTime(2026, 2, 12, 0, 0, 0, DateTimeKind.Utc), ReceiptMatchState.Unmatched, null));
            await db.SaveChangesAsync();
        }
        Factory.BrowserHostServices.GetRequiredService<ChangeNotificationService>().Publish(new DataChange(DataArea.ImportedData, OwnerUserId: E2ESeed.OwnerUserId));

        await Expect(rows).ToHaveCountAsync(4);
        await Expect(Page.GetByTestId("view-receipts").Locator(".shell-view-badge")).ToHaveTextAsync("3 to review");
    }

    [Test]
    public async Task The_view_lists_the_projects_receipts_and_the_badge_follows_a_review()
    {
        await GoToAsync($"/projects/{Pid}/browse");
        var link = Page.GetByTestId("view-receipts");
        await Expect(link.Locator(".shell-view-badge")).ToHaveTextAsync("2 to review");
        await link.ClickAsync();
        await Expect(Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex($"/projects/{Pid}/receipts$"));
        await Expect(link).ToHaveAttributeAsync("aria-current", "page");

        var rows = Page.GetByTestId("receipt-row");
        await Expect(rows).ToHaveCountAsync(3);
        await Expect(rows.Filter(new() { HasText = "Sampletown Booked" })).ToHaveCountAsync(1);
        await Expect(rows.Filter(new() { HasText = "Elsewhere Village" })).ToHaveCountAsync(0);
        await Expect(Page.Locator("[data-testid=receipts-chip][data-group=review] .receipts-chip-count")).ToHaveTextAsync("2");

        await rows.Filter(new() { HasText = "Sampletown Open A" }).GetByTestId("receipt-assign").ClickAsync();
        await Page.GetByTestId("receipt-dismiss").ClickAsync();
        await Expect(link.Locator(".shell-view-badge")).ToHaveTextAsync("1 to review");
        // a dismissed receipt stays in the project, under "No booking"
        await Expect(rows).ToHaveCountAsync(3);
        var noBooking = Page.Locator("[data-testid=receipts-chip][data-group=nobooking]");
        await Expect(noBooking.Locator(".receipts-chip-count")).ToHaveTextAsync("1");
        await noBooking.ClickAsync();
        await Expect(rows).ToHaveCountAsync(1);
        await Expect(rows.Filter(new() { HasText = "Sampletown Open A" })).ToHaveCountAsync(1);
    }

    [Test]
    public async Task The_phone_bottom_bar_shows_receipts_with_the_review_count()
    {
        await Page.SetViewportSizeAsync(390, 844);
        await GoToAsync($"/projects/{Pid}/browse");
        var bar = Page.GetByTestId("bottom-bar");
        await Expect(bar.GetByTestId("bottom-receipts").GetByTestId("bottom-badge")).ToHaveTextAsync("2");
        await bar.GetByTestId("bottom-receipts").ClickAsync();
        await Expect(Page.GetByTestId("receipt-row")).ToHaveCountAsync(3);
        Assert.That(await Page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= window.innerWidth"), Is.True);
    }

    [Test]
    public async Task On_a_phone_the_receipts_are_a_compact_two_line_list()
    {
        await Page.SetViewportSizeAsync(390, 844);
        await GoToAsync($"/projects/{Pid}/receipts");
        var rows = Page.GetByTestId("receipt-row");
        await Expect(rows).ToHaveCountAsync(3);
        await Expect(Page.GetByTestId("project-receipts-toolbar")).ToBeHiddenAsync();
        await Expect(Page.GetByTestId("receipts-settings-link")).ToBeVisibleAsync();

        var booked = rows.Filter(new() { HasText = "Sampletown Booked" });
        await Expect(booked.GetByTestId("receipt-status")).ToBeHiddenAsync();
        await Expect(booked.GetByTestId("receipt-status-short")).ToHaveTextAsync(new System.Text.RegularExpressions.Regex(@"^Booked \d\d\.\d\d\.$"));

        var open = rows.Filter(new() { HasText = "Sampletown Open A" });
        await Expect(open.GetByTestId("receipt-assign")).ToBeVisibleAsync();
        // Two text lines: the status and the assign action share the second line.
        foreach (var row in new[] { booked, open })
        {
            var box = await row.BoundingBoxAsync();
            Assert.That(box!.Height, Is.LessThanOrEqualTo(56));
        }
        var chips = Page.GetByTestId("receipts-filter-state");
        Assert.That(await chips.EvaluateAsync<string>("e => getComputedStyle(e).flexWrap"), Is.EqualTo("nowrap"));
        if (Environment.GetEnvironmentVariable("OB_RECEIPTS_SCREENSHOT_DIR") is { Length: > 0 } dir)
            await Page.ScreenshotAsync(new() { Path = Path.Combine(dir, "receipts-phone.png") });
    }

    [Test]
    public async Task On_a_desktop_the_receipts_are_a_table_with_full_statuses()
    {
        await Page.SetViewportSizeAsync(1280, 900);
        await GoToAsync($"/projects/{Pid}/receipts");
        var rows = Page.GetByTestId("receipt-row");
        await Expect(rows).ToHaveCountAsync(3);
        await Expect(Page.GetByTestId("project-receipts-toolbar")).ToBeVisibleAsync();
        var open = rows.Filter(new() { HasText = "Sampletown Open A" });
        await Expect(open.GetByTestId("receipt-status")).ToBeVisibleAsync();
        await Expect(open.GetByTestId("receipt-status-short")).ToBeHiddenAsync();
        await Expect(open.GetByTestId("receipt-assign")).ToHaveTextAsync("Assign…");
        if (Environment.GetEnvironmentVariable("OB_RECEIPTS_SCREENSHOT_DIR") is { Length: > 0 } dir)
            await Page.ScreenshotAsync(new() { Path = Path.Combine(dir, "receipts-desktop.png") });
    }
}
