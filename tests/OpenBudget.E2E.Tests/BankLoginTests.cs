using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using NUnit.Framework;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.E2E.Tests;

/// <summary>
/// The bank-connector flow against the fake bank: add a login, answer its code challenge, link a bank
/// account to an existing (MoneyMoney-style) account, sync, and find the transactions in Browse.
/// </summary>
[TestFixture]
public sealed class BankLoginTests : EndToEndTestBase
{
    private const long GiroId = 1;

    private async Task SeedGiroAsync()
    {
        var factory = Factory.Services.GetRequiredService<SystemDbContextFactory>();
        await using var db = await factory.CreateDbContextAsync();
        db.Accounts.Add(new Account
        {
            Id = GiroId,
            SourceId = E2ESeed.SourceId,
            Name = "E2E Giro",
            Iban = "DE00 0000 0000 0000 0000 01",
            Currency = "EUR",
            Type = 1,
        });
        await db.SaveChangesAsync();
    }

    private async Task AnswerCodeChallengeAsync()
    {
        var challenge = Page.GetByTestId("bank-challenge");
        await Expect(challenge).ToBeVisibleAsync();
        await Expect(challenge).ToContainTextAsync("123456");
        // The code field sits in a form (so Safari can offer codes from Messages) and Enter submits it.
        var input = Page.Locator("form#bank-challenge-form #bank-challenge-input");
        await Expect(input).ToHaveAttributeAsync("autocomplete", "one-time-code");
        await input.FillAsync("123456");
        await input.PressAsync("Enter");
        await Expect(Page.GetByTestId("bank-challenge")).ToHaveCountAsync(0);
    }

    [Test]
    public async Task Add_login_answer_challenge_link_sync_and_browse()
    {
        await SeedGiroAsync();
        await GoToAsync("/banklogins");

        // The real banks are offered too; pick the fake one explicitly.
        await Page.Locator("#new-login-connector").SelectOptionAsync("fake");
        await Page.Locator("#new-login-name").FillAsync("My fake bank");
        await Page.Locator("#new-login-username").FillAsync("demo+code");
        await Page.Locator("#new-login-pin").FillAsync("1234");
        await Page.Locator("#new-login-add").ClickAsync();

        // The first sync starts at once; its code challenge appears as a dialog.
        await AnswerCodeChallengeAsync();

        var card = Page.Locator("[data-bank-login='My fake bank']");
        var giro = card.Locator("[data-bank-account='FAKE-GIRO']");
        await Expect(giro).ToBeVisibleAsync();

        // The giro account is suggested by IBAN; link it. Create a fresh local account for the savings one.
        await giro.Locator("[data-action=link]").ClickAsync();
        await Expect(giro.GetByTestId("linked-to")).ToContainTextAsync("E2E Giro");
        await card.Locator("[data-bank-account='FAKE-SAVINGS'] [data-action=create]").ClickAsync();
        await Expect(card.Locator("[data-bank-account='FAKE-SAVINGS']").GetByTestId("linked-to")).ToContainTextAsync("Example Savings");

        // Sync now: the bank asks again; answering completes the sync and fills the accounts.
        await card.Locator("[data-action=sync-now]").ClickAsync();
        await AnswerCodeChallengeAsync();
        await Expect(giro).ToContainTextAsync("Last sync");
        await Expect(card.GetByTestId("login-error")).ToHaveCountAsync(0);

        // The connector's transactions show up in the project's transaction browser.
        await GoToAsync($"/projects/{E2ESeed.ProjectId}/browse?year=2026&month=10");
        // Connector rows are not categorized yet (that is a later phase), so they sit under "Uncategorized".
        await Page.Locator(".browse-tree-row").Filter(new() { HasText = "Uncategorized" }).First.ClickAsync();
        await Expect(Page.GetByText("Example Employer").First).ToBeVisibleAsync();
        await Expect(Page.GetByText("Example Landlord").First).ToBeVisibleAsync();
        await Expect(Page.GetByText("41 transactions")).ToBeVisibleAsync();
    }

    [Test]
    public async Task Bank_logins_are_private_to_their_owner()
    {
        await GoToAsync("/banklogins");
        await Page.Locator("#new-login-username").FillAsync("demo");
        await Page.Locator("#new-login-pin").FillAsync("1234");
        // The real banks are offered too; pick the fake one explicitly.
        await Page.Locator("#new-login-connector").SelectOptionAsync("fake");
        await Page.Locator("#new-login-name").FillAsync("Private login");
        await Page.Locator("#new-login-add").ClickAsync();
        await Expect(Page.Locator("[data-bank-login='Private login']")).ToBeVisibleAsync();

        await LogoutAsync();
        await LoginAsync(E2ESeed.SecondUserName);
        await GoToAsync("/banklogins");
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Bank logins" })).ToBeVisibleAsync();
        await Expect(Page.GetByText("No bank logins yet.")).ToBeVisibleAsync();
        await Expect(Page.Locator("[data-bank-login]")).ToHaveCountAsync(0);
    }
}
