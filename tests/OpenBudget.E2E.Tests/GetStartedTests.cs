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
    public async Task The_gear_menu_offers_it_only_without_data_or_with_an_unfinished_MoneyMoney_import()
    {
        // Without any account or bank login: offered.
        await GoToAsync($"/projects/{E2ESeed.ProjectId}/browse");
        Assert.That(await GearOffersGetStartedAsync(), Is.True);

        // The owner has accounts and no MoneyMoney data: not offered, but the page still opens by URL.
        await EditingSeed.SeedAsync(Db);
        await GoToAsync($"/projects/{E2ESeed.ProjectId}/browse");
        Assert.That(await GearOffersGetStartedAsync(), Is.False);
        await GoToAsync("/get-started");
        await Expect(Page.GetByTestId("step-banks")).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("mm-followup")).ToHaveCountAsync(0);

        // An unfinished MoneyMoney import: offered, with its follow-up on the page.
        await SetMoneyMoneyAsync(DateTime.UtcNow, null);
        await GoToAsync($"/projects/{E2ESeed.ProjectId}/browse");
        Assert.That(await GearOffersGetStartedAsync(), Is.True);
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
    public async Task A_new_user_is_led_there_and_imports_MoneyMoney_from_a_zip()
    {
        await LogoutAsync();
        await LoginAsync(E2ESeed.SecondUserName);

        // No projects and no data: the welcome links to Get started, and the gear menu offers it.
        await GoToAsync("/");
        await Page.GetByTestId("welcome-get-started").ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex("/get-started$"));
        await Expect(Page.GetByTestId("settings-nav-get-started")).ToHaveAttributeAsync("aria-current", "page");
        await Expect(Page.GetByTestId("mm-instructions")).ToContainTextAsync("Show Database in Finder");

        var zip = new FilePayload { Name = "MoneyMoney.zip", MimeType = "application/zip", Buffer = MoneyMoneyZipFixture.Build("e2e-secret") };

        // Without the password box ticked there is no password field; an encrypted database asks for it.
        await Expect(Page.Locator("#mm-password")).ToHaveCountAsync(0);
        await Page.Locator("#mm-zip").SetInputFilesAsync(zip);
        await Page.GetByTestId("mm-upload").ClickAsync();
        await Expect(Page.GetByTestId("mm-job-error")).ToContainTextAsync("needs a password");

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
