using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using NUnit.Framework;
using OpenBudget.Connectors.Amazon;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.E2E.Tests;

/// <summary>
/// An Amazon login with a read order (seeded, made up): map its card to an existing account, then find the order's
/// receipt in the matching booking's details.
/// </summary>
[TestFixture]
public sealed class AmazonOrderTests : EndToEndTestBase
{
    private const string OrderId = "301-1234567-7654321";

    [SetUp]
    public async Task SeedAsync()
    {
        var factory = Factory.Services.GetRequiredService<SystemDbContextFactory>();
        await EditingSeed.SeedAsync(factory);
        await using var db = await factory.CreateDbContextAsync();
        var login = new BankLogin { OwnerUserId = E2ESeed.OwnerUserId, ConnectorType = "amazon", DisplayName = "My Amazon", RefreshInterval = RefreshInterval.ManualOnly };
        db.BankLogins.Add(login);
        await db.SaveChangesAsync();
        db.BankLoginAccounts.Add(new BankLoginAccount { BankLoginId = login.Id, RemoteId = "amazon-visa-1111", Name = "Amazon Visa •••• 1111", Type = Connectors.RemoteAccountType.CreditCard });
        db.BankLoginAccounts.Add(new BankLoginAccount { BankLoginId = login.Id, RemoteId = "example-points", Name = "Example Points", Type = Connectors.RemoteAccountType.Other });
        var receipt = AmazonReceiptStore.NewReceipt(E2ESeed.OwnerUserId, login.Id, "k1", OrderId, new DateOnly(2026, 3, 13), -1250, "amazon-visa-1111", "Amazon Visa •••• 1111", DateTime.UtcNow);
        AmazonReceiptStore.Assign([receipt], [new AmazonItem(0, "USB-C charger", 1, 1250, 0)], new DateOnly(2026, 3, 12), 1250, DateTime.UtcNow);
        db.ReceiptInvoices.Add(receipt);
        await db.SaveChangesAsync();
    }

    [Test]
    public async Task Mapping_the_card_puts_the_order_items_on_the_booking()
    {
        await GoToAsync("/connections");
        var card = Page.Locator("[data-bank-login='My Amazon']");
        await Expect(card.GetByTestId("open-charges")).ToContainTextAsync("1");
        await Expect(card.GetByTestId("instrument-points")).ToBeVisibleAsync();

        await card.Locator("[data-instrument='Amazon Visa •••• 1111']").GetByTestId("instrument-map").SelectOptionAsync(new SelectOptionValue { Label = EditingSeed.BankAccountName });
        await Expect(card.Locator("[data-instrument='Amazon Visa •••• 1111']")).ToContainTextAsync("1 of 1");
        await Expect(card.GetByTestId("open-charges")).ToHaveCountAsync(0);

        await GoToAsync($"/projects/{E2ESeed.ProjectId}/browse?year=2026&month=3&sel=uncategorized");
        await Page.GetByTestId("tx-row").Filter(new() { HasText = "−12,50" }).ClickAsync();
        var receipt = Page.GetByTestId("tx-receipt");
        await Expect(receipt).ToContainTextAsync("Amazon " + OrderId);
        await receipt.GetByTestId("tx-receipt-open").ClickAsync();
        await Expect(Page.GetByTestId("receipt-panel")).ToContainTextAsync("USB-C charger");
        await Page.GetByTestId("modal-dialog").Locator(".btn-close").ClickAsync();

        // The items are searchable.
        await GoToAsync($"/projects/{E2ESeed.ProjectId}/browse?year=2026&month=3&q=charger");
        await Expect(Page.GetByTestId("tx-row")).ToHaveCountAsync(1);
    }
}
