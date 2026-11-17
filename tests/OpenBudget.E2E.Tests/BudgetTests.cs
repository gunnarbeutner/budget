using System.Text.RegularExpressions;
using Microsoft.Playwright;
using NUnit.Framework;

namespace OpenBudget.E2E.Tests;

/// <summary>
/// Covers the budget lifecycle through the live circuit: the 3-step creation wizard
/// (/projects/{id}/budgets/new), the monthly-amounts editor (/budgets/{id}/edit), and deleting a
/// budget from the Budgets page (/projects/{id}/budgets).
/// </summary>
[TestFixture]
public sealed class BudgetTests : EndToEndTestBase
{
    private string BudgetsUrl => $"/projects/{E2ESeed.ProjectId}/budgets";
    private string WizardUrl => $"/projects/{E2ESeed.ProjectId}/budgets/new";
    private string EditUrl => $"/budgets/{E2ESeed.BudgetId}/edit";

    [Test]
    public async Task Wizard_creates_a_budget_end_to_end()
    {
        await GoToAsync(WizardUrl);

        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Step 1: Year & Name" })).ToBeVisibleAsync();

        // Use a year without an existing budget so the new one is unambiguous.
        var yearField = Page.GetByLabel("Budget Year");
        await yearField.FillAsync("2027");
        await yearField.BlurAsync();
        var nameField = Page.GetByLabel("Name", new() { Exact = true });
        await nameField.FillAsync("E2E Wizard Budget");
        await nameField.BlurAsync();

        await Page.GetByRole(AriaRole.Button, new() { Name = "Next: Suggest amounts" }).ClickAsync();

        // Step 2: the suggested amounts with their basis per category, and a Save button.
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Step 2: Review the budget" }))
            .ToBeVisibleAsync();
        await Expect(Page.GetByTestId("budget-suggest-basis").First).ToBeVisibleAsync();
        await Page.GetByRole(AriaRole.Button, new() { Name = "Save Budget" }).ClickAsync();

        // Step 3: success screen.
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Budget Created Successfully!" }))
            .ToBeVisibleAsync();

        // The new budget shows up on the Budgets page comparison dropdown.
        await Page.GetByRole(AriaRole.Link, new() { Name = "View Budgets" }).ClickAsync();
        await Expect(Page.GetByText(new Regex("2027.*E2E Wizard Budget"))).ToHaveCountAsync(1);
    }

    [Test]
    public async Task Budget_editor_loads_and_saves()
    {
        await GoToAsync(EditUrl);

        // Metadata form is populated from the seeded budget.
        var nameField = Page.GetByLabel("Name", new() { Exact = true });
        await Expect(nameField).ToHaveValueAsync("2026");

        // The monthly-amounts table renders the seeded income/expense sections.
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Monthly amounts" })).ToBeVisibleAsync();
        await Expect(Page.Locator(".browse-main").GetByText("Salary")).ToBeVisibleAsync();

        // Rename and save: lands back on the Budgets page.
        await nameField.FillAsync("2026 (edited)");
        await nameField.BlurAsync();
        await Page.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).ClickAsync();

