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
        db.AccountGroups.Add(new AccountGroup { Id = 9601, OwnerUserId = E2ESeed.OwnerUserId, Name = "Example Cards", Sort = 1 });
        db.Accounts.Add(new Account { Id = 9602, OwnerUserId = E2ESeed.OwnerUserId, Name = "Example Card Account", Type = 1, Currency = "EUR", GroupId = 9601 });
        db.Accounts.Add(new Account { Id = 9603, OwnerUserId = E2ESeed.OwnerUserId, Name = "Old Example Card", Type = 1, Currency = "EUR", Closed = true, GroupId = 9601 });
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

        await PickAsync(card, EditingSeed.BankAccountName);
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

    [Test]
    public async Task The_picker_groups_accounts_and_offers_closed_ones_last()
    {
        await GoToAsync("/connections");
        var row = Page.Locator("[data-bank-login='My Amazon'] [data-instrument='Amazon Visa •••• 1111']");
        await row.GetByTestId("account-picker-toggle").ClickAsync();
        var picker = row.GetByTestId("account-picker");
        var rows = picker.Locator(".tree-picker-row");
        await Expect(picker.GetByTestId("account-picker-search")).ToBeFocusedAsync();
        await Expect(rows.Filter(new() { HasText = "Example Cards" })).ToBeDisabledAsync();
        await Expect(rows.Last).ToHaveTextAsync("Old Example Card");
        await Expect(rows.Nth(await rows.CountAsync() - 2)).ToHaveTextAsync("Closed accounts");

        await picker.GetByTestId("account-picker-search").FillAsync("old ex");
        await Expect(picker.Locator(".tree-picker-row:not(.header)")).ToHaveCountAsync(1);
        await rows.Filter(new() { HasText = "Old Example Card" }).ClickAsync();
        await Expect(row.GetByTestId("account-picker-toggle")).ToHaveTextAsync("Old Example Card (closed)");

        await Page.ReloadAsync();
        await WaitForInteractiveAsync();
        await Expect(row.GetByTestId("account-picker-toggle")).ToHaveTextAsync("Old Example Card (closed)");
        await row.GetByTestId("account-picker-toggle").ClickAsync();
        await row.GetByTestId("account-picker").Locator(".tree-picker-row", new() { HasText = "Not mapped" }).ClickAsync();
        await Expect(row.GetByTestId("account-picker-toggle")).ToHaveTextAsync("Not mapped");
    }

    [Test]
    public async Task An_incomplete_payment_history_shows_a_warning_instead_of_ok()
    {
        var factory = Factory.Services.GetRequiredService<SystemDbContextFactory>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            var login = db.BankLogins.Single(l => l.DisplayName == "My Amazon");
            login.LastSyncAt = DateTime.UtcNow;
            login.OrderHistoryReadBackTo = new DateOnly(2026, 8, 24);
            await db.SaveChangesAsync();
        }

        await GoToAsync("/connections");
        var card = Page.Locator("[data-bank-login='My Amazon']");
        await Expect(card.GetByTestId("login-pill")).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("bank-pill-warn"));
        await Expect(card.GetByTestId("login-history-incomplete")).ToContainTextAsync("24 Aug 2026");

        await using (var db = await factory.CreateDbContextAsync())
        {
            var login = db.BankLogins.Single(l => l.DisplayName == "My Amazon");
            login.OrderHistoryCompleteAt = DateTime.UtcNow;
            login.OrderHistoryReadBackTo = null;
            await db.SaveChangesAsync();
        }
        await Page.ReloadAsync();
        await WaitForInteractiveAsync();
        await Expect(card.GetByTestId("login-pill")).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("bank-pill-ok"));
        await Expect(card.GetByTestId("login-history-incomplete")).ToHaveCountAsync(0);
    }

    private static async Task PickAsync(ILocator card, string account)
    {
        var map = card.Locator("[data-instrument='Amazon Visa •••• 1111']").GetByTestId("instrument-map");
        await map.GetByTestId("account-picker-toggle").ClickAsync();
        await map.GetByTestId("account-picker-search").FillAsync(account);
        await map.GetByTestId("account-picker").Locator(".tree-picker-row:not(.header)", new() { HasText = account }).First.ClickAsync();
    }
}
