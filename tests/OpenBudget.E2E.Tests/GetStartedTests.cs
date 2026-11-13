using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using NUnit.Framework;
using OpenBudget.Services;
using OpenBudget.Tests;

namespace OpenBudget.E2E.Tests;

/// <summary>
/// The Get started wizard: that it holds the user until it is done, brings each step inline, links bank accounts to
/// the imported ones, and the MoneyMoney step's upload in the browser (a synthetic ZIP of MoneyMoney's data folder)
/// without, with a wrong and then with the right password, imported as the user's data.
/// </summary>
[TestFixture]
public sealed class GetStartedTests : EndToEndTestBase
{
    private SystemDbContextFactory Db => Factory.Services.GetRequiredService<SystemDbContextFactory>();

    private async Task<bool> GearOffersGetStartedAsync()
    {
        var gear = Page.GetByTestId("gear-menu");
        await gear.Locator("summary").ClickAsync();
        await Expect(gear.GetByTestId("gear-bank-logins")).ToBeVisibleAsync();
        var shown = await gear.GetByTestId("gear-get-started").CountAsync() > 0;
        await Page.Keyboard.PressAsync("Escape");
        return shown;
    }

    /// <summary>Puts the signed-in owner back into Get started (the baseline world has it done).</summary>
    private async Task ReopenGetStartedAsync(string userId = E2ESeed.OwnerUserId)
    {
        await using var db = await Db.CreateDbContextAsync();
        (await db.UserDataStates.SingleAsync(s => s.UserId == userId)).GetStartedDismissedAt = null;
        await db.SaveChangesAsync();
    }

    private async Task SetMoneyMoneyAsync(DateTime? lastUpload, DateTime? finished)
    {
        await using var db = await Db.CreateDbContextAsync();
        var source = await db.UserDataStates.SingleAsync(s => s.UserId == E2ESeed.OwnerUserId);
        source.LastUploadAt = lastUpload;
        source.MoneyMoneyImportFinishedAt = finished;
        await db.SaveChangesAsync();
    }

