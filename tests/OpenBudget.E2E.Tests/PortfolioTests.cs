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
    private static readonly long DepotId = SourceScope.Namespace(E2ESeed.SourceId, 7);

    private SystemDbContextFactory Db => Factory.Services.GetRequiredService<SystemDbContextFactory>();

    private async Task AddDepotAsync()
    {
        await using var db = await Db.CreateDbContextAsync();
        db.Accounts.Add(new Account { Id = DepotId, SourceId = E2ESeed.SourceId, Name = "Example Depot", Type = 7, Currency = "EUR" });
        await db.SaveChangesAsync();
        db.ProjectAccountAssignments.Add(new ProjectAccountAssignment { ProjectId = Pid, AccountId = DepotId });
        db.Holdings.Add(new Holding
        {
            Id = 1,
            SourceId = E2ESeed.SourceId,
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
        var second = new Project { Name = "Second Example Project", SourceId = first.SourceId, OwnerUserId = first.OwnerUserId, SortOrder = first.SortOrder + 1 };
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
        await Expect(Page.GetByTestId("portfolio-tax-note")).ToContainTextAsync("legacy");
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
    public async Task Source_depot_and_tax_pages_lead_to_the_portfolio_of_a_project_using_the_source()
    {
        await AddDepotAsync();

        await GoToAsync($"/sources/{E2ESeed.SourceId}/tax");
        await Expect(Page).ToHaveURLAsync(new Regex($"/projects/{Pid}/portfolio\\?tab=tax$"));

        await GoToAsync($"/sources/{E2ESeed.SourceId}/depots");
        await Expect(Page).ToHaveURLAsync(new Regex($"/projects/{Pid}/portfolio\\?tab=positions$"));

        // A source without such a project keeps its own page.
        await GoToAsync($"/sources/{E2ESeed.SpareSourceId}/depots");
        await Expect(Page).ToHaveURLAsync(new Regex($"/sources/{E2ESeed.SpareSourceId}/depots$"));
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
            db.PortfolioChartPoints.Add(new PortfolioChartPoint { SourceId = E2ESeed.SourceId, AccountId = DepotId, Timestamp = Noon(first), Balance = 1000 });
            db.PortfolioChartPoints.Add(new PortfolioChartPoint { SourceId = E2ESeed.SourceId, AccountId = DepotId, Timestamp = Noon(today.AddMonths(-6)), Balance = 1100 });
            await db.SaveChangesAsync();
        }

        await GoToAsync($"/projects/{Pid}/portfolio");

        var chart = Page.GetByTestId("portfolio-value-chart");
        await Expect(chart.Locator("canvas")).ToBeVisibleAsync();
        await Expect(chart).ToHaveAttributeAsync("data-start", today.AddYears(-1).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        // From the carried-in 1.000 € a year ago to today's 1.234,56 €.
        await Expect(Page.GetByTestId("portfolio-value-change")).ToContainTextAsync("+234,56 €");

        await Page.GetByTestId("portfolio-period-max").ClickAsync();
        await Expect(chart).ToHaveAttributeAsync("data-start", first.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        await Expect(Page.GetByTestId("portfolio-period-max")).ToHaveAttributeAsync("aria-pressed", "true");
    }
}
