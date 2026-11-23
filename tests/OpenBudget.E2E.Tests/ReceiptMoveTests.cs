using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.E2E.Tests;

/// <summary>
/// Fixing a receipt's item categories from "Uncategorized": the first move splits the booking, which leaves the list,
/// and the receipt panel stays open on all items so the next one can be fixed right away.
/// </summary>
[TestFixture]
public sealed class ReceiptMoveTests : EndToEndTestBase
{
    private const long Booking = 130;

    [SetUp]
    public async Task SeedAsync()
    {
        var factory = Factory.Services.GetRequiredService<SystemDbContextFactory>();
        await EditingSeed.SeedAsync(factory);
        await using var db = await factory.CreateDbContextAsync();
        EditingSeed.Add(db, Booking, EditingSeed.BankAccountId, -42_97, "AMZN Mktp DE", new DateTime(2026, 3, 20, 0, 0, 0, DateTimeKind.Utc));
        await db.SaveChangesAsync();
        var booking = await db.Transactions.FindAsync(Booking);
        booking!.OrderItems = "Example USB stick; Example paperback";
        var receipt = new ReceiptInvoice
        {
            OwnerUserId = E2ESeed.OwnerUserId,
            Shop = ReceiptShops.Amazon,
            InvoiceNumber = "302-0000000-0000001",
            OrderNumber = "302-0000000-0000001",
            OrderDate = new DateTime(2026, 3, 18, 0, 0, 0, DateTimeKind.Utc),
            TotalCents = 42_97,
            PaymentCents = 42_97,
            ReadAt = DateTime.UtcNow,
            DetailsReadAt = DateTime.UtcNow,
            State = ReceiptMatchState.Matched,
            TransactionId = Booking,
            ManualMatch = true,
        };
        // Both items sit in the wrong category: the stick under Groceries, the book under Energy.
        receipt.Items.Add(new ReceiptItem { OwnerUserId = E2ESeed.OwnerUserId, Position = 0, Name = "Example USB stick", TotalCents = 30_00, Kind = ReceiptItemKind.Product, CategoryId = 200, CategorySource = ReceiptCategorySource.Manual });
        receipt.Items.Add(new ReceiptItem { OwnerUserId = E2ESeed.OwnerUserId, Position = 1, Name = "Example paperback", TotalCents = 12_97, Kind = ReceiptItemKind.Product, CategoryId = 202, CategorySource = ReceiptCategorySource.Manual });
        db.ReceiptInvoices.Add(receipt);
        await db.SaveChangesAsync();
    }

    [Test]
    public async Task Moving_items_from_uncategorized_keeps_the_receipt_open_on_all_items()
    {
        await GoToAsync($"/projects/{E2ESeed.ProjectId}/browse?year=2026&month=3&sel=uncategorized");
        var row = Page.GetByTestId("tx-row").Filter(new() { HasText = "AMZN Mktp DE" });
        await row.ClickAsync();
        await Page.GetByTestId("tx-receipt-group").First.ClickAsync();

        var panel = Page.GetByTestId("receipt-panel");
        var lines = panel.GetByTestId("receipt-panel-line");
        await Expect(lines).ToHaveCountAsync(2);   // all items, not just the chip's part

        await lines.Filter(new() { HasText = "Example USB stick" }).GetByTestId("receipt-panel-category").ClickAsync();
        await Page.GetByTestId("category-picker").GetByTestId("category-picker-option").Filter(new() { HasText = EditingSeed.EnergyCategory }).First.ClickAsync();

        // The booking split and left "Uncategorized"; the panel stays with both items and the new category.
        await Expect(row).ToHaveCountAsync(0);
        await Expect(panel).ToBeVisibleAsync();
        await Expect(lines).ToHaveCountAsync(2);
        await Expect(lines.Filter(new() { HasText = "Example USB stick" }).GetByTestId("receipt-panel-category")).ToHaveAttributeAsync("data-category", "202");

        await lines.Filter(new() { HasText = "Example paperback" }).GetByTestId("receipt-panel-category").ClickAsync();
        await Page.GetByTestId("category-picker").GetByTestId("category-picker-option").Filter(new() { HasText = "Groceries" }).First.ClickAsync();
        await Expect(lines.Filter(new() { HasText = "Example paperback" }).GetByTestId("receipt-panel-category")).ToHaveAttributeAsync("data-category", "200");
        await Expect(panel).ToBeVisibleAsync();
    }
}
