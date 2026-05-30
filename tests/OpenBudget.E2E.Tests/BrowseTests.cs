using System.Text.RegularExpressions;
using Microsoft.Playwright;
using NUnit.Framework;

namespace OpenBudget.E2E.Tests;

/// <summary>
/// Covers the transaction browser (/projects/{id}/browse): the seeded transactions list, the
/// search and amount filters with their clear action, and selecting a category in the left rail.
/// The baseline world seeds two transactions — an expense "txn-1" (Groceries) and income "txn-2"
/// (Salary).
/// </summary>
[TestFixture]
public sealed class BrowseTests : EndToEndTestBase
{
    // Anchor on the seeded transactions' year so the period heading and highlighting are stable.
    private string BrowseUrl => $"/projects/{E2ESeed.ProjectId}/browse?year={E2ESeed.BudgetYear}&month=1";

    [Test]
    public async Task Browse_lists_the_seeded_transactions()
    {
        await GoToAsync(BrowseUrl);

        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "All transactions" })).ToBeVisibleAsync();
        await Expect(Page.GetByText("txn-1")).ToBeVisibleAsync();
        await Expect(Page.GetByText("txn-2")).ToBeVisibleAsync();
        await Expect(Page.GetByText(new Regex("2 transactions"))).ToBeVisibleAsync();
    }

    [Test]
    public async Task Search_filter_narrows_the_list_and_clear_restores_it()
    {
        await GoToAsync(BrowseUrl);
        await Expect(Page.GetByText(new Regex("2 transactions"))).ToBeVisibleAsync();

        await Page.GetByPlaceholder("Search name or comment…").FillAsync("txn-2");

        // Debounced search resolves to a single match.
        await Expect(Page.GetByText(new Regex("1 transactions"))).ToBeVisibleAsync();
        await Expect(Page.GetByText("txn-1")).Not.ToBeVisibleAsync();
        await Expect(Page.GetByText("txn-2")).ToBeVisibleAsync();

        // The Clear button appears only with an active filter; clicking it restores the full list.
        await Page.GetByRole(AriaRole.Button, new() { Name = "Clear filters" }).ClickAsync();
        await Expect(Page.GetByText(new Regex("2 transactions"))).ToBeVisibleAsync();
    }

    [Test]
    public async Task Selecting_a_category_in_the_rail_scopes_the_view()
    {
        await GoToAsync(BrowseUrl);

        // The rail renders the tracked category tree; pick the "Groceries" leaf.
        await Page.Locator(".browse-tree-row").Filter(new() { HasText = "Groceries" }).First.ClickAsync();

        // Header reflects the selection and only the expense transaction remains.
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Groceries" })).ToBeVisibleAsync();
        await Expect(Page.GetByText("txn-1")).ToBeVisibleAsync();
        await Expect(Page.GetByText("txn-2")).Not.ToBeVisibleAsync();
    }
}
