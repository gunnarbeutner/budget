using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using NUnit.Framework;
using OpenBudget.Services;

namespace OpenBudget.E2E.Tests;

/// <summary>
/// Scheduled's Subscriptions tab: series detected in the bookings with their monthly and yearly cost, dismissing one,
/// the old Recurring address forwarding there, and the expected charge in the Upcoming tab.
/// </summary>
[TestFixture]
public sealed class RecurringTests : EndToEndTestBase
{
    private static int Pid => E2ESeed.ProjectId;

    private SystemDbContextFactory Db => Factory.Services.GetRequiredService<SystemDbContextFactory>();

    private ILocator Rows => Page.GetByTestId("recurring-row");

    /// <summary>The editing world plus two monthly charges on the bank account, the last ten days ago.</summary>
    private async Task SeedAsync()
    {
        await EditingSeed.SeedAsync(Db);
        await using var db = await Db.CreateDbContextAsync();
        var last = DateTime.Today.AddDays(-10);
        for (var i = 0; i < 5; i++)
        {
            var date = DateTime.SpecifyKind(last.AddMonths(-i), DateTimeKind.Utc);
            EditingSeed.Add(db, 300 + i, EditingSeed.BankAccountId, -12_99, "Streamio Media", date);
            EditingSeed.Add(db, 310 + i, EditingSeed.BankAccountId, -30_00, "Example Gym", date);
        }
        await db.SaveChangesAsync();
    }

    [Test]
    public async Task The_view_shows_the_series_with_totals_and_a_dismissed_one_goes_away()
    {
        await SeedAsync();
        await GoToAsync($"/projects/{Pid}/browse");
        await Page.GetByTestId("project-views").GetByTestId("view-scheduled").ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex($".*/projects/{Pid}/scheduled$"));
        await Page.GetByTestId("scheduled-tab-subscriptions").ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex($".*/projects/{Pid}/scheduled\\?tab=subscriptions$"));
        await Expect(Page.GetByTestId("scheduled-tab-subscriptions")).ToHaveAttributeAsync("aria-selected", "true");

        await Expect(Rows).ToHaveCountAsync(2);
        await Expect(Page.GetByTestId("recurring-totals")).ToHaveTextAsync("42,99 € per month · 515,88 € per year");
        await Expect(Page.GetByTestId("recurring-name")).ToHaveTextAsync(new[] { "Example Gym", "Streamio Media" });
        var gym = Rows.Nth(0);
        await Expect(gym.GetByTestId("recurring-amount")).ToHaveTextAsync("−30,00 €");
        await Expect(gym.GetByTestId("recurring-rhythm")).ToHaveTextAsync("Monthly");
        await Expect(gym.GetByTestId("recurring-date")).ToContainTextAsync("~");
        await gym.GetByTestId("recurring-toggle").ClickAsync();
        await Expect(gym.GetByTestId("recurring-booking")).ToHaveCountAsync(5);

        await gym.GetByTestId("recurring-dismiss").ClickAsync();
        await Expect(Rows).ToHaveCountAsync(1);
        await Expect(Page.GetByTestId("recurring-totals")).ToHaveTextAsync("12,99 € per month · 155,88 € per year");

        await Page.GetByTestId("recurring-show").SelectOptionAsync("dismissed");
        await Expect(Page).ToHaveURLAsync(new Regex("[?&]show=dismissed"));
        await Expect(Page.GetByTestId("recurring-name")).ToHaveTextAsync(new[] { "Example Gym" });

        // The dismissal is stored: a reload keeps it.
        await GoToAsync($"/projects/{Pid}/scheduled?tab=subscriptions");
        await Expect(Page.GetByTestId("recurring-name")).ToHaveTextAsync(new[] { "Streamio Media" });
    }

    [Test]
    public async Task The_old_address_forwards_to_the_tab_and_keeps_the_filter()
    {
        await SeedAsync();
        await GoToAsync($"/projects/{Pid}/recurring?show=ended");
        await Expect(Page).ToHaveURLAsync(new Regex($".*/projects/{Pid}/scheduled\\?tab=subscriptions&show=ended$"));
        await Expect(Page.GetByTestId("recurring-show")).ToHaveValueAsync("ended");
        await Expect(Page.GetByTestId("project-views").GetByTestId("view-recurring")).ToHaveCountAsync(0);

        // Back on Upcoming the subscription filter goes and the kind filter works as before.
        await Page.GetByTestId("scheduled-tab-upcoming").ClickAsync();
        await Expect(Page).ToHaveURLAsync(new Regex($".*/projects/{Pid}/scheduled$"));
        await Expect(Page.GetByTestId("scheduled-kind")).ToBeVisibleAsync();
    }

    [Test]
    public async Task Scheduled_lists_the_next_charge_of_a_series()
    {
        await SeedAsync();
        await GoToAsync($"/projects/{Pid}/scheduled?kind=recurring");
        var rows = Page.GetByTestId("scheduled-row");
        await Expect(rows).ToHaveCountAsync(2);
        await Expect(rows.Nth(0)).ToHaveAttributeAsync("data-kind", "recurring");
        await Expect(rows.Nth(0).GetByTestId("scheduled-date")).ToContainTextAsync("~");
        await Expect(rows.Nth(0).GetByTestId("scheduled-rhythm")).ToHaveTextAsync("Recurring, Monthly · estimated");
    }
}
