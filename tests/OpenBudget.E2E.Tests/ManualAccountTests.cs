using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using NUnit.Framework;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.E2E.Tests;

/// <summary>
/// Accounts without a bank connection: kept as manual accounts or closed from the Import page's switch overview (in
/// bulk), and bookings of a manual account entered, edited and deleted in Transactions.
/// </summary>
[TestFixture]
public sealed class ManualAccountTests : EndToEndTestBase
{
    private static readonly long Cash = SourceScope.Namespace(E2ESeed.SourceId, 5);
    private static readonly long Old = SourceScope.Namespace(E2ESeed.SourceId, 6);

    private SystemDbContextFactory Db => Factory.Services.GetRequiredService<SystemDbContextFactory>();

    /// <summary>Two MoneyMoney accounts of the demo source (which has seen an upload), assigned to the demo project.</summary>
    private async Task SeedAsync(string provider = AccountProviders.MoneyMoney)
    {
        await using var db = await Db.CreateDbContextAsync();
        (await db.ImportSources.SingleAsync(s => s.Id == E2ESeed.SourceId)).LastUploadAt = DateTime.UtcNow;
        db.Accounts.Add(new Account { Id = Cash, SourceId = E2ESeed.SourceId, Name = "Example Cash", Currency = "EUR", Type = 6, Provider = provider, Balances = "11:100.000000,3:EUR," });
        db.Accounts.Add(new Account { Id = Old, SourceId = E2ESeed.SourceId, Name = "Example Old Savings", Currency = "EUR", Type = 2, Balances = "11:0.000000,3:EUR," });
        await db.SaveChangesAsync();
        db.ProjectAccountAssignments.Add(new ProjectAccountAssignment { ProjectId = E2ESeed.ProjectId, AccountId = Cash });
        db.ProjectAccountAssignments.Add(new ProjectAccountAssignment { ProjectId = E2ESeed.ProjectId, AccountId = Old });
        db.Transactions.Add(new Transaction
        {
            Id = 910001,
            SourceId = E2ESeed.SourceId,
            AccountId = Cash,
            Amount = -2000,
            Currency = "EUR",
            Timestamp = new DateTimeOffset(2026, 3, 5, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(),
            Name = "Example Market",
        });
        await db.SaveChangesAsync();
    }

    private async Task<Account> AccountAsync(long id)
    {
        await using var db = await Db.CreateDbContextAsync();
        return await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == id);
    }

    [Test]
    public async Task Keep_accounts_as_manual_in_bulk()
    {
        await SeedAsync();
        await GoToAsync("/get-started");

        var section = Page.GetByTestId("step-moneymoney");
        var list = section.GetByTestId("mm-accounts");
        await Expect(list.Locator($"[data-mm-account='{Cash}']")).ToBeVisibleAsync();
        await section.GetByTestId("mm-select-all").CheckAsync();
        await section.GetByTestId("mm-keep-manual").ClickAsync();

        var dialog = Page.GetByTestId("keep-manual-dialog");
        await Expect(dialog.Locator($"[data-keep-account='{Old}']")).ToBeVisibleAsync();
        await Page.GetByTestId("keep-manual-takeover").CheckAsync();
        await Page.GetByTestId("keep-manual-confirm").ClickAsync();
        await Expect(dialog).ToHaveCountAsync(0);

        var manual = section.GetByTestId("manual-accounts");
        await Expect(manual.Locator($"[data-manual-account='{Cash}']")).ToBeVisibleAsync();
        Assert.That((await AccountAsync(Cash)).Provider, Is.EqualTo(AccountProviders.Manual));
        Assert.That((await AccountAsync(Old)).Provider, Is.EqualTo(AccountProviders.Manual));

    }

    [Test]
    public async Task Closed_accounts_are_hidden_in_the_sidebar_until_shown()
    {
        await SeedAsync();
        await using (var db = await Db.CreateDbContextAsync())
        {
            (await db.Accounts.SingleAsync(a => a.Id == Old)).Closed = true;
            await db.SaveChangesAsync();
        }
        await GoToAsync($"/projects/{E2ESeed.ProjectId}/browse?year=2026&month=3");

        await Expect(Page.Locator(".browse-tree-row", new() { HasText = "Example Cash" })).ToBeVisibleAsync();
        await Expect(Page.Locator(".browse-tree-row", new() { HasText = "Example Old Savings" })).ToHaveCountAsync(0);
        await Page.GetByTestId("tree-closed-toggle").ClickAsync();
        await Expect(Page.Locator(".browse-tree-row", new() { HasText = "Example Old Savings" })).ToBeVisibleAsync();
    }

