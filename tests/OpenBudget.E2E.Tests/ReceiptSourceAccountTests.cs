using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.E2E.Tests;

/// <summary>
/// A receipt source's payment method on its Connections card: mapping Lidl purchases to an account puts the source's
/// open receipts into that account's project.
/// </summary>
[TestFixture]
public sealed class ReceiptSourceAccountTests : EndToEndTestBase
{
    private SystemDbContextFactory Db => Factory.Services.GetRequiredService<SystemDbContextFactory>();

    [SetUp]
    public async Task SeedAsync()
    {
        await EditingSeed.SeedAsync(Db);
        await using var db = await Db.CreateDbContextAsync();
        db.BankLogins.Add(new BankLogin { OwnerUserId = E2ESeed.OwnerUserId, ConnectorType = "lidlplus", DisplayName = "My Lidl", RefreshInterval = RefreshInterval.ManualOnly });
        db.ReceiptInvoices.Add(new ReceiptInvoice
        {
            OwnerUserId = E2ESeed.OwnerUserId,
            Shop = ReceiptShops.Lidl,
            InvoiceNumber = "E2E-MAPPED",
            Store = "Mappedtown",
            OrderDate = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
            TotalCents = 1234,
            PaymentCents = 1234,
            ReadAt = DateTime.UtcNow,
            State = ReceiptMatchState.Unmatched,
        });
        await db.SaveChangesAsync();
    }

    [Test]
    public async Task Mapping_the_lidl_source_shows_its_open_receipt_in_the_project()
    {
        await GoToAsync($"/projects/{E2ESeed.ProjectId}/receipts");
        await Expect(Page.GetByTestId("receipts-none")).ToBeVisibleAsync();

        await GoToAsync("/connections");
        var section = Page.Locator("[data-bank-login='My Lidl'] [data-testid=source-accounts]");
        await Expect(section).ToContainTextAsync("Lidl purchases");
        var add = section.GetByTestId("source-account-add");
        await add.GetByTestId("account-picker-toggle").ClickAsync();
        await add.GetByTestId("account-picker-search").FillAsync(EditingSeed.BankAccountName);
        await add.GetByTestId("account-picker").Locator(".tree-picker-row:not(.header)", new() { HasText = EditingSeed.BankAccountName }).First.ClickAsync();
        await Expect(section.GetByTestId("source-account")).ToHaveTextAsync(EditingSeed.BankAccountName);

        await GoToAsync($"/projects/{E2ESeed.ProjectId}/receipts");
        await Expect(Page.GetByTestId("receipt-row").Filter(new() { HasText = "Mappedtown" })).ToHaveCountAsync(1);
    }
}
