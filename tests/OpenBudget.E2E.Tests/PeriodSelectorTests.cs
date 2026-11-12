using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using NUnit.Framework;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.E2E.Tests;

/// <summary>
/// The period selector in the top bar: one period shared by Transactions, Budget and Cashflow, the custom range that
/// narrows the list, the chart and the sidebar sums, and links that still carry the former filter's "per" key. The
/// baseline world has Groceries -42,00 € on 15 January; these tests add -8,00 € on 10 February.
/// </summary>
[TestFixture]
public sealed class PeriodSelectorTests : EndToEndTestBase
{
    private static int Pid => E2ESeed.ProjectId;
    private ILocator GroceriesRow => Page.GetByTestId("tree-category").Filter(new() { HasText = "Groceries" }).First;

    [SetUp]
    public async Task AddFebruaryGroceries()
    {
        await using var db = await Factory.Services.GetRequiredService<SystemDbContextFactory>().CreateDbContextAsync();
        var date = new DateTime(E2ESeed.BudgetYear, 2, 10, 12, 0, 0, DateTimeKind.Utc);
        db.Transactions.Add(new Transaction
        {
            Id = 70,
            SourceId = E2ESeed.SourceId,
            CategoryId = 200,
            Amount = -8_00,
            Timestamp = ((DateTimeOffset)date).ToUnixTimeSeconds(),
            Name = "txn-feb",
        });
        await db.SaveChangesAsync();
    }

    [Test]
    public async Task The_period_persists_from_transactions_to_budget()
    {
        await GoToAsync($"/projects/{Pid}/browse?year={E2ESeed.BudgetYear}&month=1");
        var bar = Page.GetByTestId("app-topbar");
        await bar.GetByRole(AriaRole.Button, new() { Name = "Year", Exact = true }).ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex("period=year"));

        var budgetLink = Page.GetByTestId("project-views").GetByTestId("view-budget");
        await Expect(budgetLink).ToHaveAttributeAsync("href", new Regex("period=year"));
        await budgetLink.ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex($"/budgets\\?.*year={E2ESeed.BudgetYear}.*period=year"));
        await Expect(bar.GetByRole(AriaRole.Button, new() { Name = "Year", Exact = true })).ToHaveClassAsync(new Regex("active"));
        await Expect(Page.GetByTestId("budget-table").GetByTestId("budget-total")).ToContainTextAsync("1.200,00 €");

        // Cashflow shows the period's year and offers only years.
        await Page.GetByTestId("project-views").GetByTestId("view-cashflow").ClickAsync();
        await Expect(bar.GetByTestId("period-nav")).ToContainTextAsync(E2ESeed.BudgetYear.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await Expect(bar.GetByTestId("period-range")).ToHaveCountAsync(0);

        // Views without a period show no selector.
        await GoToAsync($"/projects/{Pid}/documents");
        await Expect(Page.GetByTestId("period-nav")).ToHaveCountAsync(0);
    }

    [Test]
    public async Task A_custom_range_narrows_the_list_the_chart_and_the_sidebar_sums()
    {
        await GoToAsync($"/projects/{Pid}/browse?year={E2ESeed.BudgetYear}&month=2&period=year&sel=cat-200");
        await Expect(Page.GetByText(new Regex("2 transactions"))).ToBeVisibleAsync();
        await Expect(GroceriesRow).ToContainTextAsync("50,00");
        await Page.WaitForFunctionAsync("() => Chart.getChart('browseChart')?.data.labels.length >= 2");

        await Page.GetByTestId("period-range").ClickAsync();
        await Page.GetByTestId("period-range-from").FillAsync($"{E2ESeed.BudgetYear}-02-01");
        await Page.GetByTestId("period-range-to").FillAsync($"{E2ESeed.BudgetYear}-02-28");
        await Page.GetByTestId("period-range-apply").ClickAsync();

        await Expect(Page).ToHaveURLAsync(new Regex("period=range"));
        await Expect(Page).ToHaveURLAsync(new Regex("from=2026-02-01&to=2026-02-28"));
        await Expect(Page.GetByText(new Regex("1 transactions"))).ToBeVisibleAsync();
        await Expect(Page.GetByText("txn-feb")).ToBeVisibleAsync();
        await Expect(Page.GetByText("txn-1", new() { Exact = true })).ToHaveCountAsync(0);
        await Expect(GroceriesRow).ToContainTextAsync("8,00");
        await Expect(GroceriesRow).Not.ToContainTextAsync("50,00");
        // The chart keeps the whole history and highlights only February.
        await Page.WaitForFunctionAsync("() => { const c = Chart.getChart('browseChart'); if (!c) return false; const bg = c.data.datasets[0].backgroundColor; const i = c.data.labels.findIndex(l => l.startsWith('Feb')); return i >= 0 && bg.filter(x => x === bg[i]).length === 1 && c.data.labels.length >= 2; }");
        await Expect(Page.GetByTestId("filter-count")).ToHaveCountAsync(0);   // the period is not a filter
    }

    [Test]
    public async Task An_old_per_link_opens_the_matching_period()
    {
        await GoToAsync($"/projects/{Pid}/browse?sel=cat-200&per=y:{E2ESeed.BudgetYear}");
        var bar = Page.GetByTestId("app-topbar");
        await Expect(bar.GetByRole(AriaRole.Button, new() { Name = "Year", Exact = true })).ToHaveClassAsync(new Regex("active"));
        await Expect(GroceriesRow).ToContainTextAsync("50,00");
        await Expect(Page).Not.ToHaveURLAsync(new Regex("per="));

        await GoToAsync($"/projects/{Pid}/browse?sel=cat-200&per=custom&from={E2ESeed.BudgetYear}-02-01&to={E2ESeed.BudgetYear}-02-28");
        await Expect(bar.GetByTestId("period-range")).ToHaveClassAsync(new Regex("active"));
        await Expect(Page.GetByText(new Regex("1 transactions"))).ToBeVisibleAsync();

        // "Last N days" becomes a custom range ending today.
        await GoToAsync($"/projects/{Pid}/browse?per=30d");
        await Expect(bar.GetByTestId("period-range")).ToHaveClassAsync(new Regex("active"));
        await Expect(Page).ToHaveURLAsync(new Regex("period=range"));
    }
}
