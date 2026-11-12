using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using NUnit.Framework;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.E2E.Tests;

/// <summary>
/// The view headers stay readable with a wide sidebar: no header control overlaps another, and the header never
/// overlaps the row below it (the summary actions under the Transactions header used to be covered by a wrapped
/// Filter button).
/// </summary>
[TestFixture]
public sealed class HeaderLayoutTests : EndToEndTestBase
{
    [SetUp]
    public async Task SeedAccounts() =>
        await EditingSeed.SeedAsync(Factory.Services.GetRequiredService<SystemDbContextFactory>());

    [TestCase(1280)]
    [TestCase(1600)]
    public async Task Header_controls_do_not_overlap_with_a_wide_sidebar(int width)
    {
        await Page.SetViewportSizeAsync(width, 900);
        await Page.AddInitScriptAsync("try { localStorage.setItem('ob.sidebarWidth', '520'); } catch (e) {}");
        var pid = E2ESeed.ProjectId;

        await GoToAsync($"/projects/{pid}/browse?year=2026&month=3&sel=acct-{EditingSeed.MoneyMoneyAccountId}");
        var toolbar = Page.GetByTestId("browse-toolbar");
        await Expect(Page.GetByTestId("browse-readonly")).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("filter-toggle")).ToBeVisibleAsync();
        var rail = await Page.Locator(".browse-rail").BoundingBoxAsync();
        Assert.That(rail!.Width, Is.EqualTo(520).Within(2), "the sidebar keeps its resized width");
        await AssertNoOverlapAsync(toolbar,
            ".browse-ident, .browse-balance, .period-stepper, .segmented, .browse-search, [data-testid=filter-toggle], [data-testid=account-documents]");
        await AssertBelowAsync(toolbar, Page.GetByTestId("browse-summary"));