    [Test]
    public async Task Add_edit_and_delete_bookings_of_a_manual_account()
    {
        await SeedAsync(AccountProviders.Manual);
        await GoToAsync($"/projects/{E2ESeed.ProjectId}/browse?year=2026&month=3&sel=acct-{Cash}");

        await Page.GetByTestId("tx-add").ClickAsync();
        await Page.GetByTestId("manual-tx-date").FillAsync("2026-03-20");
        await Page.GetByTestId("manual-tx-amount").FillAsync("-12,34");
        await Page.GetByTestId("manual-tx-name").FillAsync("Example Kiosk");
        await Page.GetByTestId("manual-tx-save").ClickAsync();
        await Expect(Page.GetByTestId("manual-tx-form")).ToHaveCountAsync(0);

        var kiosk = Page.GetByTestId("tx-row").Filter(new() { HasText = "Example Kiosk" });
        await Expect(kiosk).ToBeVisibleAsync();
        Assert.That((await AccountAsync(Cash)).CurrentBalance, Is.EqualTo(87.66m));

        // An imported history row: its amount is editable from the context menu.
        var market = Page.GetByTestId("tx-row").Filter(new() { HasText = "Example Market" });
        await market.ClickAsync(new() { Button = MouseButton.Right });
        await Page.GetByTestId("ctx-edit-booking").ClickAsync();
        await Page.GetByTestId("manual-tx-amount").FillAsync("-25");
        await Page.GetByTestId("manual-tx-save").ClickAsync();
        await Expect(Page.GetByTestId("manual-tx-form")).ToHaveCountAsync(0);
        await Expect(market).ToContainTextAsync("25,00");
        Assert.That((await AccountAsync(Cash)).CurrentBalance, Is.EqualTo(82.66m));

        await kiosk.ClickAsync(new() { Button = MouseButton.Right });
        await Page.GetByTestId("ctx-edit-booking").ClickAsync();
        await Page.GetByTestId("manual-tx-delete").ClickAsync();
        await Page.GetByTestId("manual-tx-delete").ClickAsync();
        await Expect(kiosk).ToHaveCountAsync(0);
        Assert.That((await AccountAsync(Cash)).CurrentBalance, Is.EqualTo(95m));
    }

    /// <summary>
    /// A manual account's initial balance is calibrated in its settings from the balance on a day; accounts the bank
    /// updates show it read-only. Set OB_INITIAL_BALANCE_SCREENSHOT_DIR to keep screenshots of the settings.
    /// </summary>
    [TestCase("light")]
    [TestCase("dark")]
    public async Task Calibrate_the_initial_balance_of_a_manual_account(string theme)
    {
        var dir = Environment.GetEnvironmentVariable("OB_INITIAL_BALANCE_SCREENSHOT_DIR");
        await Page.EmulateMediaAsync(new PageEmulateMediaOptions { ColorScheme = theme == "dark" ? ColorScheme.Dark : ColorScheme.Light });
        await SeedAsync(AccountProviders.Manual);
        await GoToAsync($"/projects/{E2ESeed.ProjectId}/browse?year=2026&month=3&sel=acct-{Cash}");
        var sidebar = Page.GetByTestId("project-sidebar");

        await sidebar.GetByTestId("tree-account").Filter(new() { HasText = "Example Old Savings" }).First.ClickAsync(new() { Button = MouseButton.Right });
        await Page.GetByTestId("ctx-settings").ClickAsync();
        var dialog = Page.GetByTestId("account-settings");
        await Expect(dialog.GetByTestId("account-settings-initial-amount")).ToBeDisabledAsync();
        await Expect(dialog.GetByTestId("account-settings-calibrate")).ToHaveCountAsync(0);
        await Page.GetByTestId("modal-dialog").GetByText("Cancel").ClickAsync();
        await Expect(dialog).ToHaveCountAsync(0);

        await sidebar.GetByTestId("tree-account").Filter(new() { HasText = "Example Cash" }).First.ClickAsync(new() { Button = MouseButton.Right });
        await Page.GetByTestId("ctx-settings").ClickAsync();
        await Expect(dialog.GetByTestId("account-settings-initial-amount")).ToHaveValueAsync("0,00");
        await dialog.GetByTestId("account-settings-calibrate").ClickAsync();
        // At the end of 4 March the account held 120 (100 now, the -20 of 5 March not yet booked); 150 then means +30.
        await dialog.GetByTestId("account-settings-cal-date").FillAsync("2026-03-04");
        await dialog.GetByTestId("account-settings-cal-date").DispatchEventAsync("change");
        await dialog.GetByTestId("account-settings-cal-balance").FillAsync("150");
        await dialog.GetByTestId("account-settings-cal-balance").BlurAsync();
        await Expect(dialog.GetByTestId("account-settings-cal-result")).ToContainTextAsync("30,00 €");
        if (dir != null)
        {
            Directory.CreateDirectory(dir);
            await Page.GetByTestId("modal-dialog").ScreenshotAsync(new() { Path = Path.Combine(dir, $"initial-balance-{theme}.png") });
        }
        await dialog.GetByTestId("account-settings-cal-apply").ClickAsync();
        await Expect(dialog.GetByTestId("account-settings-initial-amount")).ToHaveValueAsync("30,00");
        await Page.GetByTestId("account-settings-save").ClickAsync();
        await Expect(dialog).ToHaveCountAsync(0);

        var account = await AccountAsync(Cash);
        Assert.That(account.InitialBalance, Is.EqualTo(3000));
        Assert.That(account.CurrentBalance, Is.EqualTo(130m));
        await Expect(sidebar.GetByTestId("tree-account").Filter(new() { HasText = "Example Cash" }).First).ToContainTextAsync("130,00");
    }
}
