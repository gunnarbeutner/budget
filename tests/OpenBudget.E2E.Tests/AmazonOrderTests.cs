using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using NUnit.Framework;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.E2E.Tests;

/// <summary>
/// An Amazon login with a read order (seeded, made up): map its card to an existing account, then find the order's
/// items in the matching booking's details.
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
        var order = new AmazonOrder { OwnerUserId = E2ESeed.OwnerUserId, BankLoginId = login.Id, OrderId = OrderId, TotalCents = 1250, DetailsFetchedAt = DateTime.UtcNow };
        order.Items.Add(new AmazonOrderItem { OwnerUserId = E2ESeed.OwnerUserId, Title = "USB-C charger", Quantity = 1, PriceCents = 1250 });
        db.AmazonOrders.Add(order);
        db.AmazonCharges.Add(new AmazonCharge
        {
            OwnerUserId = E2ESeed.OwnerUserId,
            BankLoginId = login.Id,
            ChargeKey = "k1",
            OrderId = OrderId,
            Date = new DateTime(2026, 3, 13, 0, 0, 0, DateTimeKind.Utc),
            AmountCents = -1250,
            InstrumentId = "amazon-visa-1111",
            Instrument = "Amazon Visa •••• 1111",
        });
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
        var order = Page.GetByTestId("tx-details-order");
        await Expect(order).ToContainTextAsync("USB-C charger");
        await Expect(order.GetByTestId("tx-details-order-link")).ToHaveTextAsync(OrderId);
        await Expect(order.GetByTestId("tx-details-order-link")).ToHaveAttributeAsync("href", new Regex(Regex.Escape("orderID=" + OrderId)));

        // The items are searchable.
        await GoToAsync($"/projects/{E2ESeed.ProjectId}/browse?year=2026&month=3&q=charger");
        await Expect(Page.GetByTestId("tx-row")).ToHaveCountAsync(1);
    }
}
