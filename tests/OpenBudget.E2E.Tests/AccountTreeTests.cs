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
        var factory = Factory.Services.GetRequiredService<SystemDbContextFactory>();
        await using var db = await factory.CreateDbContextAsync();
        db.AccountGroups.Add(new AccountGroup { Id = BanksGroup, OwnerUserId = E2ESeed.OwnerUserId, Name = "Banks", Sort = 1 });
        db.AccountGroups.Add(new AccountGroup { Id = ExampleBankGroup, OwnerUserId = E2ESeed.OwnerUserId, Name = "Example Bank", ParentGroupId = BanksGroup, Sort = 1 });
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        db.Accounts.Add(new Account
        {
            Id = GiroId,
            OwnerUserId = E2ESeed.OwnerUserId,
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
            OwnerUserId = E2ESeed.OwnerUserId,
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
            OwnerUserId = E2ESeed.OwnerUserId,
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
        await Expect(groups.Nth(0).GetByTestId("tree-group-sum")).ToHaveTextAsync("1.300,00\u00A0€");
        await Expect(groups.Nth(1)).ToContainTextAsync("Example Bank");
        await Expect(groups.Nth(1).GetByTestId("tree-group-sum")).ToHaveTextAsync("1.250,50\u00A0€");
        await Expect(Page.GetByTestId("tree-account")).ToHaveCountAsync(3);

        // The chevron folds the group without selecting it.
        await groups.Nth(1).GetByTestId("tree-group-toggle").ClickAsync();
        await Expect(Page.GetByTestId("tree-account")).ToHaveCountAsync(1);
        await Expect(groups.Nth(1)).ToHaveAttributeAsync("aria-selected", "false");
        await Expect(Page).Not.ToHaveURLAsync(new System.Text.RegularExpressions.Regex("sel="));
        await groups.Nth(1).GetByTestId("tree-group-toggle").ClickAsync();
        await Expect(Page.GetByTestId("tree-account")).ToHaveCountAsync(3);
    }

    private async Task SeedGroupBookingsAsync()
    {
        var factory = Factory.Services.GetRequiredService<SystemDbContextFactory>();
        await using var db = await factory.CreateDbContextAsync();
        void Add(long id, long account, long cents, int day, string name) => db.Transactions.Add(new Transaction
        {
            Id = id,
            OwnerUserId = E2ESeed.OwnerUserId,
            AccountId = account,
            Amount = cents,
            Currency = "EUR",
            Name = name,
            Booked = true,
            Timestamp = ((DateTimeOffset)new DateTime(E2ESeed.BudgetYear, 1, day, 9, 0, 0, DateTimeKind.Utc)).ToUnixTimeSeconds(),
        });
        Add(70, GiroId, -100_00, 10, "Example Giro Booking");
        Add(71, SavingsId, 50_00, 12, "Example Savings Booking");
        Add(72, CashId, -20_00, 11, "Example Cash Booking");
        await db.SaveChangesAsync();
    }

    [Test]
    public async Task Selecting_an_account_group_lists_its_accounts_bookings_with_the_group_balance()
    {
        await SeedAccountsAsync();
        await SeedGroupBookingsAsync();
        await GoToAsync(BrowseUrl);

        var group = Page.GetByTestId("tree-group").Nth(1);
        await group.Locator(".browse-tree-label").ClickAsync();

        await Expect(Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex($"sel=g{ExampleBankGroup}"));
        await Expect(group).ToHaveAttributeAsync("aria-selected", "true");
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Example Bank" })).ToBeVisibleAsync();

        // Bookings of both member accounts (not the Cash account of the parent group), each with its account.
        var rows = Page.GetByTestId("tx-row");
        await Expect(rows).ToHaveCountAsync(2);
        await Expect(rows.Nth(0)).ToContainTextAsync("Example Savings Booking");
        await Expect(rows.Nth(0).GetByTestId("tx-account")).ToContainTextAsync("E2E Savings");
        await Expect(rows.Nth(1)).ToContainTextAsync("Example Giro Booking");

        // The toolbar total is the tree's group sum; the day rows carry the whole group's end-of-day balance.
        await Expect(Page.GetByTestId("browse-balance")).ToHaveTextAsync("1.250,50\u00A0€");
        await Expect(group.GetByTestId("tree-group-sum")).ToHaveTextAsync("1.250,50\u00A0€");
        var balances = Page.GetByTestId("tx-day-balance");
        await Expect(balances.Nth(0)).ToHaveTextAsync("1.250,50\u00A0€");
        await Expect(balances.Nth(1)).ToHaveTextAsync("1.200,50\u00A0€");

        // The keyboard: Enter selects, ArrowLeft folds.
        await group.FocusAsync();
        await Page.Keyboard.PressAsync("ArrowLeft");
        await Expect(group).ToHaveAttributeAsync("aria-expanded", "false");
        await Page.Keyboard.PressAsync("ArrowRight");
        await Expect(group).ToHaveAttributeAsync("aria-expanded", "true");
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

    [Test]
    public async Task The_account_header_copies_bank_details_iban_and_balance()
    {
        await Page.Context.GrantPermissionsAsync(["clipboard-read", "clipboard-write"], new() { Origin = Factory.BaseUrl.TrimEnd('/') });
        await SeedAccountsAsync();
        await GoToAsync(BrowseUrl);
        await Page.GetByTestId("tree-account").Filter(new() { HasText = "E2E Giro" }).ClickAsync();
        await Expect(Page.GetByTestId("account-iban")).ToBeVisibleAsync();
        async Task<string> Clipboard(string expected)
        {
            var text = "";
            for (var i = 0; i < 50 && !text.Contains(expected, StringComparison.Ordinal); i++)
            {
                text = await Page.EvaluateAsync<string>("() => navigator.clipboard.readText()");
                if (!text.Contains(expected, StringComparison.Ordinal)) await Task.Delay(100);
            }
            return text;
        }

        await Page.GetByTestId("browse-ident").ClickAsync(new() { Button = MouseButton.Right });
        var menu = Page.GetByTestId("context-menu");
        await Expect(menu.GetByTestId("ctx-copy-heading")).ToBeDisabledAsync();
        await menu.GetByTestId("ctx-copy-balance").ClickAsync();
        await Expect(Page.GetByTestId("toast").First).ToContainTextAsync("Copied");
        Assert.That(await Clipboard("1.000,00"), Is.EqualTo("1.000,00\u00A0€"));

        await Page.GetByTestId("browse-ident").ClickAsync(new() { Button = MouseButton.Right });
        await menu.GetByTestId("ctx-copy-bank-details").ClickAsync();
        Assert.That(await Clipboard("DE00 1234"), Does.StartWith("DE00 1234 5678 0123 4567 89"));

        await Page.GetByTestId("browse-ident").ClickAsync(new() { Button = MouseButton.Right });
        await menu.GetByTestId("ctx-copy-iban").ClickAsync();
        Assert.That(await Clipboard("DE0012345678"), Is.EqualTo("DE00123456780123456789"));

        await Page.EvaluateAsync("() => navigator.clipboard.writeText('')");
        await Page.GetByTestId("account-iban").ClickAsync();
        Assert.That(await Clipboard("DE0012345678"), Is.EqualTo("DE00123456780123456789"));
    }
}
