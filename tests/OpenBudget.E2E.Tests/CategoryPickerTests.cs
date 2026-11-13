using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using NUnit.Framework;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.E2E.Tests;

/// <summary>
/// The category picker as a tree: muted group headers that the keyboard skips, colored ribbons, search that keeps the
/// headers above a hit, picking, and the account filter's category picker. The editing world gets a nested "Personal"
/// group (Travel, Gifts) on top.
/// </summary>
[TestFixture]
public sealed class CategoryPickerTests : EndToEndTestBase
{
    private static string UncategorizedUrl => $"/projects/{E2ESeed.ProjectId}/browse?year=2026&month=3&sel=uncategorized";

    [SetUp]
    public async Task SeedTree()
    {
        var factory = Factory.Services.GetRequiredService<SystemDbContextFactory>();
        await EditingSeed.SeedAsync(factory);
        await using var db = await factory.CreateDbContextAsync();
        var source = E2ESeed.OwnerUserId;
        db.CategoryGroups.Add(new CategoryGroup { Id = 110, OwnerUserId = source, Name = "Personal" });
        db.CategoryGroups.Add(new CategoryGroup { Id = 111, OwnerUserId = source, Name = "Travel", ParentGroupId = 110 });
        db.CategoryGroups.Add(new CategoryGroup { Id = 112, OwnerUserId = source, Name = "Gifts", ParentGroupId = 110 });
        db.Categories.Add(new Category { Id = 210, OwnerUserId = source, Name = "Fuel", GroupId = 111, Type = 1, Color = "#d9480f" });
        db.Categories.Add(new Category { Id = 211, OwnerUserId = source, Name = "Train", GroupId = 111, Type = 1, Color = "#1971c2" });
        db.Categories.Add(new Category { Id = 212, OwnerUserId = source, Name = "Birthday", GroupId = 112, Type = 1, Color = "#c2255c" });
        db.Categories.Add(new Category { Id = 213, OwnerUserId = source, Name = "Hobbies", GroupId = 110, Type = 1, Color = "#2f9e44" });
        await db.SaveChangesAsync();
    }

    private ILocator Picker => Page.GetByTestId("category-picker");

