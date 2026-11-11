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
    public async Task Keep_accounts_as_manual_in_bulk_then_close_and_reopen_one()
    {
        await SeedAsync();
        await GoToAsync("/sources");

        var section = Page.Locator("[data-source-switch='legacy']");
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

        await manual.Locator($"[data-manual-account='{Old}']").GetByTestId("manual-account-close").ClickAsync();
        var closed = section.GetByTestId("closed-accounts");
        await Expect(closed.Locator($"[data-closed-account='{Old}']")).ToBeVisibleAsync();
        Assert.That((await AccountAsync(Old)).Closed, Is.True);

        await closed.Locator($"[data-closed-account='{Old}']").GetByTestId("closed-account-reopen").ClickAsync();
        await Expect(section.GetByTestId("closed-accounts")).ToHaveCountAsync(0);
        Assert.That((await AccountAsync(Old)).Closed, Is.False);
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
}
