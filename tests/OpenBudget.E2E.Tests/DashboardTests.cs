using System.Text.RegularExpressions;
using Microsoft.Playwright;
using NUnit.Framework;

namespace OpenBudget.E2E.Tests;

/// <summary>
/// Creating projects: "New project" in the switcher opens the create dialog and creates a project end-to-end
/// through the live Blazor circuit; without projects "/" shows the first-run welcome with "New Project"; the old
/// project list /projects/manage lands in the current project (or on the welcome, where ?new=1 opens the dialog).
/// </summary>
[TestFixture]
public sealed class DashboardTests : EndToEndTestBase
{
    private async Task OpenNewProjectDialogAsync()
    {
        await Page.Locator("[data-testid=project-selector] > summary").ClickAsync();
        await Page.GetByTestId("switcher-new-project").ClickAsync();
        await Expect(Page.GetByTestId("modal-dialog")).ToBeVisibleAsync();
    }

    private async Task DeleteTheOnlyProjectAsync()
    {
        await GoToAsync($"/projects/{E2ESeed.ProjectId}/settings");
        await Page.GetByRole(AriaRole.Button, new() { Name = "Delete Project" }).ClickAsync();
        await Page.GetByTestId("delete-project-name").FillAsync(E2ESeed.ProjectName);
        await Page.GetByTestId("delete-project-confirm").ClickAsync();
        await Expect(Page.GetByTestId("modal-dialog")).Not.ToBeVisibleAsync();
    }

    [Test]
    public async Task Switcher_new_project_opens_the_create_modal_and_closes_the_switcher()
    {
        await GoToAsync($"/projects/{E2ESeed.ProjectId}/browse");
        await OpenNewProjectDialogAsync();

        await Expect(Page.GetByText("Create New Project")).ToBeVisibleAsync();
        await Expect(Page.Locator("[data-testid=project-selector]")).Not.ToHaveAttributeAsync("open", "");
    }

    [Test]
    public async Task Creating_a_project_opens_it_and_adds_it_to_the_switcher()
    {
        await GoToAsync($"/projects/{E2ESeed.ProjectId}/browse");
        await OpenNewProjectDialogAsync();

        var modal = Page.GetByTestId("modal-dialog");
        await Expect(modal.GetByTestId("new-project-create")).ToBeDisabledAsync();
        await Expect(modal.GetByLabel("Name")).ToBeEditableAsync();
        await modal.GetByLabel("Name").FillAsync("Vacation Fund");
        await Expect(modal.GetByTestId("new-project-create")).ToBeEnabledAsync();
        await modal.GetByTestId("new-project-create").ClickAsync();

        await Expect(Page.GetByTestId("modal-dialog")).Not.ToBeVisibleAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(".*/projects/\\d+/browse$"));
        await Expect(Page.Locator("[data-testid=project-selector] .project-selector-name")).ToHaveTextAsync("Vacation Fund");
        await Page.Locator("[data-testid=project-selector] > summary").ClickAsync();
        await Expect(Page.GetByTestId("switcher-project").Filter(new() { HasText = "Vacation Fund" })).ToBeVisibleAsync();
    }

    [Test]
    public async Task The_old_project_list_url_lands_in_the_current_project()
    {
        await GoToAsync("/projects/manage");
        await Expect(Page).ToHaveURLAsync(new Regex($".*/projects/{E2ESeed.ProjectId}/browse$"));

        await GoToAsync("/projects/manage?new=1");
        await Expect(Page).ToHaveURLAsync(new Regex($".*/projects/{E2ESeed.ProjectId}/browse$"));
    }

    [Test]
    public async Task Without_projects_the_root_shows_the_welcome_with_New_Project()
    {
        await DeleteTheOnlyProjectAsync();

        await GoToAsync("/");

        await Expect(Page.GetByText("Welcome to OpenBudget!")).ToBeVisibleAsync();
        await Page.GetByRole(AriaRole.Button, new() { Name = "New Project" }).ClickAsync();
        await Expect(Page.GetByTestId("modal-dialog")).ToBeVisibleAsync();
        await Expect(Page.GetByText("Create New Project")).ToBeVisibleAsync();
    }

    [Test]
    public async Task Without_projects_the_old_new_project_link_opens_the_dialog_on_the_welcome()
    {
        await DeleteTheOnlyProjectAsync();

        await GoToAsync("/projects/manage?new=1");

        await Expect(Page.GetByText("Welcome to OpenBudget!")).ToBeVisibleAsync();
        await Expect(Page.GetByText("Create New Project")).ToBeVisibleAsync();
    }
}