        foreach (var (path, testId) in new[]
                 {
                     ($"/projects/{pid}/budgets", "budget-toolbar"),
                     ($"/projects/{pid}/portfolio", "portfolio-toolbar"),
                     ($"/projects/{pid}/documents", "documents-toolbar"),
                 })
        {
            await GoToAsync(path);
            var bar = Page.GetByTestId(testId);
            await Expect(bar).ToBeVisibleAsync();
            await AssertNoOverlapAsync(bar, ":scope > *:not(.flex-grow-1)");
        }
    }

    /// <summary>
    /// Transactions, Budget and Cashflow show the period selector in the top bar, after the project switcher and
    /// before the sync status, gear and account menu; below 1200px it takes a second row under the bar. The views'
    /// own toolbars carry none, and the page never scrolls sideways.
    /// </summary>
    [TestCase(1280)]
    [TestCase(390)]
    public async Task Period_selector_sits_in_the_top_bar(int width)
    {
        await Page.SetViewportSizeAsync(width, 900);
        var pid = E2ESeed.ProjectId;
        foreach (var (path, toolbarSelector) in new[]
                 {
                     ($"/projects/{pid}/browse?year=2026&month=3", "[data-testid=browse-toolbar]"),
                     ($"/projects/{pid}/budgets", "[data-testid=budget-toolbar]"),
                     ($"/projects/{pid}/cashflow", ".page-toolbar"),
                 })
        {
            await GoToAsync(path);
            var topbar = Page.GetByTestId("app-topbar");
            var nav = topbar.GetByTestId("period-nav");
            await Expect(nav).ToBeVisibleAsync();
            await Expect(Page.Locator(toolbarSelector).First.GetByTestId("period-nav")).ToHaveCountAsync(0);
            var switcher = (await topbar.GetByTestId("project-selector").BoundingBoxAsync())!;
            var user = (await topbar.GetByTestId("user-menu").BoundingBoxAsync())!;
            var bar = (await topbar.BoundingBoxAsync())!;
            var box = (await nav.BoundingBoxAsync())!;
            if (width >= 1200)
            {
                Assert.That(box.X, Is.GreaterThanOrEqualTo(switcher.X + switcher.Width - 1), $"{path}: right of the project switcher");
                Assert.That(box.X + box.Width, Is.LessThanOrEqualTo(user.X + 1), $"{path}: left of the account menu");
                Assert.That(box.Y + box.Height / 2, Is.EqualTo(switcher.Y + switcher.Height / 2).Within(4), $"{path}: on the bar's row");
            }
            else
            {
                Assert.That(box.Y, Is.GreaterThanOrEqualTo(switcher.Y + switcher.Height - 1), $"{path}: on a row under the switcher");
                Assert.That(box.Y + box.Height, Is.LessThanOrEqualTo(bar.Y + bar.Height + 1), $"{path}: inside the bar");
            }
            // The page's sticky toolbar starts under the (taller) bar instead of under its first row.
            var toolbar = (await Page.Locator(toolbarSelector).First.BoundingBoxAsync())!;
            Assert.That(toolbar.Y, Is.GreaterThanOrEqualTo(bar.Y + bar.Height - 1), $"{path}: the toolbar sits below the bar");
            // Polls so a frame mid-render (before the bar wraps) is not taken for the final layout.
            try { await Page.WaitForFunctionAsync("() => document.documentElement.scrollWidth <= window.innerWidth", null, new() { Timeout = 5000 }); }
            catch (TimeoutException) { /* the assertion below names the overflowing elements */ }
            Assert.That(await Page.EvaluateAsync<string>(@"() => document.documentElement.scrollWidth <= window.innerWidth ? '' :
                [...document.querySelectorAll('body *')].filter(e => e.getBoundingClientRect().right > window.innerWidth + 1)
                    .slice(0, 5).map(e => e.tagName + '.' + e.className).join(' | ')"), Is.Empty,
                $"{path}: no horizontal scroll");
        }

        await GoToAsync($"/projects/{pid}/portfolio");
        await Expect(Page.GetByTestId("app-topbar")).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("period-nav")).ToHaveCountAsync(0);
    }

    /// <summary>
    /// A busy account (IBAN info line, balance, a full month of rows) in a tall window: the browse column is a
    /// height-bound flex column, and the long ledger used to squeeze the two-row toolbar down to its 48px
    /// min-height so its second row (period, search, Filter) was drawn over the summary row.
    /// </summary>
    [TestCase(2000, 1100, 260)]
    [TestCase(2560, 1300, 640)]
    [TestCase(1280, 900, 180)]
    [TestCase(1024, 768, 640)]
    [TestCase(1600, 900, 520)]
    [TestCase(390, 844, 260)]
    public async Task Toolbar_and_summary_stack_for_a_busy_account(int width, int height, int sidebar)
    {
        var accountId = SourceScope.Namespace(E2ESeed.SourceId, 77);
        var factory = Factory.Services.GetRequiredService<SystemDbContextFactory>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Accounts.Add(new Account
            {
                Id = accountId,
                SourceId = E2ESeed.SourceId,
                Name = "Busy Giro",
                Iban = "DE89370400440532013000",
                Type = 1,
                Currency = "EUR",
                Balances = "11:-376.120000,3:EUR,",
            });
            await db.SaveChangesAsync();
            db.ProjectAccountAssignments.Add(new ProjectAccountAssignment { ProjectId = E2ESeed.ProjectId, AccountId = accountId });
            for (var i = 0; i < 120; i++)
            {
                var date = new DateTime(2026, 11, 1 + i % 28, 0, 0, 0, DateTimeKind.Utc);
                db.Transactions.Add(new Transaction
                {
                    Id = 7000 + i,
                    SourceId = E2ESeed.SourceId,
                    AccountId = accountId,
                    Amount = -(100 + i),
                    CategoryId = 200,
                    Currency = "EUR",
                    Name = $"Payee {i}",
                    Timestamp = ((DateTimeOffset)date).ToUnixTimeSeconds(),
                    ValueTimestamp = ((DateTimeOffset)date).ToUnixTimeSeconds(),
                });
            }
            await db.SaveChangesAsync();
        }

        await Page.SetViewportSizeAsync(width, height);
        await Page.AddInitScriptAsync($"try {{ localStorage.setItem('ob.sidebarWidth', '{sidebar}'); }} catch (e) {{}}");
        await Page.EmulateMediaAsync(new PageEmulateMediaOptions { ColorScheme = ColorScheme.Dark });
        await GoToAsync($"/projects/{E2ESeed.ProjectId}/browse?year=2026&month=11&sel=acct-{accountId}");
        var toolbar = Page.GetByTestId("browse-toolbar");
        var summary = Page.GetByTestId("browse-summary");
        await Expect(summary).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("filter-toggle")).ToBeVisibleAsync();

        var tb = (await toolbar.BoundingBoxAsync())!;
        var scrollHeight = await toolbar.EvaluateAsync<double>("e => e.scrollHeight");
        Assert.That(scrollHeight, Is.LessThanOrEqualTo(tb.Height + 1), "the toolbar is as tall as its content");
        await AssertNoOverlapAsync(toolbar,
            ".browse-ident, .browse-balance, .period-stepper, .segmented, .browse-search, [data-testid=filter-toggle], [data-testid=account-documents]");
        await AssertBelowAsync(toolbar, summary);
        var docOverflow = await Page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > window.innerWidth + 1");
        Assert.That(docOverflow, Is.False, "no horizontal page scroll");

        // The same rows under a category with its rules bar open: toolbar, rules bar and summary stack, and the
        // whole "+ Rule" button takes clicks (it used to be partly covered when the squeezed rows overlapped).
        await GoToAsync($"/projects/{E2ESeed.ProjectId}/browse?year=2026&month=11&sel=cat-200&rules=1");
        var rulesBar = Page.GetByTestId("rules-bar");
        var add = Page.GetByTestId("rules-add");
        await Expect(add).ToBeVisibleAsync();
        await Expect(summary).ToBeVisibleAsync();
        await AssertBelowAsync(toolbar, rulesBar);
        await AssertBelowAsync(rulesBar, summary);
        var barBox = (await rulesBar.BoundingBoxAsync())!;
        Assert.That(await rulesBar.EvaluateAsync<double>("e => e.scrollHeight"), Is.LessThanOrEqualTo(barBox.Height + 1),
            "the rules bar is as tall as its content");
        var misses = await add.EvaluateAsync<string[]>(@"b => {
            const r = b.getBoundingClientRect(), out = [];
            for (let fx = 0.05; fx < 1; fx += 0.15) for (let fy = 0.1; fy < 1; fy += 0.2) {
                const hit = document.elementFromPoint(r.left + r.width * fx, r.top + r.height * fy);
                if (!hit || !b.contains(hit)) out.push(Math.round(fx * 100) + '%,' + Math.round(fy * 100) + '% -> ' + (hit ? hit.tagName + '.' + hit.className : 'none'));
            }
            return out;
        }");
        Assert.That(misses, Is.Empty, "every point of '+ Rule' hits the button");
    }

    /// <summary>Asserts the visible elements matching <paramref name="selector"/> inside the container are
    /// pairwise disjoint and lie within the container's box.</summary>
    private async Task AssertNoOverlapAsync(ILocator container, string selector)
    {
        var outer = (await container.BoundingBoxAsync())!;
        var boxes = new List<(string Name, LocatorBoundingBoxResult Box)>();
        var items = container.Locator(selector);
        for (var i = 0; i < await items.CountAsync(); i++)
        {
            var item = items.Nth(i);
            if (!await item.IsVisibleAsync()) continue;
            var box = await item.BoundingBoxAsync();
            if (box is null || box.Width < 1 || box.Height < 1) continue;
            boxes.Add((await item.EvaluateAsync<string>("e => e.className || e.tagName"), box));
        }
        Assert.That(boxes, Has.Count.GreaterThan(1), "found the header controls");

        foreach (var (name, b) in boxes)
        {
            Assert.That(b.Y, Is.GreaterThanOrEqualTo(outer.Y - 1), $"{name} sticks out above the header");
            Assert.That(b.Y + b.Height, Is.LessThanOrEqualTo(outer.Y + outer.Height + 1), $"{name} sticks out below the header");
            Assert.That(b.X + b.Width, Is.LessThanOrEqualTo(outer.X + outer.Width + 1), $"{name} sticks out to the right");
        }
        for (var i = 0; i < boxes.Count; i++)
            for (var j = i + 1; j < boxes.Count; j++)
            {
                var (an, a) = boxes[i];
                var (bn, b) = boxes[j];
                var overlap = a.X < b.X + b.Width - 1 && b.X < a.X + a.Width - 1 && a.Y < b.Y + b.Height - 1 && b.Y < a.Y + a.Height - 1;
                Assert.That(overlap, Is.False, $"'{an}' overlaps '{bn}'");
            }
    }

    private async Task AssertBelowAsync(ILocator header, ILocator next)
    {
        var h = (await header.BoundingBoxAsync())!;
        var n = (await next.BoundingBoxAsync())!;
        Assert.That(n.Y, Is.GreaterThanOrEqualTo(h.Y + h.Height - 1), "the row below starts under the header");
    }
}