        await Expect(Page).ToHaveURLAsync(new Regex($".*/projects/{E2ESeed.ProjectId}/budgets$"));
        // The saved name may briefly appear twice (list entry plus the save notice); one must be visible.
        await Expect(Page.GetByText(new Regex(@"2026 \(edited\)")).First).ToBeVisibleAsync();
    }

    [Test]
    public async Task Budget_editor_repredict_dialog_confirms()
    {
        await GoToAsync(EditUrl);

        await Page.GetByRole(AriaRole.Button, new() { Name = "Re-predict" }).ClickAsync();

        var modal = Page.GetByTestId("modal-dialog");
        await Expect(modal.GetByText("Re-predict amounts")).ToBeVisibleAsync();
        // Confirm with the modal's Re-predict button; the dialog closes.
        await modal.GetByRole(AriaRole.Button, new() { Name = "Re-predict" }).ClickAsync();
        await Expect(Page.GetByTestId("modal-dialog")).Not.ToBeVisibleAsync();
    }

    [Test]
    public async Task Deleting_a_budget_removes_it_from_the_dropdown()
    {
        await GoToAsync(BudgetsUrl);

        // The active 2026 budget is auto-selected, exposing the Delete action in the "…" menu.
        await Page.GetByTestId("budget-menu").Locator("summary").ClickAsync();
        var delete = Page.GetByRole(AriaRole.Button, new() { Name = "Delete", Exact = true });
        await Expect(delete).ToBeVisibleAsync();
        await delete.ClickAsync();

        var modal = Page.GetByTestId("modal-dialog");
        await Expect(modal.GetByRole(AriaRole.Heading, new() { Name = "Delete Budget" })).ToBeVisibleAsync();
        await modal.GetByRole(AriaRole.Button, new() { Name = "Delete", Exact = true }).ClickAsync();

        await Expect(Page.GetByTestId("modal-dialog")).Not.ToBeVisibleAsync();
        // Only the "-- No Budget Comparison --" placeholder remains in the dropdown.
        await Expect(Page.GetByRole(AriaRole.Option)).ToHaveCountAsync(1);
    }

    /// <summary>
    /// Only the page scrolls vertically. On a narrow screen the amounts table scrolls sideways inside its card (the page
    /// never does) with the category column on the left; where it fits, the month row stays below the page header.
    /// </summary>
    [Test]
    public async Task Only_the_page_scrolls_vertically_and_the_month_row_stays_visible()
    {
        await Page.SetViewportSizeAsync(1100, 700);
        await GoToAsync(EditUrl);
        var wrap = Page.Locator(".budget-table-wrap");
        await Expect(wrap).ToBeVisibleAsync();
        Assert.That(await Page.Locator(".shell-page").EvaluateAsync<bool>("e => e.scrollWidth <= e.clientWidth + 1"), Is.True, "the page does not scroll sideways");
        Assert.That(await wrap.EvaluateAsync<bool>("e => e.scrollWidth > e.clientWidth"), Is.True, "the table scrolls sideways in its card");
        Assert.That(await wrap.EvaluateAsync<bool>("e => e.scrollHeight <= e.clientHeight + 1"), Is.True, "the card does not scroll vertically");
        await wrap.EvaluateAsync("e => e.scrollTo(e.scrollWidth, 0)");
        var category = (await wrap.Locator("thead th").First.BoundingBoxAsync())!;
        Assert.That(category.X, Is.EqualTo((await wrap.BoundingBoxAsync())!.X).Within(1), "the category column stays on the left");

        await Page.SetViewportSizeAsync(1600, 300);
        Assert.That(await wrap.EvaluateAsync<bool>("e => e.scrollHeight <= e.clientHeight + 1"), Is.True, "the card does not scroll vertically");
        var toolbar = (await Page.Locator(".shell-page .page-toolbar").First.BoundingBoxAsync())!;
        // Scroll the page until the table's top is 40px under the page header: its first rows are hidden there.
        var top = (await wrap.BoundingBoxAsync())!.Y;
        await wrap.HoverAsync();
        await Page.Mouse.WheelAsync(0, top - (toolbar.Y + toolbar.Height) + 40);
        Assert.That((await wrap.BoundingBoxAsync())!.Y, Is.LessThan(toolbar.Y + toolbar.Height - 20), "the page scrolled the table under its header");
        var head = (await wrap.Locator("thead th").Nth(1).BoundingBoxAsync())!;
        Assert.That(head.Y, Is.EqualTo(toolbar.Y + toolbar.Height).Within(2), "the month row stays below the page header");
    }

    /// <summary>On a laptop-sized window the twelve months, the total and the categories fit beside the sidebar.</summary>
    [Test]
    public async Task The_amounts_table_fits_a_laptop_screen()
    {
        await Page.SetViewportSizeAsync(1440, 900);
        await GoToAsync(EditUrl);
        var wrap = Page.Locator(".budget-table-wrap");
        await Expect(wrap).ToBeVisibleAsync();
        Assert.That(await wrap.EvaluateAsync<bool>("e => e.scrollWidth <= e.clientWidth + 1"), Is.True, "no sideways scrolling");
        var input = (await wrap.Locator(".amount-input").First.BoundingBoxAsync())!;
        Assert.That(input.Width, Is.GreaterThanOrEqualTo(64), "amounts such as 1316.59 still fit");
    }
}