    [Test]
    public async Task Until_Get_started_is_done_every_page_leads_there_and_the_navigation_is_hidden()
    {
        await ReopenGetStartedAsync();
        foreach (var url in new[] { "/", $"/projects/{E2ESeed.ProjectId}/settings/categories", "/banklogins", "/portfolio-performance" })
        {
            await GoToAsync(url);
            await Expect(Page).ToHaveURLAsync(new Regex("/get-started$"));
        }
        await Expect(Page.GetByTestId("gear-menu")).ToHaveCountAsync(0);
        await Expect(Page.Locator("[data-testid=project-selector]")).ToHaveCountAsync(0);
        await Expect(Page.GetByTestId("settings-nav")).ToHaveCountAsync(0);
        await Expect(Page.GetByTestId("user-menu")).ToBeVisibleAsync();

        // Signing in again, even with a deep link, lands in Get started.
        await LogoutAsync();
        await Page.GotoAsync($"/projects/{E2ESeed.ProjectId}/budgets");
        await Page.GetByLabel("Username").FillAsync(E2ESeed.UserName);
        await Page.GetByLabel("Password").FillAsync(E2ESeed.Password);
        await Page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex("/get-started$"));
    }

    [Test]
    public async Task The_wizard_brings_each_step_inline_and_Done_opens_the_app()
    {
        await ReopenGetStartedAsync();
        await GoToAsync("/get-started");
        await Expect(Page.GetByTestId("get-started-progress")).ToContainTextAsync("Step 1 of 4");
        await Expect(Page.GetByTestId("step-moneymoney")).ToBeVisibleAsync();
        var next = Page.GetByTestId("get-started-next");
        await Expect(next).ToHaveTextAsync("Skip");
        await Expect(next).ToHaveClassAsync(new Regex("btn-outline-secondary"));

        await next.ClickAsync();
        await Expect(Page.GetByTestId("step-banks-panel")).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("add-login-open")).ToBeVisibleAsync();
        await next.ClickAsync();
        await Expect(Page.GetByTestId("pp-upload")).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("onedrive-connect")).ToHaveAttributeAsync("href", "/connect/onedrive?return=get-started");
        await next.ClickAsync();
        await Expect(Page.GetByTestId("step-cashflow-accounts")).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("accounts-card")).ToBeVisibleAsync();
        await Page.GetByTestId("get-started-back").ClickAsync();
        await Expect(Page.GetByTestId("pp-upload")).ToBeVisibleAsync();
        await next.ClickAsync();

        await Page.GetByTestId("get-started-done").ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex($"/projects/{E2ESeed.ProjectId}/browse$"));
        Assert.That(await GearOffersGetStartedAsync(), Is.False);
        await GoToAsync($"/projects/{E2ESeed.ProjectId}/settings/categories");
        await Expect(Page).ToHaveURLAsync(new Regex("/settings/categories$"));
    }

    [Test]
    public async Task Hiding_Get_started_ends_it()
    {
        await ReopenGetStartedAsync();
        await GoToAsync("/get-started");
        await Page.GetByTestId("get-started-hide").ClickAsync();
        await Expect(Page.GetByTestId("get-started-hide")).ToHaveCountAsync(0);
        await GoToAsync($"/projects/{E2ESeed.ProjectId}/browse");
        await Expect(Page).ToHaveURLAsync(new Regex($"/projects/{E2ESeed.ProjectId}/browse$"));
        Assert.That(await GearOffersGetStartedAsync(), Is.False);
        await GoToAsync("/get-started");
        await Expect(Page.GetByTestId("step-moneymoney")).ToBeVisibleAsync();
    }

    [Test]
    public async Task An_imported_account_is_suggested_for_the_bank_account_with_its_IBAN_and_keeps_its_history()
    {
        await LogoutAsync();
        await ReopenGetStartedAsync(E2ESeed.SecondUserId);
        await LoginAsync(E2ESeed.SecondUserName);
        await Expect(Page).ToHaveURLAsync(new Regex("/get-started$"));

        var buffer = MoneyMoneyZipFixture.Build("e2e-secret", checkingIban: OpenBudget.Connectors.FakeBank.FakeBankConnector.GiroIban);
        var zip = new FilePayload { Name = "MoneyMoney.zip", MimeType = "application/zip", Buffer = buffer };
        await Page.Locator("#mm-zip").SetInputFilesAsync(zip);
        await Page.GetByTestId("mm-has-password").CheckAsync();
        await Page.Locator("#mm-password").FillAsync("e2e-secret");
        await Page.GetByTestId("mm-upload").ClickAsync();
        await Expect(Page.GetByTestId("mm-job-done")).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await Page.GetByTestId("get-started-next").ClickAsync();

        // Step 2: the bank login is added inline; its giro gets the imported account with the same IBAN suggested.
        await Page.GetByTestId("add-login-open").ClickAsync();
        await Page.Locator("#new-login-connector").SelectOptionAsync("fake");
        await Page.Locator("#new-login-name").FillAsync("My fake bank");
        await Page.Locator("#new-login-username").FillAsync("demo+code");
        await Page.Locator("#new-login-pin").FillAsync("1234");
        await Page.Locator("#new-login-add").ClickAsync();
        var challenge = Page.Locator("form#bank-challenge-form #bank-challenge-input");
        await challenge.FillAsync("123456");
        await challenge.PressAsync("Enter");

        var giro = Page.Locator("[data-bank-login='My fake bank'] [data-bank-account='FAKE-GIRO']");
        await Expect(giro.Locator("select option[selected]")).ToContainTextAsync("Example Checking");
        await giro.Locator("[data-action=link]").ClickAsync();
        await Expect(giro.GetByTestId("linked-to")).ToContainTextAsync("Example Checking");

        await using var db = await Db.CreateDbContextAsync();
        var guest = await db.Users.SingleAsync(u => u.UserName == E2ESeed.SecondUserName);
        Assert.That(await db.Accounts.CountAsync(a => a.OwnerUserId == guest.Id), Is.EqualTo(2));   // no second account
        var checking = await db.Accounts.SingleAsync(a => a.OwnerUserId == guest.Id && a.Name == "Example Checking");
        Assert.That(await db.Transactions.CountAsync(t => t.AccountId == checking.Id), Is.EqualTo(2));   // the imported history
        Assert.That(await db.BankLoginAccounts.AnyAsync(l => l.LinkedAccountId == checking.Id), Is.True);
    }

    [Test]
    public async Task A_new_user_is_led_there_and_imports_MoneyMoney_from_a_zip()
    {
        await LogoutAsync();
        await ReopenGetStartedAsync(E2ESeed.SecondUserId);
        await LoginAsync(E2ESeed.SecondUserName);

        // Signing in leads to Get started; its first step is the MoneyMoney import.
        await Expect(Page).ToHaveURLAsync(new Regex("/get-started$"));
        await Expect(Page.GetByTestId("mm-instructions")).ToContainTextAsync("Show Database in Finder");

        var zip = new FilePayload { Name = "MoneyMoney.zip", MimeType = "application/zip", Buffer = MoneyMoneyZipFixture.Build("e2e-secret") };

        // Without the password box ticked there is no password field; an encrypted database asks for it.
        await Expect(Page.Locator("#mm-password")).ToHaveCountAsync(0);
        await Page.Locator("#mm-zip").SetInputFilesAsync(zip);
        await Page.GetByTestId("mm-upload").ClickAsync();
        await Expect(Page.GetByTestId("mm-job-error")).ToContainTextAsync("has a password");

        // A wrong password: a clear error, nothing imported.
        await Page.Locator("#mm-zip").SetInputFilesAsync(zip);
        await Page.GetByTestId("mm-has-password").CheckAsync();
        await Page.Locator("#mm-password").FillAsync("not-the-password");
        await Page.GetByTestId("mm-upload").ClickAsync();
        await Expect(Page.GetByTestId("mm-job-error")).ToContainTextAsync("password does not open");

        // The right one: imported in the background, the result shown.
        await Page.Locator("#mm-zip").SetInputFilesAsync(zip);
        await Page.Locator("#mm-password").FillAsync("e2e-secret");
        await Page.GetByTestId("mm-upload").ClickAsync();
        await Expect(Page.GetByTestId("mm-job-done")).ToContainTextAsync("2 accounts, 3 bookings, 1 new statements", new() { Timeout = 30_000 });
        await Expect(Page.Locator("#mm-password")).ToHaveCountAsync(0);

        await using var db = await Db.CreateDbContextAsync();
        var guest = await db.Users.SingleAsync(u => u.UserName == E2ESeed.SecondUserName);
        var source = await db.UserDataStates.SingleAsync(s => s.UserId == guest.Id);
        Assert.That(source.LastUploadAt, Is.Not.Null);
        Assert.That(await db.Accounts.CountAsync(a => a.OwnerUserId == guest.Id), Is.EqualTo(2));
        Assert.That(await db.BankStatements.CountAsync(s => s.OwnerUserId == guest.Id), Is.EqualTo(1));

        // The import is unfinished now: its follow-up shows the accounts without bank connection.
        await Page.GetByTestId("mm-job-done").GetByRole(AriaRole.Button).ClickAsync();
        await Expect(Page.GetByTestId("mm-followup")).ToContainTextAsync("Example Checking");
    }

    // Set OB_ONBOARDING_SCREENSHOT_DIR to keep screenshots of the gear menu, the Portfolio Performance page and the
    // Get started page (with an unfinished MoneyMoney import) in both themes for review.
    [TestCase("light")]
    [TestCase("dark")]
    public async Task Screenshot(string theme)
    {
        var dir = Environment.GetEnvironmentVariable("OB_ONBOARDING_SCREENSHOT_DIR");
        if (string.IsNullOrEmpty(dir)) Assert.Ignore("OB_ONBOARDING_SCREENSHOT_DIR not set");
        await EditingSeed.SeedAsync(Db);
        await SetMoneyMoneyAsync(DateTime.UtcNow, null);
        await Page.SetViewportSizeAsync(1280, 900);
        await Page.EmulateMediaAsync(new PageEmulateMediaOptions { ColorScheme = theme == "dark" ? ColorScheme.Dark : ColorScheme.Light });

        await GoToAsync("/portfolio-performance");
        await Page.Locator("#pp-upload-file").SetInputFilesAsync(new FilePayload
        {
            Name = "Example.portfolio",
            MimeType = "application/octet-stream",
            Buffer = PortfolioPerformanceSettingsTests.PortfolioFile("Example Depot"),
        });
        await Expect(Page.GetByTestId("pp-data-set")).ToHaveCountAsync(1);
        await Page.WaitForTimeoutAsync(300);
        await Page.ScreenshotAsync(new() { Path = Path.Combine(dir, $"onboarding-portfolio-performance-{theme}.png") });

        await GoToAsync($"/projects/{E2ESeed.ProjectId}/browse");
        await Page.GetByTestId("gear-menu").Locator("summary").ClickAsync();
        await Expect(Page.GetByTestId("gear-menu").GetByTestId("gear-get-started")).ToBeVisibleAsync();
        await Page.WaitForTimeoutAsync(300);
        await Page.ScreenshotAsync(new() { Path = Path.Combine(dir, $"onboarding-gear-menu-{theme}.png") });

        await GoToAsync("/get-started");
        await Expect(Page.GetByTestId("mm-followup")).ToBeVisibleAsync();
        await Page.WaitForTimeoutAsync(300);
        await Page.ScreenshotAsync(new() { Path = Path.Combine(dir, $"onboarding-get-started-{theme}.png"), FullPage = true });
    }
}
