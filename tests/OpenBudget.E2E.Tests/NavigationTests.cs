using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using NUnit.Framework;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.E2E.Tests;

/// <summary>
/// The app navigation: the project sidebar's views (Transactions, Budget, Cashflow, and Portfolio only with a
/// depot), "/" reopening the last project, the project switcher in the top bar (which replaced the start page)
/// and the gear menu reaching every setup page.
/// </summary>
[TestFixture]
public sealed class NavigationTests : EndToEndTestBase
{
    private const string SecondProjectName = "Second Example Project";

    private static int Pid => E2ESeed.ProjectId;

    private SystemDbContextFactory Db => Factory.Services.GetRequiredService<SystemDbContextFactory>();

    /// <summary>A second project of the same owner, on the same source.</summary>
    private async Task<int> AddSecondProjectAsync()
    {
        await using var db = await Db.CreateDbContextAsync();
        var first = await db.Projects.SingleAsync(p => p.Id == E2ESeed.ProjectId);
        var second = new Project { Name = SecondProjectName, OwnerUserId = first.OwnerUserId, SortOrder = first.SortOrder + 1 };
        db.Projects.Add(second);
        await db.SaveChangesAsync();
        return second.Id;
    }

    private ILocator View(string name) => Page.GetByTestId("project-views").GetByTestId($"view-{name}");

    [Test]
    public async Task Sidebar_views_switch_between_transactions_budget_and_cashflow()
    {
        await GoToAsync($"/projects/{Pid}/browse");
        await Expect(View("transactions")).ToHaveAttributeAsync("aria-current", "page");
        await Expect(View("portfolio")).ToHaveCountAsync(0);   // no depot in this project
        // Documents is one of the views, below the others, with no separate documents section in the sidebar.
        await Expect(Page.GetByTestId("project-views").Locator(".shell-view-label"))
            .ToHaveTextAsync(new[] { "Transactions", "Budget", "Cashflow", "Scheduled", "Documents" });
        await Expect(Page.GetByTestId("project-sidebar").GetByText("Statements")).ToHaveCountAsync(0);

        await View("budget").ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex($".*/projects/{Pid}/budgets\\?year=\\d+&month=\\d+$"));
        await Expect(View("budget")).ToHaveAttributeAsync("aria-current", "page");
        await Expect(View("transactions")).Not.ToHaveAttributeAsync("aria-current", "page");

        await View("cashflow").ClickAsync();
        // the cashflow view then adds its year/budget to the query (a race the old pattern lost on a busy machine)
        await Expect(Page).ToHaveURLAsync(new Regex($".*/projects/{Pid}/cashflow(\\?.*)?$"));
        await Expect(View("cashflow")).ToHaveAttributeAsync("aria-current", "page");

