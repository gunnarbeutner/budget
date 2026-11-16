using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using OpenBudget.Connectors.Receipts;
using OpenBudget.Models;
using OpenBudget.Services;
using OpenBudget.Tests.Receipts;

namespace OpenBudget.E2E.Tests;

/// <summary>
/// A REWE direct debit with a made-up receipt: the booking is split into food and non-food, its details show the
/// receipt's parts, the receipt panel lists the items, and moving an item to another category there splits it again.
/// </summary>
[TestFixture]
public sealed class ReceiptDetailsTests : EndToEndTestBase
{
    private const long Debit = 130;
    private const long Groceries = 200;
    private const long Energy = 202;

    [SetUp]
    public async Task SeedAsync()
    {
        var factory = Factory.Services.GetRequiredService<SystemDbContextFactory>();
        await EditingSeed.SeedAsync(factory);
        await using var db = await factory.CreateDbContextAsync();
        EditingSeed.Add(db, Debit, EditingSeed.BankAccountId, -14_28, "REWE Markt GmbH", new DateTime(2026, 3, 6, 0, 0, 0, DateTimeKind.Utc),
            purpose: "48466003260302300881", category: Groceries);
        db.ProjectReceiptCategories.Add(new ProjectReceiptCategory { OwnerUserId = E2ESeed.OwnerUserId, ProjectId = E2ESeed.ProjectId, CategoryId = Energy, Description = "Drogerie, Haushalt" });
        await db.SaveChangesAsync();
        var receipt = ReweInvoice.Parse(SyntheticInvoice.Pdf(SyntheticInvoice.StandardLines(), bonus: -1.00m, orderDate: "02.03.2026", delivery: "03.03.2026"));
        await ReceiptStore.AddAsync(db, E2ESeed.OwnerUserId, ReceiptShops.Rewe, receipt, "mail-1", DateTime.UtcNow, default);
        await Factory.Services.GetRequiredService<ReceiptStore>().MatchAsync(db, E2ESeed.OwnerUserId, default);
    }

    /// <summary>
    /// A split part's receipt opens on that part: the footer sums the part's items, its share of the bonus and the part's
    /// amount; "All" shows the whole receipt's total, bonus and payment again.
    /// </summary>
    [Test]
    public async Task A_parts_receipt_opens_on_the_part_with_its_own_totals()
    {
        await GoToAsync($"/projects/{E2ESeed.ProjectId}/browse?year=2026&month=3&q=REWE");
        var rows = Page.GetByTestId("tx-row");
        await Expect(rows).ToHaveCountAsync(2);
        await rows.First.ClickAsync();
        var receipt = Page.GetByTestId("tx-receipt");
        await Expect(receipt.Locator(".cat-pill-current")).ToHaveCountAsync(1);
        await receipt.GetByTestId("tx-receipt-open").ClickAsync();

        var panel = Page.GetByTestId("receipt-panel");
        await Expect(panel.Locator("[data-testid=receipt-panel-part].btn-secondary")).ToHaveCountAsync(1);
        await Expect(panel.GetByTestId("receipt-panel-paid")).ToHaveCountAsync(0);
        var amount = await panel.GetByTestId("receipt-panel-part-amount").InnerTextAsync();
        var items = await panel.GetByTestId("receipt-panel-part-items").InnerTextAsync();
        var share = panel.GetByTestId("receipt-panel-part-share");
        // The part's amount is its items plus its share of the bonus, when it has one.
        var shareCents = await share.CountAsync() == 1 ? Cents(await share.InnerTextAsync()) : 0;
        Assert.That(Cents(amount), Is.EqualTo(Cents(items) + shareCents));
        if (Environment.GetEnvironmentVariable("OB_RECEIPT_SCREENSHOT_DIR") is { Length: > 0 } dir)
        {
            await Page.ScreenshotAsync(new() { Path = Path.Combine(dir, "panel-part-1280.png") });
            await Page.SetViewportSizeAsync(390, 844);
            await Page.ScreenshotAsync(new() { Path = Path.Combine(dir, "panel-part-390.png") });
            await Page.GetByTestId("modal-dialog").Locator(".btn-close").ClickAsync();
            await Page.ScreenshotAsync(new() { Path = Path.Combine(dir, "row-expanded-390.png") });
            await Page.SetViewportSizeAsync(1280, 900);
            await Page.ScreenshotAsync(new() { Path = Path.Combine(dir, "row-expanded-1280.png") });
            return;
        }

        await panel.Locator(".btn", new() { HasTextRegex = new System.Text.RegularExpressions.Regex("^All$") }).ClickAsync();
        await Expect(panel.GetByTestId("receipt-panel-paid")).ToHaveCountAsync(1);
        await Expect(panel.GetByTestId("receipt-panel-part-amount")).ToHaveCountAsync(0);
    }

    private static long Cents(string text) =>
        long.Parse(new string(text.Where(char.IsDigit).ToArray()), System.Globalization.CultureInfo.InvariantCulture) * (text.Contains('−') || text.Contains('-') ? -1 : 1);

    [Test]
    public async Task The_details_show_the_receipt_by_part_and_moving_an_item_resplits()
    {
        await GoToAsync($"/projects/{E2ESeed.ProjectId}/browse?year=2026&month=3&q=REWE");
        var rows = Page.GetByTestId("tx-row");
        await Expect(rows).ToHaveCountAsync(2);
        await rows.First.ClickAsync();

        var receipt = Page.GetByTestId("tx-receipt");
        await Expect(receipt).ToContainTextAsync("B-AAA-BBB-CC1");
        await Expect(receipt.Locator($"[data-category='{Energy}']")).ToContainTextAsync("(2)");

        // The receipt panel lists the items; moving one re-splits the booking and the panel shows the result.
        await receipt.GetByTestId("tx-receipt-open").ClickAsync();
        var panel = Page.GetByTestId("receipt-panel");
        await panel.Locator(".btn", new() { HasTextRegex = new System.Text.RegularExpressions.Regex("^All$") }).ClickAsync();
        var schorle = panel.GetByTestId("receipt-panel-line").Filter(new() { HasText = "Apfelschorle" });
        await Expect(schorle).ToHaveCountAsync(1);
        await schorle.GetByTestId("receipt-panel-category").ClickAsync();
        await Page.GetByTestId("category-picker").GetByTestId("category-picker-option").Filter(new() { HasText = EditingSeed.EnergyCategory }).First.ClickAsync();
        await Expect(panel.GetByTestId("receipt-panel-line").Filter(new() { HasText = "Apfelschorle" }).GetByTestId("receipt-panel-category"))
            .ToContainTextAsync(EditingSeed.EnergyCategory);
        await Page.GetByTestId("modal-dialog").Locator(".btn-close").ClickAsync();
        await Expect(panel).ToHaveCountAsync(0);

        // The parts are written anew: the list shows the new amounts and the details the new part.
        await Expect(rows.Filter(new() { HasText = "4,12" })).ToHaveCountAsync(0);
        if (await Page.GetByTestId("tx-receipt").CountAsync() == 0) await rows.First.ClickAsync();
        await Expect(Page.GetByTestId("tx-receipt").Locator($"[data-category='{Energy}']")).ToContainTextAsync("(3)");
    }
}
