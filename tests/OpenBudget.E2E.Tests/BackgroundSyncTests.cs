using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using NUnit.Framework;
using OpenBudget.Connectors;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.E2E.Tests;

/// <summary>The background-sync switch of a bank login, its status and the badge while a login needs the owner.</summary>
[TestFixture]
public sealed class BackgroundSyncTests : EndToEndTestBase
{
    [Test]
    public async Task Switch_on_background_syncs_see_needs_you_and_clear_it_with_a_manual_sync()
    {
        await GoToAsync("/connections");
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
        // on by default: the fake bank can sync fully on its own
        await Expect(toggle).ToBeCheckedAsync();
        await Expect(card.GetByTestId("background-status")).ToContainTextAsync("Last background sync");
        await Expect(Page.GetByTestId("banklogins-needs-you")).ToHaveCountAsync(0);

        // a background run stopped for the owner
        var factory = Factory.Services.GetRequiredService<SystemDbContextFactory>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            await db.BankLogins.Where(l => l.DisplayName == "Background bank")
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.NeedsManualSync, "The bank wants a TAN or code.").SetProperty(l => l.NeedsManualSince, DateTime.UtcNow));
        }
        await GoToAsync("/connections");
        await Expect(card.GetByTestId("login-status")).ToHaveTextAsync("Needs you");
        await Expect(card.GetByTestId("background-status")).ToContainTextAsync("The bank wants a TAN or code.");
        await Expect(Page.GetByTestId("banklogins-needs-you")).ToBeVisibleAsync();
        // the sign beside the sync status opens the list of connections that need the owner
        var problems = Page.GetByTestId("bank-sync-attention");
        await problems.Locator("summary").ClickAsync();
        await Expect(problems.GetByTestId("sync-problem")).ToContainTextAsync("The bank wants a TAN or code.");
        await Expect(problems.GetByTestId("sync-problems-connections")).ToHaveAttributeAsync("href", "/connections");
        await Page.Keyboard.PressAsync("Escape");

        // the owner's own sync (the session is remembered) clears it
        await card.Locator("[data-action=sync-now]").ClickAsync();
        await Expect(card.GetByTestId("background-status")).ToContainTextAsync("Last background sync");
        await GoToAsync("/connections");
        await Expect(Page.GetByTestId("banklogins-needs-you")).ToHaveCountAsync(0);
        await Expect(Page.GetByTestId("bank-sync-attention")).ToHaveCountAsync(0);
        await Expect(card.GetByTestId("background-switch")).ToBeCheckedAsync();
    }

    [Test]
    public async Task An_admin_allows_a_revoked_bank_from_the_switch_and_revokes_it_again()
    {
        await SeedRevokedFinTsLoginAsync(admin: true);
        await GoToAsync("/connections");
        var card = Page.Locator("[data-bank-login='Baader E2E']");
        var toggle = card.GetByTestId("background-switch");
        await Expect(toggle).Not.ToBeCheckedAsync();
        await Expect(toggle).ToBeEnabledAsync();

        // cancel leaves the bank revoked and the switch off
        await toggle.CheckAsync();
        await Expect(Page.GetByText("Allow background syncs for Baader Bank?")).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("allow-evidence")).ToContainTextAsync("The bank didn't ask for a TAN in 2 of 3 recent syncs. It last asked on");
        await Page.GetByRole(AriaRole.Button, new() { Name = "Cancel" }).ClickAsync();
        await Expect(toggle).Not.ToBeCheckedAsync();

        await toggle.CheckAsync();
        await Page.Locator("#allow-confirm").ClickAsync();
        await Expect(toggle).ToBeCheckedAsync();
        await Expect(card.GetByTestId("background-bank-allowed")).ToContainTextAsync("Background syncs allowed for this bank");

        await card.GetByTestId("background-bank-revoke").ClickAsync();
        await Expect(toggle).Not.ToBeCheckedAsync();
        await Expect(card.GetByTestId("background-bank-allowed")).ToHaveCountAsync(0);
    }

    [Test]
    public async Task A_user_cannot_switch_on_a_login_of_a_revoked_bank()
    {
        await SeedRevokedFinTsLoginAsync(admin: false);
        await GoToAsync("/connections");
        var card = Page.Locator("[data-bank-login='Baader E2E']");
        await Expect(card.GetByTestId("background-switch")).ToBeDisabledAsync();
        await Expect(card.GetByTestId("background-bank-off")).ToHaveTextAsync("Turned off for this bank.");
    }

    /// <summary>A Baader login of the owner whose bank an admin revoked, with three logged dialog inits (one asked for a TAN).</summary>
    private async Task SeedRevokedFinTsLoginAsync(bool admin)
    {
        var factory = Factory.Services.GetRequiredService<SystemDbContextFactory>();
        var blz = Factory.Services.GetRequiredService<ConnectorRegistry>().Find("baader")!.FinTsBlz!;
        await using var db = await factory.CreateDbContextAsync();
        var owner = await db.Users.SingleAsync(u => u.UserName == E2ESeed.UserName);
        owner.IsAdmin = admin;
        await db.FinTsBankSettings.ExecuteDeleteAsync(); // not part of the per-test reset
        var login = new BankLogin
        {
            OwnerUserId = owner.Id,
            ConnectorType = "baader",
            DisplayName = "Baader E2E",
            RefreshInterval = RefreshInterval.ManualOnly,
            BackgroundEnabled = true,
            BackgroundDefaulted = true,
        };
        db.BankLogins.Add(login);
        db.FinTsBankSettings.Add(new FinTsBankSetting { Blz = blz, BackgroundEnabled = false });
        await db.SaveChangesAsync();
        var now = DateTime.UtcNow;
        foreach (var (hoursAgo, codes) in new[] { (1, "3076"), (2, "-"), (3, "0030 3955") })
            db.BackgroundSyncRuns.Add(new BackgroundSyncRun
            {
                BankLoginId = login.Id,
                OwnerUserId = owner.Id,
                StartedAt = now.AddHours(-hoursAgo),
                FinishedAt = now.AddHours(-hoursAgo),
                FinTsCodes = codes,
                Blz = blz,
            });
        await db.SaveChangesAsync();
    }
}
