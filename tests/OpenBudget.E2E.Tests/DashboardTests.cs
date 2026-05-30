using System.Text.RegularExpressions;
using Microsoft.Playwright;
using NUnit.Framework;

namespace OpenBudget.E2E.Tests;

/// <summary>
/// Smoke tests for the dashboard (Index) page: the seeded project shows up, and the
/// "New Project" modal opens and creates a project end-to-end through the live Blazor circuit.
/// </summary>
[TestFixture]
public sealed class DashboardTests : EndToEndTestBase
{
    [Test]
    public async Task Dashboard_lists_the_seeded_project()
    {
        await GoToAsync("/");

        await Expect(Page.GetByRole(AriaRole.Link, new() { Name = E2ESeed.ProjectName }))
            .ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "New Project" }))
            .ToBeVisibleAsync();
    }

    [Test]
    public async Task New_Project_button_opens_the_create_modal()
    {
        await GoToAsync("/");

        await Page.GetByRole(AriaRole.Button, new() { Name = "New Project" }).ClickAsync();

        await Expect(Page.GetByTestId("modal-dialog")).ToBeVisibleAsync();
        await Expect(Page.GetByText("Create New Project")).ToBeVisibleAsync();
    }

    [Test]
    public async Task Creating_a_project_adds_it_to_the_dashboard()
    {
        await GoToAsync("/");
        await Page.GetByRole(AriaRole.Button, new() { Name = "New Project" }).ClickAsync();

        var modal = Page.GetByTestId("modal-dialog");
        var nameField = modal.GetByLabel("Name");
        await nameField.FillAsync("Vacation Fund");
        // Blazor's default @bind commits on the change event (blur), not on input; without a blur
        // the bound name stays empty and the Create button stays disabled.
        await nameField.BlurAsync();
        await modal.GetByRole(AriaRole.Button, new() { Name = "Create" }).ClickAsync();

        // Modal closes and the new project appears as a row link.
        await Expect(Page.GetByTestId("modal-dialog")).Not.ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Link, new() { Name = "Vacation Fund" }))
            .ToBeVisibleAsync();
    }

    [Test]
    public async Task Clicking_a_project_navigates_to_its_overview()
    {
        await GoToAsync("/");

        await Page.GetByRole(AriaRole.Link, new() { Name = E2ESeed.ProjectName }).ClickAsync();

        await Expect(Page).ToHaveURLAsync(new Regex($".*/projects/{E2ESeed.ProjectId}/overview.*"));
    }
}
