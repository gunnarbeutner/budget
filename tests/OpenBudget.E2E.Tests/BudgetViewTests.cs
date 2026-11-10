using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using NUnit.Framework;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.E2E.Tests;

/// <summary>
/// The Budget view: period in the toolbar, the summary strip, the grouped table (German money format, rows that
/// open Transactions filtered to the category and period), the charts behind a toggle, and imported data
/// reloading in place. January of the seeded year: Groceries 42,00 € of 100,00 €.
/// </summary>
[TestFixture]
public sealed class BudgetViewTests : EndToEndTestBase
{
    private static int Pid => E2ESeed.ProjectId;
    private static string January => $"year={E2ESeed.BudgetYear}&month=1";

    [Test]
    public async Task Budget_table_groups_categories_and_shows_german_money()
    {
        await GoToAsync($"/projects/{Pid}/budgets?{January}");

        var table = Page.GetByTestId("budget-table");
        await Expect(table.Locator(".budget-group")).ToContainTextAsync("Living");
        var row = table.GetByTestId("budget-row").Filter(new() { HasText = "Groceries" });
        await Expect(row).ToContainTextAsync("100,00 €");
        await Expect(row).ToContainTextAsync("42,00 €");
        await Expect(row).ToContainTextAsync("58,00 €");
        await Expect(table.GetByTestId("budget-total")).ToContainTextAsync("100,00 €");
        await Expect(Page.GetByTestId("budget-spent")).ToContainTextAsync("42,00 € of 100,00 €");

        // A year's budget: twelve months of 100,00 €, in German format.
        await Page.GetByRole(AriaRole.Button, new() { Name = "Year", Exact = true }).ClickAsync();
        await Expect(table.GetByTestId("budget-total")).ToContainTextAsync("1.200,00 €");
        await Expect(Page).ToHaveURLAsync(new Regex("period=year"));
    }

    [Test]
    public async Task Income_has_its_own_section_with_expected_and_received()
    {
        await GoToAsync($"/projects/{Pid}/budgets?{January}");

        var table = Page.GetByTestId("budget-table");
        await Expect(table.GetByTestId("budget-income")).ToContainTextAsync("Expected");
        var salary = table.GetByTestId("budget-income-row").Filter(new() { HasText = "Salary" });
        await Expect(salary).ToContainTextAsync("500,00 €");
        await Expect(table.GetByTestId("budget-income-total")).ToContainTextAsync("500,00 €");
        // Spending rows and totals do not include income.
        await Expect(table.GetByTestId("budget-row").Filter(new() { HasText = "Salary" })).ToHaveCountAsync(0);
        await Expect(table.GetByTestId("budget-total")).ToContainTextAsync("100,00 €");
    }

    [Test]
    public async Task The_sidebar_budget_badge_follows_the_shown_period()
    {
        var badge = Page.GetByTestId("view-budget").Locator(".shell-view-badge");
        await GoToAsync($"/projects/{Pid}/budgets?{January}");
        await Expect(badge).ToHaveTextAsync("42 %");

        await Page.GetByRole(AriaRole.Button, new() { Name = "Year", Exact = true }).ClickAsync();
        await Expect(badge).ToHaveTextAsync("4 %");   // 42,00 € of 1.200,00 €
    }

    [Test]
    public async Task A_budget_row_opens_transactions_filtered_to_the_category_and_period()
    {
        await GoToAsync($"/projects/{Pid}/budgets?{January}");

        await Page.GetByTestId("budget-row").Filter(new() { HasText = "Groceries" }).ClickAsync();

        await Expect(Page).ToHaveURLAsync(new Regex($"/projects/{Pid}/browse\\?year={E2ESeed.BudgetYear}&month=1&sel=cat-200"));
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Groceries" })).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("category-budget-line")).ToContainTextAsync("42,00 €");
    }

    [Test]
    public async Task Charts_are_collapsed_until_toggled()
    {
        await GoToAsync($"/projects/{Pid}/budgets?{January}");

        var toggle = Page.GetByTestId("budget-charts-toggle");
        await Expect(toggle).ToHaveAttributeAsync("aria-expanded", "false");
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Category Trends" })).ToHaveCountAsync(0);

        await toggle.ClickAsync();
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Category Trends" })).ToBeVisibleAsync();
    }

    [Test]
    public async Task Imported_data_reloads_the_budget_in_place_without_the_banner()
    {
        await GoToAsync($"/projects/{Pid}/budgets?{January}&period=ytd");
        var row = Page.GetByTestId("budget-row").Filter(new() { HasText = "Groceries" });
        await Expect(row).ToContainTextAsync("42,00 €");

        var services = AppFixture.Factory.Services;
        await using (var db = await services.GetRequiredService<SystemDbContextFactory>().CreateDbContextAsync())
        {
            var date = ((DateTimeOffset)new DateTime(E2ESeed.BudgetYear, 1, 20, 0, 0, 0, DateTimeKind.Utc)).ToUnixTimeSeconds();
            db.Transactions.Add(new Transaction { Id = 60, SourceId = E2ESeed.SourceId, CategoryId = 200, Amount = -8_00, Timestamp = date, Name = "Example Market" });
            await db.SaveChangesAsync();
        }
        AppFixture.Factory.BrowserHostServices.GetRequiredService<ChangeNotificationService>().Publish(new DataChange(DataArea.ImportedData, SourceId: E2ESeed.SourceId));

        await Expect(row).ToContainTextAsync("50,00 €");
        await Expect(Page.GetByText("changed in another session")).ToHaveCountAsync(0);
        await Expect(Page).ToHaveURLAsync(new Regex("period=ytd"));   // the view state survives
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "YTD", Exact = true })).ToHaveClassAsync(new Regex("active"));
    }
}
