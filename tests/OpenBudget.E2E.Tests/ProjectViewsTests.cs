using System.Text.RegularExpressions;
using Microsoft.Playwright;
using NUnit.Framework;

namespace OpenBudget.E2E.Tests;

/// <summary>
/// Covers the remaining read/interaction surfaces of a project: the
/// Cashflow projection page (controls, what-if scenarios, contribution splits), and the Budgets
/// trends/variance page. All anchored on the seeded baseline world.
/// </summary>
[TestFixture]
public sealed class ProjectViewsTests : EndToEndTestBase
{
    private int Pid => E2ESeed.ProjectId;

    // ---- Cashflow -------------------------------------------------------------------------

    [Test]
    public async Task Cashflow_renders_projection_controls_and_summary()
    {
        await GoToAsync($"/projects/{Pid}/cashflow");

        await Expect(Page.GetByText("From budget")).ToBeVisibleAsync();
        await Expect(Page.GetByText("Pace to break even")).ToBeVisibleAsync();
        await Expect(Page.GetByText("Repeat last month")).ToBeVisibleAsync();

        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Total Income" })).ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Total Expenses" })).ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Net Cashflow" })).ToBeVisibleAsync();

        // No accounts are assigned in the baseline world, so the balance card shows its notice.
        await Expect(Page.GetByText(new Regex("No accounts assigned"))).ToBeVisibleAsync();
    }

    [Test]
    public async Task Cashflow_what_if_row_can_be_added_and_removed()
    {
        await GoToAsync($"/projects/{Pid}/cashflow");

        await Page.GetByRole(AriaRole.Button, new() { Name = "Add" }).ClickAsync();
        var remove = Page.GetByRole(AriaRole.Button, new() { Name = "Remove" });
        await Expect(remove).ToHaveCountAsync(1);

        await remove.ClickAsync();
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Remove" })).ToHaveCountAsync(0);
    }

    [Test]
    public async Task Cashflow_contribution_split_save_clears_the_dirty_state()
    {
        await GoToAsync($"/projects/{Pid}/cashflow");

        // The split percentage input is the only number field bounded to 0–100.
        var split = Page.Locator("input[type='number'][max='100']");
        await Expect(split).ToBeVisibleAsync();
        await split.FillAsync("50");
        await split.BlurAsync();

        await Expect(Page.GetByText("unsaved changes")).ToBeVisibleAsync();
        await Page.GetByRole(AriaRole.Button, new() { Name = "Save splits" }).ClickAsync();
        await Expect(Page.GetByText("unsaved changes")).Not.ToBeVisibleAsync();
    }

    // ---- Budgets --------------------------------------------------------------------------

    [Test]
    public async Task Budgets_shows_variance_against_the_active_budget()
    {
        await GoToAsync($"/projects/{Pid}/budgets");

        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Category Trends" })).ToBeVisibleAsync();
        // The active 2026 budget is auto-selected as the comparison, so the variance table shows.
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Budget vs Actual Variance" }))
            .ToBeVisibleAsync();
    }

    [Test]
    public async Task Budgets_categories_panel_expands_on_click()
    {
        await GoToAsync($"/projects/{Pid}/budgets");

        // The Categories panel is collapsed until its header is clicked.
        await Expect(Page.GetByText("How to use:")).Not.ToBeVisibleAsync();
        await Page.GetByRole(AriaRole.Button, new() { Name = "Categories" }).ClickAsync();
        await Expect(Page.GetByText("How to use:")).ToBeVisibleAsync();
    }
}
