using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using NUnit.Framework;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.E2E.Tests;

/// <summary>The Browse rail's accounts tree (groups with sums) and the single-account header.</summary>
[TestFixture]
public sealed class AccountTreeTests : EndToEndTestBase
{
    private const long BanksGroup = 10;
    private const long ExampleBankGroup = 11;
    private const long GiroId = 1;
    private const long SavingsId = 2;
    private const long CashId = 3;

    private async Task SeedAccountsAsync()
    {
        var factory = AppFixture.Factory.Services.GetRequiredService<SystemDbContextFactory>();
        await using var db = await factory.CreateDbContextAsync();
        db.AccountGroups.Add(new AccountGroup { Id = BanksGroup, SourceId = E2ESeed.SourceId, Name = "Banks", Sort = 1 });
        db.AccountGroups.Add(new AccountGroup { Id = ExampleBankGroup, SourceId = E2ESeed.SourceId, Name = "Example Bank", ParentGroupId = BanksGroup, Sort = 1 });
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        db.Accounts.Add(new Account
        {
            Id = GiroId,
            SourceId = E2ESeed.SourceId,
            Name = "E2E Giro",
            Iban = "DE00123456780123456789",
            Currency = "EUR",
            Type = 1,
            Balances = "11:1000.000000,3:EUR,",
            GroupId = ExampleBankGroup,
            Sort = 1,
            RefreshTimestamp = now,
        });
        db.Accounts.Add(new Account
        {
            Id = SavingsId,
            SourceId = E2ESeed.SourceId,
            Name = "E2E Savings",
            Currency = "EUR",
            Type = 2,
            Balances = "11:250.500000,3:EUR,",
            GroupId = ExampleBankGroup,
            Sort = 2,
        });
        db.Accounts.Add(new Account
        {
            Id = CashId,
            SourceId = E2ESeed.SourceId,
            Name = "E2E Cash",
            Currency = "EUR",
            Type = 6,
            Balances = "11:49.500000,3:EUR,",
            GroupId = BanksGroup,
            Sort = 3,
        });
        foreach (var id in new[] { GiroId, SavingsId, CashId })
            db.ProjectAccountAssignments.Add(new ProjectAccountAssignment { ProjectId = E2ESeed.ProjectId, AccountId = id, Scope = AccountScope.Exclusive });
        await db.SaveChangesAsync();
    }

    private static string BrowseUrl => $"/projects/{E2ESeed.ProjectId}/browse?year={E2ESeed.BudgetYear}&month=1";

    [Test]
    public async Task The_rail_shows_account_groups_with_their_sums_and_collapses_them()
    {
        await SeedAccountsAsync();
        await GoToAsync(BrowseUrl);

        var groups = Page.GetByTestId("tree-group");
        await Expect(groups).ToHaveCountAsync(2);
        await Expect(groups.Nth(0)).ToContainTextAsync("Banks");
        await Expect(groups.Nth(0).GetByTestId("tree-group-sum")).ToHaveTextAsync("€1,300.00");
        await Expect(groups.Nth(1)).ToContainTextAsync("Example Bank");
        await Expect(groups.Nth(1).GetByTestId("tree-group-sum")).ToHaveTextAsync("€1,250.50");
        await Expect(Page.GetByTestId("tree-account")).ToHaveCountAsync(3);

        await groups.Nth(1).ClickAsync();
        await Expect(Page.GetByTestId("tree-account")).ToHaveCountAsync(1);
    }

    [Test]
    public async Task An_account_header_shows_its_iban_and_when_it_was_last_updated()
    {
        await SeedAccountsAsync();
        await GoToAsync(BrowseUrl);

        await Page.GetByTestId("tree-account").Filter(new() { HasText = "E2E Giro" }).ClickAsync();

        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "E2E Giro" })).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("account-iban")).ToHaveTextAsync("DE00 1234 5678 0123 4567 89");
        await Expect(Page.GetByTestId("account-last-updated")).ToContainTextAsync("Last updated:");
        await Expect(Page.GetByTestId("account-last-updated")).ToContainTextAsync("Today");
    }
}
