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
        db.ReceiptSourceCategories.Add(new ReceiptSourceCategory { OwnerUserId = E2ESeed.OwnerUserId, Source = ReceiptSources.Mail, CategoryId = Energy, Description = "Drogerie, Haushalt" });
        await db.SaveChangesAsync();
        var receipt = ReweInvoice.Parse(SyntheticInvoice.Pdf(SyntheticInvoice.StandardLines(), bonus: -1.00m, orderDate: "02.03.2026", delivery: "03.03.2026"));
        await ReceiptStore.AddAsync(db, E2ESeed.OwnerUserId, ReceiptShops.Rewe, receipt, "mail-1", DateTime.UtcNow, default);
        await Factory.Services.GetRequiredService<ReceiptStore>().MatchAsync(db, E2ESeed.OwnerUserId, default);
    }

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
        var schorle = panel.GetByTestId("receipt-panel-line").Filter(new() { HasText = "Apfelschorle" });
        await Expect(schorle).ToHaveCountAsync(1);
        await schorle.GetByTestId("receipt-panel-category").ClickAsync();
        await Page.GetByTestId("category-picker").GetByTestId("category-picker-option").Filter(new() { HasText = EditingSeed.EnergyCategory }).First.ClickAsync();
        await Expect(panel.GetByTestId("receipt-panel-line").Filter(new() { HasText = "Apfelschorle" }).GetByTestId("receipt-panel-category"))
            .ToContainTextAsync(EditingSeed.EnergyCategory);
        await Page.Keyboard.PressAsync("Escape");
        await Expect(panel).ToHaveCountAsync(0);

        // The parts are written anew: the list shows the new amounts and the details the new part.
        await Expect(rows.Filter(new() { HasText = "4,12" })).ToHaveCountAsync(0);
        if (await Page.GetByTestId("tx-receipt").CountAsync() == 0) await rows.First.ClickAsync();
        await Expect(Page.GetByTestId("tx-receipt").Locator($"[data-category='{Energy}']")).ToContainTextAsync("(3)");
    }
}
