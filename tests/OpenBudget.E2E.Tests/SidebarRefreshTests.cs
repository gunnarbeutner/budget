using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using NUnit.Framework;
using OpenBudget.Connectors;
using OpenBudget.Connectors.Sync;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.E2E.Tests;

/// <summary>The Transactions sidebar's bank entries: "Refresh account" syncs the linked account, "Edit login data…" opens the login.</summary>
[TestFixture]
public sealed class SidebarRefreshTests : EndToEndTestBase
{
    private SystemDbContextFactory Db => Factory.Services.GetRequiredService<SystemDbContextFactory>();

    /// <summary>The editing world plus a fake-bank login of the owner whose giro is linked to the bank account.</summary>
    private async Task<int> SeedLoginAsync()
    {
        await EditingSeed.SeedAsync(Db);
        await using var db = await Db.CreateDbContextAsync();
        var owner = await db.Users.SingleAsync(u => u.UserName == E2ESeed.UserName);
        var secrets = Factory.Services.GetRequiredService<SecretsProtector>();
        var login = new BankLogin
        {
            OwnerUserId = owner.Id,
            ConnectorType = "fake",
            DisplayName = "Example Bank",
            RefreshInterval = RefreshInterval.ManualOnly,
            LastSyncAt = DateTime.UtcNow,
            EncryptedCredentials = BankSyncRunner.EncodeCredentials(secrets, "demo", "1234"),
        };
        db.BankLogins.Add(login);
        await db.SaveChangesAsync();
        db.BankLoginAccounts.Add(new BankLoginAccount
        {
            BankLoginId = login.Id,
            RemoteId = "FAKE-GIRO",
            Name = "Giro",
            LinkedAccountId = EditingSeed.BankAccountId,
            CreatedByConnector = true,
        });
        await db.SaveChangesAsync();
        return login.Id;
    }

    private ILocator Account(string name) =>
        Page.GetByTestId("project-sidebar").GetByTestId("tree-account").Filter(new() { HasText = name }).First;

    [Test]
    public async Task Refresh_account_syncs_the_linked_account()
    {
        await SeedLoginAsync();
        await using (var db = await Db.CreateDbContextAsync())
            Assert.That(await db.Transactions.CountAsync(t => t.AccountId == EditingSeed.BankAccountId), Is.EqualTo(4));

        await GoToAsync($"/projects/{E2ESeed.ProjectId}/browse?year=2026&month=3");
        await Account(EditingSeed.BankAccountName).ClickAsync(new() { Button = MouseButton.Right });
        await Page.GetByTestId("ctx-bank-refresh-account").ClickAsync();

        var deadline = DateTime.UtcNow.AddSeconds(20);
        int count;
        do
        {
            await Task.Delay(100);
            await using var db = await Db.CreateDbContextAsync();
            count = await db.Transactions.CountAsync(t => t.AccountId == EditingSeed.BankAccountId);
        }
        while (count <= 4 && DateTime.UtcNow < deadline);
        Assert.That(count, Is.GreaterThan(4));
        await using (var db = await Db.CreateDbContextAsync())
            Assert.That(await db.BankLoginAccounts.Where(a => a.RemoteId == "FAKE-GIRO").Select(a => a.LastSuccessAt).SingleAsync(), Is.Not.Null);

        // the MoneyMoney account has no bank login: no bank entries
        await Account(EditingSeed.ImportedAccountName).ClickAsync(new() { Button = MouseButton.Right });
        await Expect(Page.GetByTestId("context-menu")).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("ctx-bank-refresh-account")).ToHaveCountAsync(0);
    }

    [Test]
    public async Task Edit_login_data_opens_the_login_dialog()
    {
        await SeedLoginAsync();
        await GoToAsync($"/projects/{E2ESeed.ProjectId}/browse?year=2026&month=3");
        await Account(EditingSeed.BankAccountName).ClickAsync(new() { Button = MouseButton.Right });
        await Page.GetByTestId("ctx-bank-edit-login").ClickAsync();
        await Expect(Page.Locator("#edit-name")).ToHaveValueAsync("Example Bank");
    }
}
