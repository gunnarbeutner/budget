using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using NUnit.Framework;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.E2E.Tests;

/// <summary>
/// Covers the project settings views (/projects/{id}/settings and its accounts, categories and sharing views):
/// the left list switches views and the old anchors open the matching view, category assignments can be removed
/// and re-added through the live circuit, accounts can be added, the name can be changed, and the destructive
/// "Delete Project" flow confirms via the modal and returns to the dashboard.
/// </summary>
[TestFixture]
public sealed class ProjectSettingsTests : EndToEndTestBase
{
    private string SettingsUrl => $"/projects/{E2ESeed.ProjectId}/settings";
    private string CategoriesUrl => SettingsUrl + "/categories";
    private string AccountsUrl => SettingsUrl + "/accounts";

    // "Living" shows up in several cards; the Category Assignments row is the one carrying a
    // "Remove" button, so scope to that to avoid a strict-mode multi-match.
    private ILocator AssignmentRow(string name) =>
        Page.GetByRole(AriaRole.Row)
            .Filter(new() { HasText = name })
            .Filter(new() { Has = Page.GetByRole(AriaRole.Button, new() { Name = "Remove" }) });

    [Test]
    public async Task The_settings_list_switches_between_the_four_views()
    {
        await GoToAsync(SettingsUrl);
        var nav = Page.GetByTestId("project-settings-nav");
        await Expect(nav.GetByTestId("project-settings-nav-general")).ToHaveAttributeAsync("aria-current", "page");
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Parent project" })).ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Delete Project" })).ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Category Assignments" })).ToHaveCountAsync(0);

        await nav.GetByTestId("project-settings-nav-accounts").ClickAsync();
        await Expect(Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/settings/accounts$"));
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Accounts", Exact = true })).ToBeVisibleAsync();
        await Expect(nav.GetByTestId("project-settings-nav-accounts")).ToHaveAttributeAsync("aria-current", "page");

        await nav.GetByTestId("project-settings-nav-categories").ClickAsync();
        await Expect(Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/settings/categories$"));
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Category Assignments" })).ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Category Income/Expense Configuration" })).ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Budget Tracking Configuration" })).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("accounts-card")).ToHaveCountAsync(0);

        await nav.GetByTestId("project-settings-nav-sharing").ClickAsync();
        await Expect(Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/settings/sharing$"));
        await Expect(Page.Locator("#sharing-card")).ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Delete Project" })).ToHaveCountAsync(0);
    }

    [TestCase("#accounts-categories", "categories")]
    [TestCase("#sharing", "sharing")]
    public async Task Old_anchors_open_the_matching_view(string anchor, string view)
    {
        await GoToAsync(SettingsUrl + anchor);
        await Expect(Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex($"/settings/{view}$"));
        await Expect(Page.GetByTestId($"project-settings-view-{view}")).ToBeVisibleAsync();
    }

    [Test]
    public async Task The_general_view_saves_name_and_description_while_typing()
    {
        await GoToAsync(SettingsUrl);
        await Expect(Page.GetByTestId("project-general-saved")).ToHaveTextAsync("");
        await Page.Locator("#project-name").FillAsync("Renamed Project");
        await Expect(Page.GetByTestId("project-general-saved")).ToHaveTextAsync("Saved");
        await Expect(Page.GetByTestId("page-header")).ToContainTextAsync("Renamed Project");
        await Page.Locator("#project-description").FillAsync("Example description");
        await Page.Locator("#project-name").FocusAsync();   // leaving the field saves at once
        await Expect(Page.GetByTestId("page-header")).ToContainTextAsync("Example description");
        await Page.ReloadAsync();
        await Expect(Page.Locator("#project-name")).ToHaveValueAsync("Renamed Project");
        await Expect(Page.Locator("#project-description")).ToHaveValueAsync("Example description");
    }

    [Test]
    public async Task The_views_fit_a_phone_with_the_list_as_a_tab_row()
    {
        await Page.SetViewportSizeAsync(390, 844);
        foreach (var view in new[] { "", "/accounts", "/categories", "/sharing" })
        {
            await GoToAsync(SettingsUrl + view);
            await Expect(Page.GetByTestId("project-settings-nav-sharing")).ToBeVisibleAsync();
            var overflow = await Page.EvaluateAsync<bool>("document.documentElement.scrollWidth > document.documentElement.clientWidth");
            Assert.That(overflow, Is.False, $"{view} scrolls horizontally");
        }
        var general = await Page.GetByTestId("project-settings-nav-general").BoundingBoxAsync();
        var sharing = await Page.GetByTestId("project-settings-nav-sharing").BoundingBoxAsync();
        Assert.That(sharing!.Y, Is.EqualTo(general!.Y).Within(2), "the views sit in one row on a phone");
    }

    [Test]
    public async Task Removing_an_assigned_category_clears_the_list()
    {
        await GoToAsync(CategoriesUrl);

        // The baseline world assigns the "Living" group to the project.
        var row = AssignmentRow("Living");
        await Expect(row).ToBeVisibleAsync();
        await row.GetByRole(AriaRole.Button, new() { Name = "Remove" }).ClickAsync();

        await Expect(Page.GetByText("No categories assigned yet.")).ToBeVisibleAsync();
    }

    [Test]
    public async Task Adding_a_category_through_the_picker_shows_it_in_the_list()
    {
        await GoToAsync(CategoriesUrl);

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

    private async Task SeedAccountsAsync()
    {
        var factory = Factory.Services.GetRequiredService<SystemDbContextFactory>();
        await using var db = await factory.CreateDbContextAsync();
        db.AccountGroups.Add(new AccountGroup { Id = 20, OwnerUserId = E2ESeed.OwnerUserId, Name = "Example Bank", Sort = 1 });
        db.AccountGroups.Add(new AccountGroup { Id = 21, OwnerUserId = E2ESeed.OwnerUserId, Name = "Another Bank", Sort = 2 });
        void Add(long id, string name, long group, int sort) => db.Accounts.Add(new Account
        {
            Id = id,
            OwnerUserId = E2ESeed.OwnerUserId,
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
        await GoToAsync(AccountsUrl);

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
            await Expect(row.GetByRole(AriaRole.Button, new() { Name = "All" })).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("active"));
        }
        await Expect(Page.GetByRole(AriaRole.Row).Filter(new() { HasText = "Example Bank Card" })).ToHaveCountAsync(0);
        await Expect(Page.GetByRole(AriaRole.Row).Filter(new() { HasText = "Example Bank Giro" })
            .GetByRole(AriaRole.Button, new() { Name = "Project categories" })).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("active"));

        // Giro and savings join the cashflow; the "In cashflow" checkbox takes them out again.
        var savingsCashflow = Page.GetByRole(AriaRole.Row).Filter(new() { HasText = "Example Bank Savings" }).GetByTestId("in-cashflow");
        await Expect(savingsCashflow).ToBeCheckedAsync();
        await savingsCashflow.UncheckAsync();
        await Expect(savingsCashflow).Not.ToBeCheckedAsync();
        await Page.ReloadAsync();
        await Expect(savingsCashflow).Not.ToBeCheckedAsync();
    }

    [Test]
    public async Task Deleting_a_project_returns_to_the_dashboard()
    {
        await GoToAsync(SettingsUrl);

        await Page.GetByRole(AriaRole.Button, new() { Name = "Delete Project" }).ClickAsync();

        var modal = Page.GetByTestId("modal-dialog");
        await Expect(modal.GetByText(E2ESeed.ProjectName).First).ToBeVisibleAsync();
        await Expect(modal.GetByTestId("delete-project-confirm")).ToBeDisabledAsync();
        await modal.GetByTestId("delete-project-name").FillAsync(E2ESeed.ProjectName);
        await modal.GetByTestId("delete-project-confirm").ClickAsync();

        // Lands back on "/", which with no project left is the first-run welcome (its "New Project" button is
        // the marker), and the deleted project is gone.
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "New Project" })).ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Link, new() { Name = E2ESeed.ProjectName })).Not.ToBeVisibleAsync();
    }
}
