using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using NUnit.Framework;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.E2E.Tests;

/// <summary>
/// The category picker as a tree: muted group headers that the keyboard skips, colored ribbons, search that keeps the
/// headers above a hit, and picking. The editing world gets a nested "Personal" group (Travel, Gifts) on top.
/// </summary>
[TestFixture]
public sealed class CategoryPickerTests : EndToEndTestBase
{
    private static string UncategorizedUrl => $"/projects/{E2ESeed.ProjectId}/browse?year=2026&month=3&sel=uncategorized";

    [SetUp]
    public async Task SeedTree()
    {
        var factory = AppFixture.Factory.Services.GetRequiredService<SystemDbContextFactory>();
        await EditingSeed.SeedAsync(factory);
        await using var db = await factory.CreateDbContextAsync();
        var source = E2ESeed.SourceId;
        db.CategoryGroups.Add(new CategoryGroup { Id = 110, SourceId = source, Name = "Personal" });
        db.CategoryGroups.Add(new CategoryGroup { Id = 111, SourceId = source, Name = "Travel", ParentGroupId = 110 });
        db.CategoryGroups.Add(new CategoryGroup { Id = 112, SourceId = source, Name = "Gifts", ParentGroupId = 110 });
        db.Categories.Add(new Category { Id = 210, SourceId = source, Name = "Fuel", GroupId = 111, Type = 1, Color = "#d9480f" });
        db.Categories.Add(new Category { Id = 211, SourceId = source, Name = "Train", GroupId = 111, Type = 1, Color = "#1971c2" });
        db.Categories.Add(new Category { Id = 212, SourceId = source, Name = "Birthday", GroupId = 112, Type = 1, Color = "#c2255c" });
        db.Categories.Add(new Category { Id = 213, SourceId = source, Name = "Hobbies", GroupId = 110, Type = 1, Color = "#2f9e44" });
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
        var factory = AppFixture.Factory.Services.GetRequiredService<SystemDbContextFactory>();
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
        var factory = AppFixture.Factory.Services.GetRequiredService<SystemDbContextFactory>();
        await using var db = await factory.CreateDbContextAsync();
        Assert.That((await db.Transactions.AsNoTracking().SingleAsync(t => t.Id == 104)).CategoryId, Is.EqualTo(211));
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
}
