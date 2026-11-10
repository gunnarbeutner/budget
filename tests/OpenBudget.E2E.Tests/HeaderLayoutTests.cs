using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using NUnit.Framework;
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
        await EditingSeed.SeedAsync(AppFixture.Factory.Services.GetRequiredService<SystemDbContextFactory>());

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

    /// <summary>Asserts the visible elements matching <paramref name="selector"/> inside the container are
    /// pairwise disjoint and lie within the container's box.</summary>
    private static async Task AssertNoOverlapAsync(ILocator container, string selector)
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

    private static async Task AssertBelowAsync(ILocator header, ILocator next)
    {
        var h = (await header.BoundingBoxAsync())!;
        var n = (await next.BoundingBoxAsync())!;
        Assert.That(n.Y, Is.GreaterThanOrEqualTo(h.Y + h.Height - 1), "the row below starts under the header");
    }
}
