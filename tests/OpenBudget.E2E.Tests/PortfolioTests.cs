using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using NUnit.Framework;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.E2E.Tests;

/// <summary>
/// The Portfolio view (Positions, Rebalance, Tax as a segmented control in the toolbar), the old Rebalance and
/// source-level depot / tax URLs leading there, and the project switcher keeping the current view.
/// </summary>
[TestFixture]
public sealed class PortfolioTests : EndToEndTestBase
{
    private static int Pid => E2ESeed.ProjectId;
    private static readonly long DepotId = RowIds.Namespace(E2ESeed.IdSpace, 7);

    private SystemDbContextFactory Db => Factory.Services.GetRequiredService<SystemDbContextFactory>();

    private async Task AddDepotAsync()
    {
        await using var db = await Db.CreateDbContextAsync();
        db.Accounts.Add(new Account { Id = DepotId, OwnerUserId = E2ESeed.OwnerUserId, Name = "Example Depot", Type = 7, Currency = "EUR" });
        await db.SaveChangesAsync();
        db.ProjectAccountAssignments.Add(new ProjectAccountAssignment { ProjectId = Pid, AccountId = DepotId });
        db.Holdings.Add(new Holding
        {
            Id = 1,
            OwnerUserId = E2ESeed.OwnerUserId,
            AccountId = DepotId,
            Name = "Example World Fund",
            Isin = "XX0000000001",
            Quantity = 10,
            Amount = 1234.56,
            AmountCurrency = "EUR",
            Price = 123.456,
            PriceCurrency = "EUR",
        });
        await db.SaveChangesAsync();
    }

    private async Task<int> AddSecondProjectAsync()
    {
        await using var db = await Db.CreateDbContextAsync();
        var first = await db.Projects.SingleAsync(p => p.Id == Pid);
        var second = new Project { Name = "Second Example Project", OwnerUserId = first.OwnerUserId, SortOrder = first.SortOrder + 1 };
        db.Projects.Add(second);
        await db.SaveChangesAsync();
        return second.Id;
    }

    private ILocator Tab(string key) => Page.GetByTestId($"portfolio-tab-{key}");

    [Test]
    public async Task Portfolio_has_positions_rebalance_and_tax_sections()
    {
        await AddDepotAsync();
        await GoToAsync($"/projects/{Pid}/portfolio");

        await Expect(Tab("positions")).ToHaveAttributeAsync("aria-current", "page");
        await Expect(Page.Locator(".holdings-card")).ToContainTextAsync("Example World Fund");
        await Expect(Page.GetByTestId("portfolio-total")).ToHaveTextAsync("1.234,56 €");
        await Expect(Page.GetByTestId("portfolio-links")).ToBeVisibleAsync();   // the owner links PP depots here

        await Tab("rebalance").ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(@"/portfolio\?tab=rebalance$"));
        await Expect(Tab("rebalance")).ToHaveAttributeAsync("aria-current", "page");
        await Expect(Page.Locator("#cashInput")).ToBeVisibleAsync();