    private async Task OpenPickerAsync()
    {
        await GoToAsync(UncategorizedUrl);
        await Page.GetByTestId("tx-row").Filter(new() { HasText = "−25,00" }).ClickAsync();
        await Page.Keyboard.PressAsync("c");
        await Expect(Picker).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("category-picker-search")).ToBeFocusedAsync();
    }

    [Test]
    public async Task Tree_shows_muted_group_headers_that_the_arrow_keys_skip()
    {
        await OpenPickerAsync();
        var groups = Picker.GetByTestId("category-picker-group");
        await Expect(groups.Filter(new() { HasText = "Personal" })).ToBeVisibleAsync();
        await Expect(groups.Filter(new() { HasText = "Travel" })).ToBeVisibleAsync();
        // Nested rows are indented further than their header.
        var personal = (await groups.Filter(new() { HasText = "Personal" }).Locator(".cp-ribbon").BoundingBoxAsync())!;
        var fuel = (await Picker.GetByTestId("category-picker-option").Filter(new() { HasText = "Fuel" }).Locator(".cp-ribbon").BoundingBoxAsync())!;
        Assert.That(fuel.X, Is.GreaterThan(personal.X + 30));

        var pickable = await Picker.Locator("button[role=option]").CountAsync();
        for (var i = 1; i < pickable + 2; i++)
        {
            await Page.Keyboard.PressAsync("ArrowDown");
            var active = await Picker.Locator(".cp-active").EvaluateAsync<string>("e => e.tagName + ':' + e.getAttribute('role')");
            Assert.That(active, Is.EqualTo("BUTTON:option"), "the highlight never lands on a group header");
        }
        // At the bottom: the last category of the tree.
        var last = await Picker.Locator("button[role=option]").Last.InnerTextAsync();
        await Expect(Picker.Locator(".cp-active")).ToHaveTextAsync(last);
        await Page.Keyboard.PressAsync("Escape");
        await Expect(Picker).ToHaveCountAsync(0);
    }

    [Test]
    public async Task Search_keeps_the_ancestor_headers_and_enter_picks_the_hit()
    {
        await OpenPickerAsync();
        await Page.GetByTestId("category-picker-search").FillAsync("fuel");
        await Expect(Picker.GetByTestId("category-picker-option")).ToHaveCountAsync(1);
        await Expect(Picker.GetByTestId("category-picker-option")).ToContainTextAsync("Fuel");
        await Expect(Picker.GetByTestId("category-picker-group")).ToHaveTextAsync(["Personal", "Travel"]);
        await Expect(Picker.Locator(".cp-active")).ToContainTextAsync("Fuel");

        await Page.Keyboard.PressAsync("Enter");
        await Expect(Picker).ToHaveCountAsync(0);
        var factory = Factory.Services.GetRequiredService<SystemDbContextFactory>();
        await using var db = await factory.CreateDbContextAsync();
        Assert.That((await db.Transactions.AsNoTracking().SingleAsync(t => t.Id == 101)).CategoryId, Is.EqualTo(210));
    }

    [Test]
    public async Task The_current_category_is_marked_and_clicking_another_picks_it()
    {
        await GoToAsync($"/projects/{E2ESeed.ProjectId}/browse?year=2026&month=3&sel=acct-{EditingSeed.BankAccountId}");
        await Page.GetByTestId("tx-row").Filter(new() { HasText = "Example Employer" }).ClickAsync();
        await Page.Keyboard.PressAsync("c");
        await Expect(Picker.Locator("[data-testid=category-picker-option].cp-current")).ToContainTextAsync("Salary");
        await Expect(Picker.Locator("[data-testid=category-picker-option].cp-current .cp-check")).ToBeVisibleAsync();

        await Picker.GetByTestId("category-picker-option").Filter(new() { HasText = "Train" }).ClickAsync();
        await Expect(Picker).ToHaveCountAsync(0);
        var factory = Factory.Services.GetRequiredService<SystemDbContextFactory>();
        await using var db = await factory.CreateDbContextAsync();
        Assert.That((await db.Transactions.AsNoTracking().SingleAsync(t => t.Id == 104)).CategoryId, Is.EqualTo(211));
    }

    [Test]
    public async Task The_account_filter_picks_a_category_by_search_and_any_category_clears_it()
    {
        await GoToAsync($"/projects/{E2ESeed.ProjectId}/browse?year=2026&month=3&sel=acct-{EditingSeed.BankAccountId}");
        var rows = Page.GetByTestId("tx-row");
        await Expect(rows.Filter(new() { HasText = "Example Employer" })).ToBeVisibleAsync();
        var all = await rows.CountAsync();
        await Page.GetByTestId("filter-toggle").ClickAsync();
        var button = Page.GetByTestId("filter-category");
        await Expect(button).ToHaveTextAsync("Any category");

        await button.ClickAsync();
        await Expect(Page.GetByTestId("category-picker-search")).ToBeFocusedAsync();
        await Page.GetByTestId("category-picker-search").FillAsync("salar");
        await Expect(Picker.Locator(".cp-active")).ToContainTextAsync("Salary");
        await Page.Keyboard.PressAsync("Enter");
        await Expect(Picker).ToHaveCountAsync(0);
        await Expect(button).ToHaveTextAsync("Salary");
        await Expect(Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("[?&]cat=\\d+"));
        await Expect(rows.Filter(new() { HasText = "Example Employer" })).ToBeVisibleAsync();
        Assert.That(await rows.CountAsync(), Is.LessThan(all));

        await button.ClickAsync();
        await Expect(Picker.Locator("[data-testid=category-picker-option].cp-current")).ToContainTextAsync("Salary");
        await Picker.GetByTestId("category-picker-remove").ClickAsync();
        await Expect(button).ToHaveTextAsync("Any category");
        await Expect(rows).ToHaveCountAsync(all);
    }

    // Set OB_PICKER_SCREENSHOT_DIR to keep a full-page and a cropped screenshot of the open picker for review.
    [TestCase("light")]
    [TestCase("dark")]
    public async Task Screenshot(string theme)
    {
        await Page.SetViewportSizeAsync(1200, 800);
        await Page.EmulateMediaAsync(new PageEmulateMediaOptions { ColorScheme = theme == "dark" ? ColorScheme.Dark : ColorScheme.Light });
        await OpenPickerAsync();
        await Page.Keyboard.PressAsync("ArrowDown");
        await Page.Keyboard.PressAsync("ArrowDown");
        var dir = Environment.GetEnvironmentVariable("OB_PICKER_SCREENSHOT_DIR");
        if (string.IsNullOrEmpty(dir)) return;
        await Page.ScreenshotAsync(new() { Path = Path.Combine(dir, $"picker-{theme}.png") });
        var box = (await Picker.BoundingBoxAsync())!;
        await Page.ScreenshotAsync(new()
        {
            Path = Path.Combine(dir, $"picker-{theme}-crop.png"),
            Clip = new() { X = box.X - 20, Y = box.Y - 20, Width = box.Width + 40, Height = box.Height + 40 },
        });
    }

    // A short window with the clicked row in its lower part: the picker opens upward over the free space, stays inside
    // the window and shows a good part of the tree without scrolling.
    [Test]
    public async Task Opens_upward_and_uses_the_free_space_near_the_bottom_of_a_short_window()
    {
        await Page.SetViewportSizeAsync(1200, 520);
        await GoToAsync(UncategorizedUrl);
        var pill = Page.GetByTestId("tx-row").Last.GetByTestId("tx-category");
        await pill.ScrollIntoViewIfNeededAsync();
        var pillBox = (await pill.BoundingBoxAsync())!;
        Assert.That(pillBox.Y, Is.GreaterThan(260), "the row sits in the lower half");
        await pill.ClickAsync();
        await Expect(Picker).ToBeVisibleAsync();
        await Expect(Picker).ToHaveAttributeAsync("data-placement", "above");

        var box = (await Picker.BoundingBoxAsync())!;
        Assert.That(box.Y, Is.GreaterThanOrEqualTo(0));
        Assert.That(box.Y + box.Height, Is.LessThanOrEqualTo(pillBox.Y + pillBox.Height + 1), "grows upward from the click");
        Assert.That(box.X + box.Width, Is.LessThanOrEqualTo(1200));
        Assert.That(box.Height, Is.GreaterThan(240), "uses the space above instead of a small fixed height");
        var shown = await Picker.Locator(".cp-list").EvaluateAsync<int>(
            "l => { const b = l.getBoundingClientRect(); return [...l.children].filter(e => { const r = e.getBoundingClientRect(); return r.height > 0 && r.top >= b.top - 1 && r.bottom <= b.bottom + 1; }).length; }");
        Assert.That(shown, Is.GreaterThanOrEqualTo(8));
        await Expect(Page.GetByTestId("category-picker-search")).ToBeInViewportAsync();

        // A taller window gives room below again.
        await Page.Keyboard.PressAsync("Escape");
        await Page.SetViewportSizeAsync(1200, 1000);
        await pill.ClickAsync();
        await Expect(Picker).ToHaveAttributeAsync("data-placement", "below");
    }

    // The selection bar sits above the list: it never covers the first row, on desktop and on a phone, light and dark.
    [TestCase(1200, "light")]
    [TestCase(1200, "dark")]
    [TestCase(390, "light")]
    public async Task Action_bar_does_not_cover_the_first_row(int width, string theme)
    {
        await Page.SetViewportSizeAsync(width, 800);
        await Page.EmulateMediaAsync(new PageEmulateMediaOptions { ColorScheme = theme == "dark" ? ColorScheme.Dark : ColorScheme.Light });
        await GoToAsync(UncategorizedUrl);
        var first = Page.GetByTestId("tx-row").First;
        await first.Locator(".tx-amount").ClickAsync();
        var bar = Page.GetByTestId("action-bar");
        await Expect(bar).ToBeVisibleAsync();
        var b = (await bar.BoundingBoxAsync())!;
        var r = (await first.BoundingBoxAsync())!;
        Assert.That(b.Y + b.Height, Is.LessThanOrEqualTo(r.Y + 0.5), "bar ends above the first row");
        var bg = await bar.EvaluateAsync<string>("e => getComputedStyle(e).backgroundColor");
        Assert.That(bg, Does.Not.Contain("rgba"), "opaque background");
    }
}
