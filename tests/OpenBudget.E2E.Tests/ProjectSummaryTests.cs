using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using NUnit.Framework;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.E2E.Tests;

/// <summary>
/// The summary strip above Transactions (whole project, no filter), the one-line budget note when a category is
/// selected, and the old Overview URL landing on Transactions. Anchored on January of the seeded budget year:
/// Groceries 42,00 € of a 100,00 € budget, Salary 500,00 €.
/// </summary>
[TestFixture]
public sealed class ProjectSummaryTests : EndToEndTestBase
{
    private static int Pid => E2ESeed.ProjectId;
    private static string January => $"year={E2ESeed.BudgetYear}&month=1";

    private SystemDbContextFactory Db => Factory.Services.GetRequiredService<SystemDbContextFactory>();

    private ILocator Strip => Page.GetByTestId("summary-strip");

    /// <summary>Pushes Groceries over its budget in January and adds a new, uncategorized booking.</summary>
    private async Task AddReviewWorkAsync()
    {
        await using var db = await Db.CreateDbContextAsync();
        var date = ((DateTimeOffset)new DateTime(E2ESeed.BudgetYear, 1, 20, 0, 0, 0, DateTimeKind.Utc)).ToUnixTimeSeconds();
        db.Transactions.Add(new Transaction { Id = 50, SourceId = E2ESeed.SourceId, CategoryId = 200, Amount = -80_00, Timestamp = date, Name = "Example Market", IsNew = true });
        db.Transactions.Add(new Transaction { Id = 51, SourceId = E2ESeed.SourceId, CategoryId = null, Amount = -9_99, Timestamp = date, Name = "Example Kiosk" });
        await db.SaveChangesAsync();
    }

    [Test]
    public async Task Summary_strip_shows_budget_income_and_review_tiles()
    {
        await GoToAsync($"/projects/{Pid}/browse?{January}");

        await Expect(Strip.GetByTestId("tile-spent")).ToContainTextAsync("42,00 €");
        await Expect(Strip.GetByTestId("tile-spent")).ToContainTextAsync("of 100,00 €");
        await Expect(Strip.GetByTestId("tile-income")).ToContainTextAsync("500,00 €");
        await Expect(Strip.GetByTestId("tile-over")).ToContainTextAsync("All on track");
        await Expect(Strip.GetByTestId("tile-review")).ToContainTextAsync("Nothing to review");
    }

    [Test]
    public async Task Summary_tiles_link_to_the_matching_filtered_lists()
    {
        await AddReviewWorkAsync();
        await GoToAsync($"/projects/{Pid}/browse?{January}");

        // Over budget: the worst category, by how much, and how many of the budgeted ones.
        await Expect(Strip.GetByTestId("tile-over")).ToContainTextAsync("Groceries");
        await Expect(Strip.GetByTestId("tile-over")).ToContainTextAsync("+22,00 €");
        await Expect(Strip.GetByTestId("tile-over")).ToContainTextAsync("1 of 1 categories");
        await Strip.GetByTestId("summary-category").ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(@"sel=cat-200"));
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Groceries" })).ToBeVisibleAsync();
        await Expect(Strip).ToHaveCountAsync(0);   // a selection replaces the strip

        await GoToAsync($"/projects/{Pid}/browse?{January}");
        await Expect(Strip.GetByTestId("summary-new")).ToHaveTextAsync("1 new");
        await Strip.GetByTestId("summary-new").ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(@"new=1"));
        await Expect(Page.GetByTestId("tx-row")).ToHaveCountAsync(1);
        await Expect(Page.GetByTestId("tx-row")).ToContainTextAsync("Example Market");

        await GoToAsync($"/projects/{Pid}/browse?{January}");
        await Expect(Strip.GetByTestId("summary-uncategorized")).ToHaveTextAsync("1 uncategorized");
        await Strip.GetByTestId("summary-uncategorized").ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(@"sel=uncategorized"));
        await Expect(Page.GetByTestId("tx-row")).ToHaveCountAsync(1);
        await Expect(Page.GetByTestId("tx-row")).ToContainTextAsync("Example Kiosk");

        await GoToAsync($"/projects/{Pid}/browse?{January}");
        await Strip.GetByTestId("summary-income").ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(@"dir=in"));
        await Expect(Page.GetByTestId("tx-row")).ToHaveCountAsync(1);
        await Expect(Page.GetByTestId("tx-row")).ToContainTextAsync("500,00 €");
    }

    [Test]
    public async Task Summary_strip_can_be_collapsed_and_stays_collapsed()
    {
        await GoToAsync($"/projects/{Pid}/browse?{January}");
        await Expect(Strip).ToBeVisibleAsync();

        await Page.GetByTestId("summary-toggle").ClickAsync();
        await Expect(Strip).ToHaveCountAsync(0);

        await GoToAsync($"/projects/{Pid}/browse?{January}");
        await Expect(Page.GetByTestId("summary-toggle")).ToHaveAttributeAsync("aria-expanded", "false");
        await Expect(Strip).ToHaveCountAsync(0);

        await Page.GetByTestId("summary-toggle").ClickAsync();
        await Expect(Strip).ToBeVisibleAsync();
    }

    [Test]
    public async Task A_selected_category_shows_its_budget_line_with_a_link_to_the_budget()
    {
        await GoToAsync($"/projects/{Pid}/browse?{January}&sel=cat-200");

        var line = Page.GetByTestId("category-budget-line");
        await Expect(line).ToContainTextAsync("42,00 €");
        await Expect(line).ToContainTextAsync("of 100,00 €");
        await line.GetByTestId("category-budget-link").ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex($"/projects/{Pid}/budgets"));
    }

    [Test]
    public async Task The_old_overview_url_opens_transactions_for_the_same_period()
    {
        await GoToAsync($"/projects/{Pid}/overview?{January}");

        await Expect(Page).ToHaveURLAsync(new Regex($"/projects/{Pid}/browse\\?{Regex.Escape(January)}$"));
        await Expect(Strip.GetByTestId("tile-spent")).ToContainTextAsync("42,00 €");
    }
}
