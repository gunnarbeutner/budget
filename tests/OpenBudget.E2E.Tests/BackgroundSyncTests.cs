using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using OpenBudget.Services;

namespace OpenBudget.E2E.Tests;

/// <summary>The background-sync switch of a bank login, its status and the badge while a login needs the owner.</summary>
[TestFixture]
public sealed class BackgroundSyncTests : EndToEndTestBase
{
    [Test]
    public async Task Switch_on_background_syncs_see_needs_you_and_clear_it_with_a_manual_sync()
    {
        await GoToAsync("/banklogins");
        await Page.GetByTestId("add-login-open").ClickAsync();
        await Page.Locator("#new-login-connector").SelectOptionAsync("fake");
        await Page.Locator("#new-login-name").FillAsync("Background bank");
        await Page.Locator("#new-login-username").FillAsync("demo+session");
        await Page.Locator("#new-login-pin").FillAsync("1234");
        await Page.Locator("#new-login-add").ClickAsync();
        var input = Page.Locator("#bank-challenge-input");
        await input.FillAsync("123456");
        await input.PressAsync("Enter");

        var card = Page.Locator("[data-bank-login='Background bank']");
        await Expect(card.GetByTestId("login-pill")).ToBeVisibleAsync();
        var toggle = card.GetByTestId("background-switch");
        await Expect(toggle).Not.ToBeCheckedAsync();
        await toggle.CheckAsync();
        await Expect(card.GetByTestId("background-status")).ToContainTextAsync("Last background sync");
        await Expect(Page.GetByTestId("banklogins-needs-you")).ToHaveCountAsync(0);

        // a background run stopped for the owner
        var factory = Factory.Services.GetRequiredService<SystemDbContextFactory>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.BankLogins.Where(l => l.DisplayName == "Background bank")
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.NeedsManualSync, "The bank wants a TAN or code.").SetProperty(l => l.NeedsManualSince, DateTime.UtcNow));
        }
        await GoToAsync("/banklogins");
        await Expect(card.GetByTestId("login-status")).ToHaveTextAsync("Needs you");
        await Expect(card.GetByTestId("background-status")).ToContainTextAsync("The bank wants a TAN or code.");
        await Expect(Page.GetByTestId("banklogins-needs-you")).ToBeVisibleAsync();

        // the owner's own sync (the session is remembered) clears it
        await card.Locator("[data-action=sync-now]").ClickAsync();
        await Expect(card.GetByTestId("background-status")).ToContainTextAsync("Last background sync");
        await GoToAsync("/banklogins");
        await Expect(Page.GetByTestId("banklogins-needs-you")).ToHaveCountAsync(0);
        await Expect(card.GetByTestId("background-switch")).ToBeCheckedAsync();
    }
}