        await Tab("tax").ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(@"/portfolio\?tab=tax$"));
        await Expect(Page.GetByTestId("portfolio-tax-note")).ToContainTextAsync("owner");
        await Expect(Page.GetByText("No securities with reconstructed lots")).ToBeVisibleAsync();
    }

    [Test]
    public async Task The_old_rebalance_url_opens_the_rebalance_section()
    {
        await AddDepotAsync();
        await GoToAsync($"/projects/{Pid}/rebalance");

        await Expect(Page).ToHaveURLAsync(new Regex($"/projects/{Pid}/portfolio\\?tab=rebalance$"));
        await Expect(Tab("rebalance")).ToHaveAttributeAsync("aria-current", "page");
    }

    [Test]
    public async Task Depot_and_tax_pages_stay_in_the_settings_with_a_depot_in_a_project()
    {
        await AddDepotAsync();
        foreach (var page in new[] { "depots", "instruments", "tax" })
        {
            await GoToAsync($"/portfolio-performance/{page}");
            await Expect(Page).ToHaveURLAsync(new Regex($"/portfolio-performance/{page}$"));
            await Expect(Page.GetByTestId("settings-nav-portfolio-performance")).ToHaveAttributeAsync("aria-current", "page");
        }
    }

    [Test]
    public async Task Switching_projects_keeps_the_current_view()
    {
        await AddDepotAsync();
        var second = await AddSecondProjectAsync();
        var switcher = Page.GetByTestId("project-selector");
        var secondRow = switcher.GetByTestId("switcher-project").Filter(new() { HasText = "Second Example Project" });

        await GoToAsync($"/projects/{Pid}/budgets");
        await switcher.Locator("summary").ClickAsync();
        await secondRow.ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex($"/projects/{second}/budgets$"));

        await GoToAsync($"/projects/{Pid}/cashflow");
        await switcher.Locator("summary").ClickAsync();
        await secondRow.ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex($"/projects/{second}/cashflow(\\?.*)?$"));

        // The second project has no depot: Portfolio falls back to Transactions.
        await GoToAsync($"/projects/{Pid}/portfolio");
        await switcher.Locator("summary").ClickAsync();
        await secondRow.ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex($"/projects/{second}/browse$"));

        // Transactions → Transactions, without the selection.
        await GoToAsync($"/projects/{Pid}/browse?sel=cat-200");
        await switcher.Locator("summary").ClickAsync();
        await secondRow.ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex($"/projects/{second}/browse$"));

        // To a project with a depot: Portfolio → Portfolio.
        await GoToAsync($"/projects/{Pid}/portfolio");
        await switcher.Locator("summary").ClickAsync();
        await switcher.GetByTestId("switcher-project").Filter(new() { HasText = E2ESeed.ProjectName }).ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex($"/projects/{Pid}/portfolio$"));
    }

    [Test]
    public async Task Positions_show_the_depots_value_history_for_the_selected_period()
    {
        await AddDepotAsync();
        var today = DateTime.Today;
        var first = today.AddYears(-4);
        await using (var db = await Db.CreateDbContextAsync())
        {
            static long Noon(DateTime d) => new DateTimeOffset(d.Year, d.Month, d.Day, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
            db.PortfolioChartPoints.Add(new PortfolioChartPoint { OwnerUserId = E2ESeed.OwnerUserId, AccountId = DepotId, Timestamp = Noon(first), Balance = 1000 });
            db.PortfolioChartPoints.Add(new PortfolioChartPoint { OwnerUserId = E2ESeed.OwnerUserId, AccountId = DepotId, Timestamp = Noon(today.AddMonths(-6)), Balance = 1100 });
            await db.SaveChangesAsync();
        }

        await GoToAsync($"/projects/{Pid}/portfolio");

        var chart = Page.GetByTestId("portfolio-value-chart");
        await Expect(chart.Locator("canvas")).ToBeVisibleAsync();
        await Expect(chart).ToHaveAttributeAsync("data-start", today.AddYears(-1).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        // From the carried-in 1.000 € a year ago to today's 1.234,56 €.
        await Expect(Page.GetByTestId("portfolio-value-change")).ToContainTextAsync("+234,56 €");
        // The x axis labels its days in the app's date format, whatever the browser language.
        var firstTick = await Page.EvaluateAsync<string>("() => Chart.getChart('portfolioValueChart').scales.x.getLabelForValue(0)");
        Assert.That(firstTick, Is.EqualTo(today.AddYears(-1).ToString("dd.MM.yyyy", System.Globalization.CultureInfo.InvariantCulture)));

        await Page.GetByTestId("portfolio-period-max").ClickAsync();
        await Expect(chart).ToHaveAttributeAsync("data-start", first.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        await Expect(Page.GetByTestId("portfolio-period-max")).ToHaveAttributeAsync("aria-pressed", "true");
    }

    [Test]
    public async Task A_depot_without_positions_or_value_shows_the_empty_state_without_a_chart()
    {
        await using (var db = await Db.CreateDbContextAsync())
        {
            db.Accounts.Add(new Account { Id = DepotId, OwnerUserId = E2ESeed.OwnerUserId, Name = "Example Depot", Type = 7, Currency = "EUR" });
            await db.SaveChangesAsync();
            db.ProjectAccountAssignments.Add(new ProjectAccountAssignment { ProjectId = Pid, AccountId = DepotId });
            var noon = new DateTimeOffset(DateTime.Today.AddMonths(-3).Year, DateTime.Today.AddMonths(-3).Month, 1, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
            db.PortfolioChartPoints.Add(new PortfolioChartPoint { OwnerUserId = E2ESeed.OwnerUserId, AccountId = DepotId, Timestamp = noon, Balance = 0 });
            await db.SaveChangesAsync();
        }

        await GoToAsync($"/projects/{Pid}/portfolio");

        await Expect(Page.GetByTestId("portfolio-empty")).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("portfolio-value-chart")).ToHaveCountAsync(0);
    }

    [Test]
    public async Task Money_axes_without_range_get_distinct_labels_and_no_negative_zero()
    {
        await GoToAsync($"/projects/{Pid}/portfolio");
        await Page.WaitForFunctionAsync("() => typeof window.renderChart === 'function' && typeof Chart !== 'undefined'");

        // Renders a line chart with a whole-euro money axis into a scratch canvas and returns its y tick labels.
        const string labelsFor = """
            (values) => {
                const box = document.createElement('div');
                box.style.cssText = 'position:relative;width:400px;height:220px';
                const canvas = document.createElement('canvas');
                canvas.id = 'axisProbe' + Math.random().toString(36).slice(2);
                box.appendChild(canvas);
                document.body.appendChild(box);
                window.renderChart(canvas.id, {
                    type: 'line',
                    data: { labels: values.map((_, i) => String(i)), datasets: [{ label: 'x', data: values }] },
                    options: { responsive: true, maintainAspectRatio: false, animation: false,
                               scales: { y: { ticks: { callback: { kind: 'euroTicks', decimals: 0 } } } } }
                });
                const c = Chart.getChart(canvas.id);
                const labels = c.scales.y.ticks.map(t => t.label);
                window.destroyChart(canvas.id);
                box.remove();
                return labels;
            }
            """;
        foreach (var values in new[] { new[] { 0.0, 0.0, 0.0 }, new[] { 0.3, 0.3 }, new[] { 0.0, 0.4, 0.2 }, new[] { -0.2, 0.0 }, new[] { 5.0, 5.0 } })
        {
            var labels = await Page.EvaluateAsync<string[]>(labelsFor, values);
            Assert.That(labels, Has.Length.GreaterThan(1), string.Join(", ", values));
            Assert.That(labels, Is.Unique, string.Join(" | ", labels));
            Assert.That(labels, Has.None.StartsWith("\u22120"), string.Join(" | ", labels));
        }
        // All zero: a 0 € baseline with room above, nothing below.
        var zero = await Page.EvaluateAsync<string[]>(labelsFor, new[] { 0.0, 0.0 });
        Assert.That(zero, Has.None.StartsWith("\u2212"), string.Join(" | ", zero));
    }

    [Test]
    public async Task Portfolio_is_listed_only_while_the_project_has_an_open_depot()
    {
        await AddDepotAsync();
        await using (var db = await Db.CreateDbContextAsync())
        {
            var depot = await db.Accounts.SingleAsync(a => a.Id == DepotId);
            depot.Closed = true;
            await db.SaveChangesAsync();
        }

        await GoToAsync($"/projects/{Pid}/browse");
        var views = Page.GetByTestId("project-views");
        await Expect(views.GetByTestId("view-transactions")).ToBeVisibleAsync();
        await Expect(views.GetByTestId("view-portfolio")).ToHaveCountAsync(0);

        // Still reachable by URL, and then listed as the current view.
        await GoToAsync($"/projects/{Pid}/portfolio");
        await Expect(Page.GetByTestId("portfolio-tab-positions")).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("view-portfolio")).ToHaveAttributeAsync("aria-current", "page");

        // Reopening the depot lists Portfolio again.
        await GoToAsync($"/projects/{Pid}/browse");
        await Expect(views.GetByTestId("view-transactions")).ToBeVisibleAsync();
        await using (var db = await Db.CreateDbContextAsync())
        {
            var depot = await db.Accounts.SingleAsync(a => a.Id == DepotId);
            depot.Closed = false;
            await db.SaveChangesAsync();
        }
        Factory.BrowserHostServices.GetRequiredService<ChangeNotificationService>()
            .Publish(new DataChange(DataArea.ImportedData, OwnerUserId: E2ESeed.OwnerUserId));
        await Expect(views.GetByTestId("view-portfolio")).ToBeVisibleAsync();
    }
}
