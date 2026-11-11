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
    public async Task The_sidebar_can_be_resized_and_keeps_its_width()
    {
        await Page.SetViewportSizeAsync(1440, 900);
        await GoToAsync(BrowseUrl);
        var rail = Page.Locator(".browse-rail");
        var handle = Page.GetByTestId("sidebar-resizer");
        var before = (await rail.BoundingBoxAsync())!.Width;

        var box = (await handle.BoundingBoxAsync())!;
        await Page.Mouse.MoveAsync(box.X + box.Width / 2, box.Y + 200);
        await Page.Mouse.DownAsync();
        await Page.Mouse.MoveAsync(box.X + 120, box.Y + 200, new() { Steps = 5 });
        await Page.Mouse.UpAsync();
        var after = (await rail.BoundingBoxAsync())!.Width;
        Assert.That(after, Is.EqualTo(before + 120).Within(8));

        await Page.ReloadAsync();
        await Expect(Page.GetByText("txn-1")).ToBeVisibleAsync();
        Assert.That((await rail.BoundingBoxAsync())!.Width, Is.EqualTo(after).Within(2));

        await Page.GetByTestId("sidebar-resizer").DblClickAsync();
        Assert.That((await rail.BoundingBoxAsync())!.Width, Is.EqualTo(before).Within(2));
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

    [Test]
    public async Task A_budgeted_category_charts_its_budget_line()
    {
        await GoToAsync($"{BrowseUrl}&sel=cat-200");
        await Page.GetByRole(AriaRole.Button, new() { Name = "Chart" }).ClickAsync();
        await Expect(Page.GetByTestId("browse-chart")).ToBeVisibleAsync();

        // Groceries is budgeted 100 € a month; as an expense the line shares the bars' negative sign.
        await Page.WaitForFunctionAsync("() => Chart.getChart('browseChart')?.data.datasets.length === 2");
        var budget = await Page.EvaluateAsync<double[]>("() => Chart.getChart('browseChart').data.datasets[1].data");
        Assert.That(budget, Has.All.EqualTo(-100d));
        var label = await Page.EvaluateAsync<string>("() => Chart.getChart('browseChart').data.datasets[1].label");
        Assert.That(label, Is.EqualTo("Budget"));
    }
}
