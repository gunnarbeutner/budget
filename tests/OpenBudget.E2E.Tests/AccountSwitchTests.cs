using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.E2E.Tests;

/// <summary>
/// The follow-up of Get started's MoneyMoney step: switching all linked accounts of a source with a preview, and finishing the
/// MoneyMoney import (and resuming it).
/// </summary>
[TestFixture]
public sealed class AccountSwitchTests : EndToEndTestBase
{
    private const long GiroId = 1;

    private SystemDbContextFactory Db => Factory.Services.GetRequiredService<SystemDbContextFactory>();

    /// <summary>A MoneyMoney giro of the demo source with one booking, linked (not yet switched) to the owner's fake-bank login.</summary>
    private async Task SeedLinkedGiroAsync()
    {
        await using var db = await Db.CreateDbContextAsync();
        var owner = await db.Users.SingleAsync(u => u.UserName == E2ESeed.UserName);
        (await db.UserDataStates.SingleAsync(s => s.UserId == E2ESeed.OwnerUserId)).LastUploadAt = DateTime.UtcNow;
        db.Accounts.Add(new Account { Id = GiroId, OwnerUserId = E2ESeed.OwnerUserId, Name = "E2E Giro", Currency = "EUR", Type = 1 });
        db.Transactions.Add(new Transaction
        {
            Id = 900001,
            OwnerUserId = E2ESeed.OwnerUserId,
            AccountId = GiroId,
            Amount = -80000,
            Currency = "EUR",
            Timestamp = new DateTimeOffset(2026, 10, 3, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(),
            Name = "VERMIETER",
            Purpose = "MIETE OKTOBER",
        });
        var login = new BankLogin { OwnerUserId = owner.Id, ConnectorType = "fake", DisplayName = "Example Bank", RefreshInterval = RefreshInterval.Never };
        db.BankLogins.Add(login);
        await db.SaveChangesAsync();
        db.BankLoginAccounts.Add(new BankLoginAccount { BankLoginId = login.Id, RemoteId = "FAKE-GIRO", Name = "Giro", LinkedAccountId = GiroId });
        await db.SaveChangesAsync();
    }

    [Test]
    public async Task Switch_all_linked_accounts_then_finish_and_resume_the_import()
    {
        await SeedLinkedGiroAsync();
        await GoToAsync("/get-started");

        var section = Page.GetByTestId("step-moneymoney");
        await Expect(section.GetByTestId("switch-progress")).ToContainTextAsync("0 of 1");
        await section.GetByTestId("switch-all").ClickAsync();

        // Not synced since linking: the preview says so and shows the cutover.
        var dialog = Page.GetByTestId("switch-dialog");
        await Expect(dialog.Locator($"[data-switch-account='{GiroId}']")).ToContainTextAsync("2026-10-03");
        await Expect(dialog.GetByTestId("switch-not-fetched")).ToBeVisibleAsync();
        await Page.GetByTestId("switch-confirm").ClickAsync();
        await Expect(dialog).ToHaveCountAsync(0);
        await Expect(section.GetByTestId("switch-progress")).ToContainTextAsync("1 of 1");

        await using (var db = await Db.CreateDbContextAsync())
            Assert.That((await db.Accounts.AsNoTracking().SingleAsync(a => a.Id == GiroId)).Provider, Is.EqualTo("fake"));

        await section.GetByTestId("import-finish").ClickAsync();
        await Page.GetByTestId("import-finish-confirm").ClickAsync();
        await Expect(section.GetByTestId("import-finished")).ToBeVisibleAsync();
        await using (var db = await Db.CreateDbContextAsync())
            Assert.That((await db.UserDataStates.AsNoTracking().SingleAsync(s => s.UserId == E2ESeed.OwnerUserId)).MoneyMoneyImportFinishedAt, Is.Not.Null);

        await section.GetByTestId("import-resume").ClickAsync();
        await Expect(section.GetByTestId("import-finish")).ToBeVisibleAsync();
    }
}
