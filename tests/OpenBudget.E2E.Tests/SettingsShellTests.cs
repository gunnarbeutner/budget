using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using NUnit.Framework;
using OpenBudget.Services;

namespace OpenBudget.E2E.Tests;

/// <summary>
/// The settings pages (project settings, budget editor, bank logins, sources) share the views' compact header.
/// </summary>
[TestFixture]
public sealed class SettingsShellTests : EndToEndTestBase
{
    [SetUp]
    public async Task SeedAccounts() =>
        await EditingSeed.SeedAsync(Factory.Services.GetRequiredService<SystemDbContextFactory>());

    /// <summary>
    /// Every settings page opens with the compact toolbar header (no home icon) inside a frame with navigation:
    /// the project sidebar for project settings, the settings list for the app-level pages.
    /// </summary>
    [TestCase("/banklogins", "settings-nav")]
    [TestCase("/portfolio-performance", "settings-nav")]
    [TestCase("/portfolio-performance/depots", "settings-nav")]
    [TestCase("/portfolio-performance/instruments", "settings-nav")]
    [TestCase("/portfolio-performance/tax", "settings-nav")]
    [TestCase("/Account", "settings-nav")]
    [TestCase("/projects/1/settings", "project-sidebar")]
    [TestCase("/projects/1/budgets/new", "project-sidebar")]
    [TestCase("/budgets/1/edit", "project-sidebar")]
    public async Task Settings_page_has_the_compact_header(string path, string frame)
    {
        if (path == "/Account") await Page.GotoAsync(path);
        else await GoToAsync(path);
        var header = Page.GetByTestId("page-header");
        await Expect(header).ToBeVisibleAsync();
        await Expect(header.Locator("h1.view-title")).ToBeVisibleAsync();
        await Expect(Page.GetByTestId(frame)).ToBeVisibleAsync();
        await Expect(Page.Locator(".bi-house")).ToHaveCountAsync(0);
    }

    [Test]
    public async Task Settings_list_highlights_the_page_and_leads_back_to_the_project()
    {
        await GoToAsync($"/projects/{E2ESeed.ProjectId}/browse");
        await Page.GetByTestId("gear-menu").Locator("summary").ClickAsync();
        await Page.GetByTestId("gear-menu").GetByTestId("gear-bank-logins").ClickAsync();
        await Expect(Page.GetByTestId("settings-nav-banklogins")).ToHaveAttributeAsync("aria-current", "page");
        await Page.GetByTestId("settings-nav-portfolio-performance").ClickAsync();
        await Expect(Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex("/portfolio-performance$"));
        await Expect(Page.GetByTestId("settings-nav-portfolio-performance")).ToHaveAttributeAsync("aria-current", "page");
        await Expect(Page.GetByTestId("settings-back")).ToContainTextAsync(E2ESeed.ProjectName);
        await Page.GetByTestId("settings-back").ClickAsync();
        await Expect(Page).ToHaveURLAsync(new System.Text.RegularExpressions.Regex($"/projects/{E2ESeed.ProjectId}/browse"));
    }

    [Test]
    public async Task Settings_pages_fit_a_phone()
    {
        await Page.SetViewportSizeAsync(390, 844);
        foreach (var path in new[] { "/banklogins", "/portfolio-performance", $"/projects/{E2ESeed.ProjectId}/settings" })
        {
            await GoToAsync(path);
            await Expect(Page.GetByTestId("page-header")).ToBeVisibleAsync();
            var overflow = await Page.EvaluateAsync<bool>("document.documentElement.scrollWidth > document.documentElement.clientWidth");
            Assert.That(overflow, Is.False, $"{path} scrolls horizontally");
        }
    }

    // Set OB_SETTINGS_SCREENSHOT_DIR (and optionally OB_SETTINGS_SCREENSHOT_TAG) to keep desktop screenshots of
    // the bank logins and project settings pages in both themes for review.
    [TestCase("light")]
    [TestCase("dark")]
    public async Task Screenshot(string theme)
    {
        var dir = Environment.GetEnvironmentVariable("OB_SETTINGS_SCREENSHOT_DIR");
        if (string.IsNullOrEmpty(dir)) Assert.Ignore("OB_SETTINGS_SCREENSHOT_DIR not set");
        var tag = Environment.GetEnvironmentVariable("OB_SETTINGS_SCREENSHOT_TAG") ?? "after";
        await Page.SetViewportSizeAsync(1280, 860);
        await Page.EmulateMediaAsync(new PageEmulateMediaOptions { ColorScheme = theme == "dark" ? ColorScheme.Dark : ColorScheme.Light });
        foreach (var (name, path) in new[] { ("banklogins", "/banklogins"), ("project", $"/projects/{E2ESeed.ProjectId}/settings") })
        {
            await GoToAsync(path);
            await Page.WaitForTimeoutAsync(500);
            await Page.ScreenshotAsync(new() { Path = Path.Combine(dir, $"settings-{name}-{tag}-{theme}.png") });
        }
    }
}
