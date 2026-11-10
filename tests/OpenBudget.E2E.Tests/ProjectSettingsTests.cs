using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using NUnit.Framework;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.E2E.Tests;

/// <summary>
/// Covers the project settings page (/projects/{id}/settings): the configuration cards render,
/// category assignments can be removed and re-added through the live circuit, and the destructive
/// "Delete Project" flow confirms via the modal and returns to the dashboard.
/// </summary>
[TestFixture]
public sealed class ProjectSettingsTests : EndToEndTestBase
{
    private string SettingsUrl => $"/projects/{E2ESeed.ProjectId}/settings";

    // "Living" shows up in several cards; the Category Assignments row is the one carrying a
    // "Remove" button, so scope to that to avoid a strict-mode multi-match.
    private ILocator AssignmentRow(string name) =>
        Page.GetByRole(AriaRole.Row)
            .Filter(new() { HasText = name })
            .Filter(new() { Has = Page.GetByRole(AriaRole.Button, new() { Name = "Remove" }) });

    [Test]
    public async Task Settings_page_shows_all_configuration_cards()
    {
        await GoToAsync(SettingsUrl);

        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Parent project" })).ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Category Assignments" })).ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Accounts", Exact = true })).ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Category Income/Expense Configuration" })).ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Budget Tracking Configuration" })).ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Delete Project" })).ToBeVisibleAsync();
    }

    [Test]
    public async Task Removing_an_assigned_category_clears_the_list()
    {
        await GoToAsync(SettingsUrl);

        // The baseline world assigns the "Living" group to the project.
        var row = AssignmentRow("Living");
        await Expect(row).ToBeVisibleAsync();
        await row.GetByRole(AriaRole.Button, new() { Name = "Remove" }).ClickAsync();

        await Expect(Page.GetByText("No categories assigned yet.")).ToBeVisibleAsync();
    }

    [Test]
    public async Task Adding_a_category_through_the_picker_shows_it_in_the_list()
    {
        await GoToAsync(SettingsUrl);

        // Start from a clean slate so the re-add is unambiguous.
        await AssignmentRow("Living").GetByRole(AriaRole.Button, new() { Name = "Remove" }).ClickAsync();
        await Expect(Page.GetByText("No categories assigned yet.")).ToBeVisibleAsync();

        await Page.GetByRole(AriaRole.Button, new() { Name = "Add Category" }).ClickAsync();

        var modal = Page.GetByTestId("modal-dialog");
        await Expect(modal.GetByText("Add Category to Project")).ToBeVisibleAsync();

        // The confirm button is disabled until a tree node is picked.
        var confirm = modal.GetByRole(AriaRole.Button, new() { Name = "Add Category" });
        await Expect(confirm).ToBeDisabledAsync();

        await modal.GetByRole(AriaRole.Button).Filter(new() { HasText = "Living" }).ClickAsync();
        await Expect(confirm).ToBeEnabledAsync();
        await confirm.ClickAsync();

        await Expect(Page.GetByTestId("modal-dialog")).Not.ToBeVisibleAsync();
        await Expect(AssignmentRow("Living")).ToBeVisibleAsync();
    }

    private static async Task SeedAccountsAsync()
    {
        var factory = AppFixture.Factory.Services.GetRequiredService<SystemDbContextFactory>();
        await using var db = await factory.CreateDbContextAsync();
        db.AccountGroups.Add(new AccountGroup { Id = 20, SourceId = E2ESeed.SourceId, Name = "Example Bank", Sort = 1 });
        db.AccountGroups.Add(new AccountGroup { Id = 21, SourceId = E2ESeed.SourceId, Name = "Another Bank", Sort = 2 });
        void Add(long id, string name, long group, int sort) => db.Accounts.Add(new Account
        {
            Id = id,
            SourceId = E2ESeed.SourceId,
            Name = name,
            Currency = "EUR",
            Type = 1,
            Balances = "11:100.000000,3:EUR,",
            GroupId = group,
            Sort = sort,
        });
        Add(31, "Example Bank Giro", 20, 1);
        Add(32, "Example Bank Savings", 20, 2);
        Add(33, "Example Bank Card", 20, 3);
        Add(34, "Another Bank Giro", 21, 1);
        db.ProjectAccountAssignments.Add(new ProjectAccountAssignment { ProjectId = E2ESeed.ProjectId, AccountId = 31, Scope = AccountScope.Shared });
        await db.SaveChangesAsync();
    }

    [Test]
    public async Task Several_accounts_can_be_added_at_once()
    {
        await SeedAccountsAsync();
        await GoToAsync(SettingsUrl);

        await Page.GetByRole(AriaRole.Button, new() { Name = "Add Account" }).ClickAsync();
        var modal = Page.GetByTestId("modal-dialog");
        await Expect(modal.GetByText("Add Account to Project")).ToBeVisibleAsync();

        var confirm = modal.GetByTestId("add-accounts-confirm");
        await Expect(confirm).ToHaveTextAsync("Add accounts");
        await Expect(confirm).ToBeDisabledAsync();

        // The already-assigned account is listed but checked off and disabled.
        var giro = modal.GetByTestId("add-account-option").Filter(new() { HasText = "Example Bank Giro" });
        await Expect(giro).ToBeDisabledAsync();
        await Expect(giro).ToContainTextAsync("already added");

        // The group row selects all its not-yet-added accounts; unticking one makes it mixed.
        var group = modal.GetByTestId("add-account-group").Filter(new() { HasText = "Example Bank" });
        await group.ClickAsync();
        await Expect(group).ToHaveAttributeAsync("aria-checked", "true");
        await Expect(confirm).ToHaveTextAsync("Add 2 accounts");
        await modal.GetByTestId("add-account-option").Filter(new() { HasText = "Example Bank Card" }).ClickAsync();
        await Expect(group).ToHaveAttributeAsync("aria-checked", "mixed");
        await Expect(confirm).ToHaveTextAsync("Add 1 account");

        // The filter narrows the list; selection survives filtering.
        await modal.GetByTestId("add-account-filter").FillAsync("another");
        await Expect(modal.GetByTestId("add-account-option")).ToHaveCountAsync(1);
        await modal.GetByTestId("add-account-option").ClickAsync();
        await Expect(confirm).ToHaveTextAsync("Add 2 accounts");
        await modal.GetByTestId("add-account-filter").FillAsync("");
        await Expect(modal.GetByTestId("add-account-option")).ToHaveCountAsync(4);

        await modal.Locator("label[for=addAccountScopeExclusive]").ClickAsync();
        await confirm.ClickAsync();
        await Expect(Page.GetByTestId("modal-dialog")).Not.ToBeVisibleAsync();

        foreach (var name in new[] { "Example Bank Savings", "Another Bank Giro" })
        {
            var row = Page.GetByRole(AriaRole.Row).Filter(new() { HasText = name });
            await Expect(row.GetByRole(AriaRole.Button, new() { Name = "Exclusive" })).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("active"));
        }
        await Expect(Page.GetByRole(AriaRole.Row).Filter(new() { HasText = "Example Bank Card" })).ToHaveCountAsync(0);
        await Expect(Page.GetByRole(AriaRole.Row).Filter(new() { HasText = "Example Bank Giro" })
            .GetByRole(AriaRole.Button, new() { Name = "Shared" })).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("active"));
    }

    [Test]
    public async Task Deleting_a_project_returns_to_the_dashboard()
    {
        await GoToAsync(SettingsUrl);

        await Page.GetByRole(AriaRole.Button, new() { Name = "Delete Project" }).ClickAsync();

        var modal = Page.GetByTestId("modal-dialog");
        await Expect(modal.GetByText(E2ESeed.ProjectName)).ToBeVisibleAsync();
        await modal.GetByRole(AriaRole.Button, new() { Name = "Delete", Exact = true }).ClickAsync();

        // Lands back on "/", which with no project left is the first-run welcome (its "New Project" link is
        // the marker), and the deleted project is gone.
        await Expect(Page.GetByRole(AriaRole.Link, new() { Name = "New Project" })).ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Link, new() { Name = E2ESeed.ProjectName })).Not.ToBeVisibleAsync();
    }
}
