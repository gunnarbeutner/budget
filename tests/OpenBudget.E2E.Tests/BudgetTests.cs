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
}
