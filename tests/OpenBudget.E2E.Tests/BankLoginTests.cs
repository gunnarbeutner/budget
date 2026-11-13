using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using NUnit.Framework;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.E2E.Tests;

/// <summary>
/// The bank-connector flow against the fake bank: add a login, answer its code challenge, link a bank
/// account to an existing (MoneyMoney-style) account, sync, switch it with a preview, and find the transactions in Browse.
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
            OwnerUserId = E2ESeed.OwnerUserId,
            Name = "E2E Giro",
            Iban = "DE00 0000 0000 0000 0000 01",
            Currency = "EUR",
            Type = 1,
        });
        // MoneyMoney's history: August's rent lies before the compared window, October's is the bank's rent booking
        // worded differently.
        foreach (var (id, day, purpose) in new[] { (900001L, new DateTime(2026, 8, 3), "MIETE AUGUST"), (900002L, new DateTime(2026, 10, 3), "MIETE OKTOBER") })
        {
            db.Transactions.Add(new Transaction
            {
                Id = id,
                OwnerUserId = E2ESeed.OwnerUserId,
                AccountId = GiroId,
                Amount = -80000,
                Currency = "EUR",
                Timestamp = new DateTimeOffset(DateTime.SpecifyKind(day, DateTimeKind.Utc)).ToUnixTimeSeconds(),
                Name = "VERMIETER",
                Purpose = purpose,
                Note = id == 900002L ? "Rent note from MoneyMoney" : null,
            });
        }
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
    public async Task Add_login_answer_challenge_link_sync_switch_and_browse()
    {
        await SeedGiroAsync();
        await GoToAsync("/banklogins");
        await Page.GetByTestId("add-login-open").ClickAsync();

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

        // The linked giro is still MoneyMoney's until it is switched.
        await Expect(giro.GetByTestId("awaiting-switch")).ToBeVisibleAsync();

        // Sync now: the bank asks again; answering completes the sync, fills the new savings account and fetches the
        // giro's bookings for the switch preview.
        await card.Locator("[data-action=sync-now]").ClickAsync();
        await AnswerCodeChallengeAsync();
        await Expect(card.Locator("[data-bank-account='FAKE-SAVINGS']")).ToContainTextAsync("Last sync");
        await Expect(card.GetByTestId("login-error")).ToHaveCountAsync(0);

        // Switch the giro: the preview shows the cutover and how the rows are matched.
        await giro.Locator("[data-action=switch]").ClickAsync();
        var dialog = Page.GetByTestId("switch-dialog");
        await Expect(dialog).ToBeVisibleAsync();
        var row = dialog.Locator($"[data-switch-account='{GiroId}']");
        await Expect(row).ToContainTextAsync("2026-10-03");
        await Expect(row.Locator("[data-col=history]")).ToHaveTextAsync("1");
        await Expect(row.Locator("[data-col=matched]")).ToHaveTextAsync("1");
        await Expect(row.Locator("[data-col=new]")).ToHaveTextAsync("7");
        await Expect(row.Locator("[data-col=unmatched]")).ToHaveTextAsync("0");
        await Page.GetByTestId("switch-confirm").ClickAsync();
        await Expect(dialog).ToHaveCountAsync(0);
        await Expect(giro.GetByTestId("switched")).ToContainTextAsync("Switched from MoneyMoney");

        var factory = Factory.Services.GetRequiredService<SystemDbContextFactory>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            var rent = await db.Transactions.AsNoTracking().SingleAsync(t => t.Id == 900002L);
            Assert.That(rent.Name, Is.EqualTo("Example Landlord"));
            Assert.That(rent.Note, Is.EqualTo("Rent note from MoneyMoney"));
            Assert.That(await db.Transactions.CountAsync(t => t.AccountId == GiroId), Is.EqualTo(9));
        }

        // The connector's transactions show up in the project's transaction browser.
        await GoToAsync($"/projects/{E2ESeed.ProjectId}/browse?year=2026&month=10");
        // Connector rows are not categorized yet (that is a later phase), so they sit under "Uncategorized".
        await Page.Locator(".browse-tree-row").Filter(new() { HasText = "Uncategorized" }).First.ClickAsync();
        await Expect(Page.GetByText("Example Employer").First).ToBeVisibleAsync();
        await Expect(Page.GetByText("Example Landlord").First).ToBeVisibleAsync();
    }

    [Test]
    public async Task Bank_logins_are_private_to_their_owner()
    {
        await GoToAsync("/banklogins");
        await Page.GetByTestId("add-login-open").ClickAsync();
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
