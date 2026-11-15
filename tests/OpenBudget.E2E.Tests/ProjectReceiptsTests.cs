using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.E2E.Tests;

/// <summary>
/// A project's Receipts view: the sidebar link with the count of receipts to review, the list scoped to the project
/// (receipts on its bookings, open receipts of sources booking into it, not receipts elsewhere), the badge following
/// an assignment, and the phone bottom bar.
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
        await Expect(rows).ToHaveCountAsync(2);
        await Expect(link.Locator(".shell-view-badge")).ToHaveTextAsync("1 to review");
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
}