        await View("transactions").ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex($".*/projects/{Pid}/browse"));
        await Expect(Page.GetByTestId("browse-toolbar")).ToBeVisibleAsync();

        await View("documents").ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex($".*/projects/{Pid}/documents$"));
        await Expect(View("documents")).ToHaveAttributeAsync("aria-current", "page");
    }

    [Test]
    public async Task Portfolio_view_appears_when_the_project_has_a_depot()
    {
        await using (var db = await Db.CreateDbContextAsync())
        {
            var depotId = RowIds.Namespace(E2ESeed.IdSpace, 7);
            db.Accounts.Add(new Account { Id = depotId, OwnerUserId = E2ESeed.OwnerUserId, Name = "Example Depot", Type = 7, Currency = "EUR" });
            await db.SaveChangesAsync();
            db.ProjectAccountAssignments.Add(new ProjectAccountAssignment { ProjectId = Pid, AccountId = depotId });
            await db.SaveChangesAsync();
        }

        await GoToAsync($"/projects/{Pid}/budgets");
        await View("portfolio").ClickAsync();

        await Expect(Page).ToHaveURLAsync(new Regex($".*/projects/{Pid}/portfolio$"));
        await Expect(View("portfolio")).ToHaveAttributeAsync("aria-current", "page");
    }

    [Test]
    public async Task Picking_an_account_outside_transactions_opens_transactions_filtered_to_it()
    {
        await EditingSeed.SeedAsync(Db);
        await GoToAsync($"/projects/{Pid}/cashflow");

        await Page.GetByTestId("tree-account").Filter(new() { HasText = EditingSeed.BankAccountName }).ClickAsync();

        await Expect(Page).ToHaveURLAsync(new Regex($".*/projects/{Pid}/browse\\?year=\\d+&month=\\d+&sel=acct-{EditingSeed.BankAccountId}"));
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = EditingSeed.BankAccountName })).ToBeVisibleAsync();
    }

    [Test]
    public async Task Root_opens_the_project_the_user_was_in_last()
    {
        var second = await AddSecondProjectAsync();

        await GoToAsync("/");
        await Expect(Page).ToHaveURLAsync(new Regex($".*/projects/{Pid}/browse$"));   // nothing remembered yet: the first project

        await GoToAsync($"/projects/{second}/budgets");
        // The header remembers the project from JS after its own interactive render, which can follow the layout's.
        await Page.WaitForFunctionAsync("id => document.cookie.split('; ').includes('ob.lastProject=' + id)", second);
        await GoToAsync("/");
        await Expect(Page).ToHaveURLAsync(new Regex($".*/projects/{second}/browse$"));
        await Expect(Page.Locator("[data-testid=project-selector] .project-selector-name")).ToHaveTextAsync(SecondProjectName);
    }

    [Test]
    public async Task Switcher_lists_the_projects_with_their_numbers_and_switches()
    {
        await EditingSeed.SeedAsync(Db);
        var second = await AddSecondProjectAsync();
        await GoToAsync($"/projects/{Pid}/browse");

        await Page.Locator("[data-testid=project-selector] > summary").ClickAsync();
        var rows = Page.GetByTestId("switcher-project");
        await Expect(rows).ToHaveCountAsync(2);
        var first = rows.Filter(new() { HasText = E2ESeed.ProjectName });
        await Expect(first).ToHaveAttributeAsync("aria-current", "true");
        await Expect(first.GetByTestId("switcher-balance")).ToContainTextAsync("1.050,00 €");
        await Expect(first.GetByTestId("switcher-balance")).ToContainTextAsync("2 accounts");
        await Expect(first.GetByTestId("switcher-budget")).ToContainTextAsync("%");
        await Expect(Page.GetByTestId("switcher-new-project")).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("switcher-manage")).ToHaveCountAsync(0);

        await rows.Filter(new() { HasText = SecondProjectName }).ClickAsync();

        await Expect(Page).ToHaveURLAsync(new Regex($".*/projects/{second}/browse$"));
        await Expect(Page.Locator("[data-testid=project-selector] .project-selector-name")).ToHaveTextAsync(SecondProjectName);
        await Expect(Page.Locator("[data-testid=project-selector]")).Not.ToHaveAttributeAsync("open", "");
    }

    [Test]
    public async Task The_old_project_list_url_redirects_to_the_last_project()
    {
        var second = await AddSecondProjectAsync();
        await GoToAsync($"/projects/{second}/budgets");

        await GoToAsync("/projects/manage");

        await Expect(Page).ToHaveURLAsync(new Regex($".*/projects/{second}/browse$"));
    }

    [Test]
    public async Task The_avatar_leads_to_settings_and_the_switcher_gear_to_the_project_settings()
    {
        await GoToAsync($"/projects/{Pid}/browse");

        await Page.Locator("[data-testid=project-selector] summary").ClickAsync();
        await Page.Locator($"[data-testid=switcher-project-settings][href='/projects/{Pid}/settings']").ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex($".*/projects/{Pid}/settings$"));

        var menu = Page.GetByTestId("user-menu");
        await menu.Locator("summary").ClickAsync();
        await Expect(menu.GetByTestId("sign-out-link")).ToBeVisibleAsync();
        await menu.GetByTestId("user-settings-link").ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(".*/Account$"));
        // the settings list starts with the account and ends with About
        var items = Page.Locator("[data-testid=settings-nav] .shell-view");
        await Expect(items.First).ToHaveAttributeAsync("data-testid", "settings-nav-account");
        await Expect(items.Last).ToHaveAttributeAsync("data-testid", "settings-nav-about");
        await Expect(Page.GetByTestId("settings-nav-get-started")).ToHaveCountAsync(0);
        await Page.GetByTestId("settings-nav-about").ClickAsync();
        await Expect(Page.GetByTestId("about-version")).ToBeVisibleAsync();
    }

    [Test]
    public async Task Escape_closes_the_avatar_menu()
    {
        await GoToAsync($"/projects/{Pid}/browse");
        var menu = Page.GetByTestId("user-menu");
        await menu.Locator("summary").ClickAsync();
        await Expect(menu.GetByTestId("user-settings-link")).ToBeVisibleAsync();

        await Page.Keyboard.PressAsync("Escape");

        await Expect(menu.GetByTestId("user-settings-link")).Not.ToBeVisibleAsync();
    }
}
