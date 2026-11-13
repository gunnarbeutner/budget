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
        (await db.UserDataStates.SingleAsync(s => s.UserId == userId)).GetStartedCompletedAt = null;
        await db.SaveChangesAsync();
        // The browser import outlives a test; forget an earlier test's import result.
        Factory.BrowserHostServices.GetRequiredService<OpenBudget.Importers.MoneyMoney.MoneyMoneyBrowserImport>().Dismiss(userId);
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
        // The user menu only signs out; the account page leads back too.
        await Page.GetByTestId("user-menu").Locator("summary").ClickAsync();
        await Expect(Page.GetByTestId("sign-out-link")).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("user-account-link")).ToHaveCountAsync(0);
        await Page.GotoAsync("/Account");
        await Expect(Page).ToHaveURLAsync(new Regex("/get-started$"));

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
        await Expect(Page.GetByTestId("get-started-stepper-1")).ToHaveAttributeAsync("aria-current", "step");
        await Expect(Page.GetByTestId("get-started-stepper")).ToContainTextAsync("Accounts & budget");
        await Expect(Page.GetByTestId("step-moneymoney")).ToBeVisibleAsync();
        var next = Page.GetByTestId("get-started-next");
        // The seeded owner has categories, so the first step counts as done.
        await Expect(next).ToHaveTextAsync("Next");
        await Expect(next).ToHaveClassAsync(new Regex("btn-primary"));

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
    public async Task Each_step_starts_at_the_top_with_its_heading_in_view_and_only_Done_ends_it()
    {
        await ReopenGetStartedAsync();
        await Page.SetViewportSizeAsync(390, 700);
        await GoToAsync("/get-started");
        var next = Page.GetByTestId("get-started-next");
        foreach (var step in new[] { "step-banks", "step-portfolio", "step-cashflow" })
        {
            await Page.Mouse.WheelAsync(0, 5000);
            await next.ClickAsync();
            await Expect(Page.GetByTestId(step).Locator("h2").First).ToBeInViewportAsync();
        }
        await Expect(Page.GetByTestId("get-started-stepper-4")).ToHaveAttributeAsync("aria-current", "step");
        await GoToAsync($"/projects/{E2ESeed.ProjectId}/browse");
        await Expect(Page).ToHaveURLAsync(new Regex("/get-started$"));
    }

    [Test]
    public async Task While_the_import_runs_the_wizard_holds_and_Next_waits_for_it()
    {
        await LogoutAsync();
        await ReopenGetStartedAsync(E2ESeed.SecondUserId);
        await LoginAsync(E2ESeed.SecondUserName);
        var import = Factory.BrowserHostServices.GetRequiredService<OpenBudget.Importers.MoneyMoney.MoneyMoneyBrowserImport>();
        var release = new TaskCompletionSource();
        import.AfterStatementsForTests = () => release.Task;
        try
        {
            var zip = new FilePayload { Name = "MoneyMoney.zip", MimeType = "application/zip", Buffer = MoneyMoneyZipFixture.Build("e2e-secret") };
            await Page.Locator("#mm-zip").SetInputFilesAsync(zip);
            await Page.GetByTestId("mm-has-password").CheckAsync();
            await Page.Locator("#mm-password").FillAsync("e2e-secret");
            await Page.GetByTestId("mm-upload").ClickAsync();
            await Expect(Page.GetByTestId("mm-job")).ToBeVisibleAsync(new() { Timeout = 30_000 });

            await Expect(Page.GetByTestId("get-started-next")).ToBeDisabledAsync();
            await Expect(Page.GetByTestId("get-started-stepper-1")).Not.ToHaveClassAsync(new Regex("done"));
            await Expect(Page.GetByTestId("gear-menu")).ToHaveCountAsync(0);
            await Expect(Page.GetByTestId("settings-nav")).ToHaveCountAsync(0);
        }
        finally
        {
            release.TrySetResult();
        }
        // Success moves on to step 2 by itself; step 1 is done, and Get started still holds.
        await Expect(Page.GetByTestId("mm-job-done")).ToBeVisibleAsync(new() { Timeout = 30_000 });
        import.AfterStatementsForTests = null;
        await Expect(Page).ToHaveURLAsync(new Regex("step=2$"));
        await Expect(Page.GetByTestId("get-started-stepper-1")).ToHaveClassAsync(new Regex("done"));
        await Expect(Page.GetByTestId("get-started-next")).ToBeEnabledAsync();
        await Expect(Page.GetByTestId("gear-menu")).ToHaveCountAsync(0);
        await GoToAsync("/banklogins");
        await Expect(Page).ToHaveURLAsync(new Regex("/get-started$"));
    }

    [Test]
    public async Task Suggested_categories_are_created_once_in_the_default_project_with_income_typed()
    {
        await LogoutAsync();
        await ReopenGetStartedAsync(E2ESeed.SecondUserId);
        await UserDefaultProject.EnsureAsync(Db, E2ESeed.SecondUserId);   // as at startup
        await LoginAsync(E2ESeed.SecondUserName);
        await Page.GetByTestId("starter-categories").ClickAsync();
        // Done: on to step 2; the offer is gone from step 1.
        await Expect(Page).ToHaveURLAsync(new Regex("step=2$"));
        await Page.GetByTestId("get-started-back").ClickAsync();
        await Expect(Page.GetByTestId("step-moneymoney")).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("starter-categories")).ToHaveCountAsync(0);
        await Expect(Page.GetByTestId("get-started-next")).ToHaveTextAsync("Next");

        await using var db = await Db.CreateDbContextAsync();
        var groups = await db.CategoryGroups.Where(g => g.OwnerUserId == E2ESeed.SecondUserId).ToListAsync();
        Assert.That(groups, Has.Count.EqualTo(12));
        Assert.That(await db.Categories.Where(c => c.OwnerUserId == E2ESeed.SecondUserId).Select(c => c.Color).ToListAsync(), Has.All.Not.Null);
        var income = groups.Single(g => g.Name == "Income");
        var salary = await db.Categories.SingleAsync(c => c.OwnerUserId == E2ESeed.SecondUserId && c.Name == "Salary");
        Assert.That(salary.GroupId, Is.EqualTo(income.Id));
        Assert.That(await db.Categories.CountAsync(c => c.OwnerUserId == E2ESeed.SecondUserId && c.Name == "Gifts"), Is.EqualTo(1));
        var project = await db.Projects.SingleAsync(p => p.OwnerUserId == E2ESeed.SecondUserId && p.IsDefaultForNewAccounts);
        var assigned = await db.ProjectCategoryAssignments.Where(a => a.ProjectId == project.Id).Select(a => a.CategoryId).ToListAsync();
        Assert.That(assigned, Is.SupersetOf(groups.Select(g => g.Id)));
        Assert.That(await db.CategoryTypeAssignments.AnyAsync(t => t.ProjectId == project.Id && t.CategoryId == salary.Id && t.IsIncome), Is.True);
        var cash = await db.Accounts.SingleAsync(a => a.OwnerUserId == E2ESeed.SecondUserId);
        Assert.That((cash.Name, cash.Type, cash.Provider), Is.EqualTo(("Cash", (long?)6, OpenBudget.Models.AccountProviders.Manual)));
        var cashAssignment = await db.ProjectAccountAssignments.SingleAsync(a => a.ProjectId == project.Id && a.AccountId == cash.Id);
        Assert.That(cashAssignment.InCashflow, Is.True);
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
        // A successful import moves on to step 2, which shows its result.
        await Expect(Page.GetByTestId("step-banks").GetByTestId("mm-job-done")).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await Expect(Page).ToHaveURLAsync(new Regex("step=2$"));

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
        Assert.That(checking.Provider, Is.EqualTo(OpenBudget.Connectors.FakeBank.FakeBankConnector.TypeName));   // the bank's at once
    }

    [Test]
    public async Task A_new_user_is_led_there_and_imports_MoneyMoney_from_a_zip()
    {
        await LogoutAsync();
        await ReopenGetStartedAsync(E2ESeed.SecondUserId);
        await UserDefaultProject.EnsureAsync(Db, E2ESeed.SecondUserId);   // as at startup
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
        await Expect(Page.GetByTestId("step-banks").GetByTestId("mm-job-done")).ToContainTextAsync("2 accounts, 3 bookings, 1 new statements", new() { Timeout = 30_000 });
        await Expect(Page).ToHaveURLAsync(new Regex("step=2$"));
        await Page.GetByTestId("mm-job-dismiss").ClickAsync();
        await Expect(Page.GetByTestId("mm-job-done")).ToHaveCountAsync(0);

        await using var db = await Db.CreateDbContextAsync();
        var guest = await db.Users.SingleAsync(u => u.UserName == E2ESeed.SecondUserName);
        var source = await db.UserDataStates.SingleAsync(s => s.UserId == guest.Id);
        Assert.That(source.LastUploadAt, Is.Not.Null);
        Assert.That(await db.Accounts.CountAsync(a => a.OwnerUserId == guest.Id), Is.EqualTo(2));
        Assert.That(await db.BankStatements.CountAsync(s => s.OwnerUserId == guest.Id), Is.EqualTo(1));

        // The default project gets income and tracking as the data suggests: Salary (positive bookings) is income,
        // the budgeted categories are tracked.
        var project = await db.Projects.SingleAsync(p => p.OwnerUserId == guest.Id && p.IsDefaultForNewAccounts);
        var ids = await db.Categories.Where(c => c.OwnerUserId == guest.Id).ToDictionaryAsync(c => c.Name!, c => c.Id);
        Assert.That(await db.CategoryTypeAssignments.AnyAsync(t => t.ProjectId == project.Id && t.CategoryId == ids["Salary"] && t.IsIncome), Is.True);
        Assert.That(await db.CategoryTypeAssignments.AnyAsync(t => t.ProjectId == project.Id && t.CategoryId == ids["Groceries"] && t.IsIncome), Is.False);
        var trackedIds = await db.ProjectTrackingItems.Where(t => t.ProjectId == project.Id && t.CategoryId != null).Select(t => t.CategoryId!.Value).ToListAsync();
        Assert.That(trackedIds, Is.SupersetOf(new[] { ids["Groceries"], ids["Cinema"] }));
        Assert.That((await db.Budgets.SingleAsync(b => b.ProjectId == project.Id)).IsActive, Is.True);

        // Step 4 shows them, ticked.
        await Page.GetByTestId("get-started-stepper-4").Locator("a").ClickAsync();
        var salaryRow = Page.GetByTestId("category-setup").Locator("[data-setup-row='Salary']");
        await Expect(salaryRow.GetByTestId("setup-income")).ToBeCheckedAsync();
        await Expect(Page.GetByTestId("category-setup").Locator("[data-setup-row='Groceries']").GetByTestId("setup-track")).ToBeCheckedAsync();

        // Done turns the imported accounts not linked to a bank into manual ones and finishes the one-time import.
        await Page.GetByTestId("get-started-done").ClickAsync();
        await Expect(Page).Not.ToHaveURLAsync(new Regex("/get-started"));
        await using var after = await Db.CreateDbContextAsync();
        Assert.That(await after.Accounts.Where(a => a.OwnerUserId == guest.Id).Select(a => a.Provider).Distinct().ToListAsync(),
            Is.EqualTo(new[] { OpenBudget.Models.AccountProviders.Manual }));
        Assert.That((await after.UserDataStates.SingleAsync(s => s.UserId == guest.Id)).MoneyMoneyImportFinishedAt, Is.Not.Null);
    }

    // Set OB_ONBOARDING_SCREENSHOT_DIR to keep screenshots of each Get started step at desktop and phone width in
    // both themes for review.
    [TestCase("light")]
    [TestCase("dark")]
    public async Task Screenshot(string theme)
    {
        var dir = Environment.GetEnvironmentVariable("OB_ONBOARDING_SCREENSHOT_DIR");
        if (string.IsNullOrEmpty(dir)) Assert.Ignore("OB_ONBOARDING_SCREENSHOT_DIR not set");
        await EditingSeed.SeedAsync(Db);
        await ReopenGetStartedAsync();
        await Page.EmulateMediaAsync(new PageEmulateMediaOptions { ColorScheme = theme == "dark" ? ColorScheme.Dark : ColorScheme.Light });
        foreach (var (width, height) in new[] { (1440, 900), (390, 844) })
        {
            await Page.SetViewportSizeAsync(width, height);
            for (var step = 1; step <= 4; step++)
            {
                await GoToAsync($"/get-started?step={step}");
                await Expect(Page.GetByTestId($"get-started-stepper-{step}")).ToHaveAttributeAsync("aria-current", "step");
                await Page.WaitForTimeoutAsync(500);
                await Page.ScreenshotAsync(new() { Path = Path.Combine(dir, $"get-started-{step}-{width}-{theme}.png"), FullPage = true });
            }
        }
    }
}
