using Microsoft.Playwright;
using NUnit.Framework;

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
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Accounts" })).ToBeVisibleAsync();
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

    [Test]
    public async Task Deleting_a_project_returns_to_the_dashboard()
    {
        await GoToAsync(SettingsUrl);

        await Page.GetByRole(AriaRole.Button, new() { Name = "Delete Project" }).ClickAsync();

        var modal = Page.GetByTestId("modal-dialog");
        await Expect(modal.GetByText(E2ESeed.ProjectName)).ToBeVisibleAsync();
        await modal.GetByRole(AriaRole.Button, new() { Name = "Delete", Exact = true }).ClickAsync();

        // Lands back on the dashboard (its "New Project" button is the marker), and the deleted
        // project is gone from the list.
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "New Project" })).ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Link, new() { Name = E2ESeed.ProjectName })).Not.ToBeVisibleAsync();
    }
}
