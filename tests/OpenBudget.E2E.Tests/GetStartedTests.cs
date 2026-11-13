using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using NUnit.Framework;
using OpenBudget.Services;
using OpenBudget.Tests;

namespace OpenBudget.E2E.Tests;

/// <summary>
/// The Get started page: that the gear menu offers it until the MoneyMoney import is finished or the user hides it, that
/// its URL always works, and the MoneyMoney step's upload in the browser (a synthetic ZIP of MoneyMoney's data folder)
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

    private async Task SetMoneyMoneyAsync(DateTime? lastUpload, DateTime? finished)
    {
        await using var db = await Db.CreateDbContextAsync();
        var source = await db.UserDataStates.SingleAsync(s => s.UserId == E2ESeed.OwnerUserId);
        source.LastUploadAt = lastUpload;
        source.MoneyMoneyImportFinishedAt = finished;
        await db.SaveChangesAsync();
    }

    [Test]
    public async Task The_gear_menu_offers_it_until_the_MoneyMoney_import_is_finished_or_it_is_hidden()
    {
        // Data from a bank login and accounts do not end Get started: MoneyMoney may still come.
        await EditingSeed.SeedAsync(Db);
        await using (var db = await Db.CreateDbContextAsync())
        {
            db.BankLogins.Add(new Models.BankLogin { OwnerUserId = E2ESeed.OwnerUserId, ConnectorType = "fake", DisplayName = "Example Bank", RefreshInterval = Models.RefreshInterval.Never });
            await db.SaveChangesAsync();
        }
        await GoToAsync($"/projects/{E2ESeed.ProjectId}/browse");
        Assert.That(await GearOffersGetStartedAsync(), Is.True);

        // An unfinished MoneyMoney import: offered, with its follow-up on the page.
        await SetMoneyMoneyAsync(DateTime.UtcNow, null);
        await GoToAsync("/get-started");
        await Expect(Page.GetByTestId("mm-followup")).ToBeVisibleAsync();

        // Finished: no longer offered, and the step says so.
        await SetMoneyMoneyAsync(DateTime.UtcNow, DateTime.UtcNow);
        await GoToAsync($"/projects/{E2ESeed.ProjectId}/browse");
        Assert.That(await GearOffersGetStartedAsync(), Is.False);
        await GoToAsync("/get-started");
        await Expect(Page.GetByTestId("import-finished")).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("mm-followup")).ToHaveCountAsync(0);
    }

    [Test]
    public async Task Hiding_Get_started_removes_it_from_the_menus_but_the_page_still_opens()
    {
        await using (var db = await Db.CreateDbContextAsync())
        {
            db.BankLogins.Add(new Models.BankLogin { OwnerUserId = E2ESeed.OwnerUserId, ConnectorType = "fake", DisplayName = "Example Bank", RefreshInterval = Models.RefreshInterval.Never });
            await db.SaveChangesAsync();
        }
        await GoToAsync($"/projects/{E2ESeed.ProjectId}/browse");
        Assert.That(await GearOffersGetStartedAsync(), Is.True);

        await GoToAsync("/banklogins");
        await Expect(Page.GetByTestId("settings-nav-get-started")).ToBeVisibleAsync();
        await GoToAsync("/get-started");
        await Page.GetByTestId("get-started-hide").ClickAsync();
        await Expect(Page.GetByTestId("get-started-hide")).ToHaveCountAsync(0);

        await GoToAsync($"/projects/{E2ESeed.ProjectId}/browse");
        Assert.That(await GearOffersGetStartedAsync(), Is.False);
        await GoToAsync("/banklogins");
        await Expect(Page.GetByTestId("settings-nav-banklogins")).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("settings-nav-get-started")).ToHaveCountAsync(0);
        await GoToAsync("/get-started");
        await Expect(Page.GetByTestId("step-banks")).ToBeVisibleAsync();
    }

    [Test]
    public async Task The_wizard_leads_through_the_steps_and_back_from_the_pages_it_links_to_and_Done_ends_it()
    {
        await GoToAsync("/get-started");
        await Expect(Page.GetByTestId("get-started-progress")).ToContainTextAsync("Step 1 of 4");
        await Expect(Page.GetByTestId("get-started-next")).ToHaveTextAsync("Skip");

        // Adding a bank login happens on its page, which leads back to the next step.
        await Page.GetByTestId("step-banks-link").ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex("/banklogins\\?return="));
        await Page.GetByTestId("get-started-return").ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex("/get-started\\?step=2$"));
        await Expect(Page.GetByTestId("step-moneymoney")).ToBeVisibleAsync();

        await Page.GetByTestId("get-started-next").ClickAsync();
        await Expect(Page.GetByTestId("step-portfolio")).ToBeVisibleAsync();
        await Page.GetByTestId("step-portfolio-link").ClickAsync();
        await Page.GetByTestId("get-started-return").ClickAsync();
        await Expect(Page.GetByTestId("step-cashflow")).ToBeVisibleAsync();
        await Page.GetByTestId("get-started-back").ClickAsync();
        await Expect(Page.GetByTestId("step-portfolio")).ToBeVisibleAsync();
        await Page.GetByTestId("get-started-next").ClickAsync();

        // Done ends Get started: the project opens, the menus no longer offer it and signing in goes to the project.
        await Page.GetByTestId("get-started-done").ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex($"/projects/{E2ESeed.ProjectId}/browse$"));
        Assert.That(await GearOffersGetStartedAsync(), Is.False);
        await LogoutAsync();
        await LoginAsync(E2ESeed.UserName);
        await Expect(Page).Not.ToHaveURLAsync(new Regex("/get-started"));
    }

    [Test]
    public async Task Signing_in_with_a_deep_link_opens_that_page_even_before_Get_started_is_done()
    {
        await LogoutAsync();
        await Page.GotoAsync($"/projects/{E2ESeed.ProjectId}/budgets");
        await Expect(Page).ToHaveURLAsync(new Regex("/Account/Login"));
        await Page.GetByLabel("Username").FillAsync(E2ESeed.UserName);
        await Page.GetByLabel("Password").FillAsync(E2ESeed.Password);
        await Page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex($"/projects/{E2ESeed.ProjectId}/budgets$"));
    }

    [Test]
    public async Task A_new_user_is_led_there_and_imports_MoneyMoney_from_a_zip()
    {
        await LogoutAsync();
        await LoginAsync(E2ESeed.SecondUserName);

        // Signing in leads to Get started; its first step is the bank logins, the next one MoneyMoney.
        await Expect(Page).ToHaveURLAsync(new Regex("/get-started$"));
        await Expect(Page.GetByTestId("settings-nav-get-started")).ToHaveAttributeAsync("aria-current", "page");
        await Expect(Page.GetByTestId("step-banks")).ToBeVisibleAsync();
        await Page.GetByTestId("get-started-next").ClickAsync();
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
