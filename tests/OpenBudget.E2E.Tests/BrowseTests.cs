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

        await Page.GetByPlaceholder("Search name, purpose, note or amount…").FillAsync("txn-2");

        // Debounced search resolves to a single match.
        await Expect(Page.GetByText(new Regex("1 transactions"))).ToBeVisibleAsync();
        await Expect(Page.GetByText("txn-1")).Not.ToBeVisibleAsync();
        await Expect(Page.GetByText("txn-2")).ToBeVisibleAsync();

        // The Clear button appears only with an active filter; clicking it restores the full list.
        await Page.GetByRole(AriaRole.Button, new() { Name = "Clear filters" }).ClickAsync();
        await Expect(Page.GetByText(new Regex("2 transactions"))).ToBeVisibleAsync();
    }

    /// <summary>
    /// The search field sits in the top bar; an amount finds bookings of that amount, Escape clears it. Views without a
    /// transaction list show no search field.
    /// </summary>
    [Test]
    public async Task Top_bar_search_matches_amounts_and_escape_clears_it()
    {
        await GoToAsync(BrowseUrl);
        var search = Page.GetByTestId("topbar-search").GetByTestId("tx-search");
        await Expect(search).ToBeVisibleAsync();

        await search.FillAsync("500,00 €");
        await Expect(Page.GetByText(new Regex("1 transactions"))).ToBeVisibleAsync();
        await Expect(Page.GetByText("txn-2")).ToBeVisibleAsync();
        await Expect(Page.GetByText("txn-1")).Not.ToBeVisibleAsync();

        await search.PressAsync("Escape");
        await Expect(search).ToHaveValueAsync("");
        await Expect(Page.GetByText(new Regex("2 transactions"))).ToBeVisibleAsync();

        await GoToAsync($"/projects/{E2ESeed.ProjectId}/budgets");
        await Expect(Page.GetByTestId("budget-toolbar")).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("tx-search")).ToHaveCountAsync(0);
        await Expect(Page.GetByTestId("topbar-search")).ToBeHiddenAsync();
    }

    /// <summary>
    /// At phone width the search is an icon in the top bar that opens over the bar; the page never scrolls sideways.
    /// Set OB_SEARCH_SCREENSHOT_DIR to keep screenshots of the top bar for review.
    /// </summary>
    [TestCase("light")]
    [TestCase("dark")]
    public async Task Top_bar_search_collapses_to_an_icon_on_phones(string theme)
    {
        var dir = Environment.GetEnvironmentVariable("OB_SEARCH_SCREENSHOT_DIR");
        await Page.EmulateMediaAsync(new PageEmulateMediaOptions { ColorScheme = theme == "dark" ? ColorScheme.Dark : ColorScheme.Light });

        await Page.SetViewportSizeAsync(1280, 720);
        await GoToAsync(BrowseUrl);
        await Expect(Page.GetByTestId("tx-search")).ToBeVisibleAsync();
        if (!string.IsNullOrEmpty(dir))
            await Page.ScreenshotAsync(new() { Path = Path.Combine(dir, $"search-desktop-{theme}.png") });

        await Page.SetViewportSizeAsync(390, 760);
        await GoToAsync(BrowseUrl);
        var label = Page.GetByTestId("topbar-search").Locator(".topbar-search");
        Assert.That((await label.BoundingBoxAsync())!.Width, Is.LessThanOrEqualTo(40), "collapsed to an icon");
        Assert.That(await Page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > window.innerWidth + 1"), Is.False);
        if (!string.IsNullOrEmpty(dir))
            await Page.ScreenshotAsync(new() { Path = Path.Combine(dir, $"search-phone-{theme}.png") });

        await label.ClickAsync();
        await Expect(Page.GetByTestId("tx-search")).ToBeFocusedAsync();
        await Page.GetByTestId("tx-search").FillAsync("txn");
        Assert.That((await label.BoundingBoxAsync())!.Width, Is.GreaterThan(300), "opens over the bar");
        Assert.That(await Page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > window.innerWidth + 1"), Is.False);
        if (!string.IsNullOrEmpty(dir))
            await Page.ScreenshotAsync(new() { Path = Path.Combine(dir, $"search-phone-open-{theme}.png") });
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
        await Expect(Page.GetByTestId("browse-chart")).ToBeVisibleAsync();

        // Groceries is budgeted 100 € a month; as an expense the line shares the bars' negative sign.
        await Page.WaitForFunctionAsync("() => Chart.getChart('browseChart')?.data.datasets.length === 2");
        var budget = await Page.EvaluateAsync<double[]>("() => browseChartData('browseChart').budget");
        Assert.That(budget, Has.All.EqualTo(-100d));
        var label = await Page.EvaluateAsync<string>("() => Chart.getChart('browseChart').data.datasets[1].label");
        Assert.That(label, Is.EqualTo("Budget"));
        Assert.That(await Page.EvaluateAsync<string>("() => Chart.getChart('browseChart').data.datasets[0].type ?? Chart.getChart('browseChart').config.type"), Is.EqualTo("bar"));

        // Only spending and an expense budget: the axis reaches down to them and stops at 0 on the other side.
        var scale = await Page.EvaluateAsync<double[]>("() => { const y = Chart.getChart('browseChart').scales.y; return [y.min, y.max]; }");
        Assert.That(scale[0], Is.LessThanOrEqualTo(-100d));
        Assert.That(scale[1], Is.EqualTo(0d));
        Assert.That(await Page.EvaluateAsync<bool>("() => Chart.getChart('browseChart').scales.y.ticks.some(t => t.value === 0)"), Is.True);
    }

    [Test]
    public async Task The_chart_axis_fits_the_data_on_each_side_of_zero()
    {
        await GoToAsync($"{BrowseUrl}&sel=cat-200");
        await Page.WaitForFunctionAsync("() => typeof window.browseChartAxis === 'function'");

        var positive = await Page.EvaluateAsync<double[]>("() => { const a = browseChartAxis([4800, 1200, null, 300]); return [a.min, a.max, a.step]; }");
        Assert.That(positive[0], Is.EqualTo(0d));
        Assert.That(positive[1], Is.GreaterThan(4800d).And.LessThan(5500d));
        Assert.That(positive[2], Is.EqualTo(2500d));

        var mixed = await Page.EvaluateAsync<double[]>("() => { const a = browseChartAxis([4800, -300]); return [a.min, a.max]; }");
        Assert.That(mixed[0], Is.LessThan(-300d).And.GreaterThan(-1000d));
        Assert.That(mixed[1], Is.GreaterThan(4800d).And.LessThan(5500d));
    }
}
