using System.Text.RegularExpressions;
using Microsoft.Playwright;
using NUnit.Framework;

namespace OpenBudget.E2E.Tests;

/// <summary>
/// Smoke tests for the project list that replaced the start page (/projects/manage, "Reorder and manage
/// projects…" in the switcher): the seeded project shows up, and the "New Project" modal opens and creates a
/// project end-to-end through the live Blazor circuit. Also the first-run welcome "/" shows without projects.
/// </summary>
[TestFixture]
public sealed class DashboardTests : EndToEndTestBase
{
    [Test]
    public async Task Dashboard_lists_the_seeded_project()
    {
        await GoToAsync("/projects/manage");

        await Expect(Page.GetByRole(AriaRole.Link, new() { Name = E2ESeed.ProjectName }))
            .ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "New Project" }))
            .ToBeVisibleAsync();
    }

    [Test]
    public async Task New_Project_button_opens_the_create_modal()
    {
        await GoToAsync("/projects/manage");

        await Page.GetByRole(AriaRole.Button, new() { Name = "New Project" }).ClickAsync();

        await Expect(Page.GetByTestId("modal-dialog")).ToBeVisibleAsync();
        await Expect(Page.GetByText("Create New Project")).ToBeVisibleAsync();
    }

    [Test]
    public async Task Creating_a_project_adds_it_to_the_dashboard()
    {
        await GoToAsync("/projects/manage");
        await Page.GetByRole(AriaRole.Button, new() { Name = "New Project" }).ClickAsync();

        var modal = Page.GetByTestId("modal-dialog");
        var nameField = modal.GetByLabel("Name");
        await nameField.FillAsync("Vacation Fund");
        // Blazor's default @bind commits on the change event (blur), not on input; without a blur
        // the bound name stays empty and the Create button stays disabled.
        await nameField.BlurAsync();
        await modal.GetByRole(AriaRole.Button, new() { Name = "Create" }).ClickAsync();

        // Modal closes and the new project opens; it is in the switcher and the project list.
        await Expect(Page.GetByTestId("modal-dialog")).Not.ToBeVisibleAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(".*/projects/\\d+/browse$"));
        await Expect(Page.Locator("[data-testid=project-selector] .project-selector-name")).ToHaveTextAsync("Vacation Fund");
        await GoToAsync("/projects/manage");
        await Expect(Page.GetByRole(AriaRole.Link, new() { Name = "Vacation Fund" }))
            .ToBeVisibleAsync();
    }

    [Test]
    public async Task Clicking_a_project_navigates_to_its_transactions()
    {
        await GoToAsync("/projects/manage");

        await Page.GetByRole(AriaRole.Link, new() { Name = E2ESeed.ProjectName }).ClickAsync();

        await Expect(Page).ToHaveURLAsync(new Regex($".*/projects/{E2ESeed.ProjectId}/browse.*"));
    }

    [Test]
    public async Task Switcher_new_project_opens_the_create_modal()
    {
        await GoToAsync($"/projects/{E2ESeed.ProjectId}/browse");

        await Page.Locator("[data-testid=project-selector] > summary").ClickAsync();
        await Page.GetByTestId("switcher-new-project").ClickAsync();

        await Expect(Page.GetByTestId("modal-dialog")).ToBeVisibleAsync();
        await Expect(Page.GetByText("Create New Project")).ToBeVisibleAsync();
    }

    [Test]
    public async Task Without_projects_the_root_shows_the_welcome_with_New_Project()
    {
        // Delete the only project through its settings page.
        await GoToAsync($"/projects/{E2ESeed.ProjectId}/settings");
        await Page.GetByRole(AriaRole.Button, new() { Name = "Delete Project" }).ClickAsync();
        await Page.GetByTestId("modal-dialog").GetByRole(AriaRole.Button, new() { Name = "Delete", Exact = true }).ClickAsync();
        await Expect(Page.GetByTestId("modal-dialog")).Not.ToBeVisibleAsync();

        await GoToAsync("/");

        await Expect(Page.GetByText("Welcome to OpenBudget!")).ToBeVisibleAsync();
        await Page.GetByRole(AriaRole.Link, new() { Name = "New Project" }).ClickAsync();
        await Expect(Page.GetByTestId("modal-dialog")).ToBeVisibleAsync();
    }
}
