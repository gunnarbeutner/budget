using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using NUnit.Framework;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.E2E.Tests;

/// <summary>
/// The ledger's header stays put while the bookings scroll: on desktop the title row with its actions (
/// the Filter button) and the chart stay in view; on a phone the sticky toolbar keeps title and actions while
/// the chart scrolls away with the page. Set OB_STICKY_SCREENSHOT_DIR to keep screenshots for review.
/// </summary>
[TestFixture]
public sealed class StickyHeaderTests : EndToEndTestBase
{
    private long _accountId;

    [SetUp]
    public async Task SeedBusyAccount()
    {
        _accountId = SourceScope.Namespace(E2ESeed.SourceId, 78);
        var factory = Factory.Services.GetRequiredService<SystemDbContextFactory>();
        await using var db = await factory.CreateDbContextAsync();
        db.Accounts.Add(new Account
        {
            Id = _accountId,
            SourceId = E2ESeed.SourceId,
            Name = "Long Giro",
            Type = 1,
            Currency = "EUR",
            Balances = "11:650.640000,3:EUR,",
        });
        await db.SaveChangesAsync();
        db.ProjectAccountAssignments.Add(new ProjectAccountAssignment { ProjectId = E2ESeed.ProjectId, AccountId = _accountId });
        for (var i = 0; i < 150; i++)
        {
            var date = new DateTime(2026, 1 + i % 11, 1 + i % 28, 0, 0, 0, DateTimeKind.Utc);
            var ts = ((DateTimeOffset)date).ToUnixTimeSeconds();
            db.Transactions.Add(new Transaction
            {
                Id = 7500 + i,
                SourceId = E2ESeed.SourceId,
                AccountId = _accountId,
                Amount = -(250 + i),
                CategoryId = 200,
                Currency = "EUR",
                Name = $"Example Shop {i}",
                Timestamp = ts,
                ValueTimestamp = ts,
            });
        }
        await db.SaveChangesAsync();
    }

    private string Url => $"/projects/{E2ESeed.ProjectId}/browse?year=2026&month=11&sel=acct-{_accountId}";

    [TestCase("light")]
    [TestCase("dark")]
    public async Task Desktop_header_and_chart_stay_while_the_list_scrolls(string theme)
    {
        await Page.EmulateMediaAsync(new PageEmulateMediaOptions { ColorScheme = theme == "dark" ? ColorScheme.Dark : ColorScheme.Light });
        await Page.SetViewportSizeAsync(1280, 900);
        await GoToAsync(Url);
        var filter = Page.GetByTestId("filter-toggle");
        var chart = Page.GetByTestId("browse-chart-scroll");
        await Expect(chart).ToBeVisibleAsync();
        var filterBefore = (await filter.BoundingBoxAsync())!;
        var chartBefore = (await chart.BoundingBoxAsync())!;

        var row = Page.GetByTestId("tx-row").First;
        await row.HoverAsync();
        await Page.Mouse.WheelAsync(0, 3000);
        await Page.WaitForFunctionAsync("() => document.querySelector('.browse-scroll').scrollTop > 1000");
        Assert.That(await Page.EvaluateAsync<double>("() => window.scrollY"), Is.EqualTo(0), "only the list scrolls");

        await Expect(filter).ToBeInViewportAsync();
        await Expect(chart).ToBeInViewportAsync();
        Assert.That((await filter.BoundingBoxAsync())!.Y, Is.EqualTo(filterBefore.Y).Within(1));
        Assert.That((await chart.BoundingBoxAsync())!.Y, Is.EqualTo(chartBefore.Y).Within(1));
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Long Giro" })).ToBeInViewportAsync();
        await Shoot($"sticky-desktop-{theme}.png");
    }

    [Test]
    public async Task Phone_keeps_title_and_actions_while_the_page_scrolls()
    {
        await Page.SetViewportSizeAsync(390, 760);
        await GoToAsync(Url);
        var filter = Page.GetByTestId("filter-toggle");
        await Expect(filter).ToBeVisibleAsync();

        await Page.EvaluateAsync("() => window.scrollTo(0, 3000)");
        await Page.WaitForFunctionAsync("() => window.scrollY > 1000");
        await Expect(filter).ToBeInViewportAsync();
        await Expect(Page.GetByTestId("browse-toolbar").GetByTestId("browse-summary")).ToBeInViewportAsync();
        var hit = await filter.EvaluateAsync<bool>(@"b => { const r = b.getBoundingClientRect();
            return b.contains(document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2)); }");
        Assert.That(hit, Is.True, "no row covers the Filter button");
        Assert.That(await Page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > window.innerWidth + 1"), Is.False);
        await Page.WaitForTimeoutAsync(300);
        await Shoot("sticky-phone.png");
    }

    private async Task Shoot(string name)
    {
        var dir = Environment.GetEnvironmentVariable("OB_STICKY_SCREENSHOT_DIR");
        if (!string.IsNullOrEmpty(dir))
            await Page.ScreenshotAsync(new() { Path = Path.Combine(dir, name) });
    }
}
