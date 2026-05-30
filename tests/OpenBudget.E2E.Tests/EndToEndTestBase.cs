using Microsoft.Playwright;
using Microsoft.Playwright.NUnit;
using NUnit.Framework;

namespace OpenBudget.E2E.Tests;

/// <summary>
/// Base for browser E2E tests. Gives each test a freshly seeded DB (no host restart) and a helper
/// to navigate to the running app. The browser locale is pinned to English so the localized
/// (resx-driven) UI text the tests match is deterministic.
/// </summary>
public abstract class EndToEndTestBase : PageTest
{
    public override BrowserNewContextOptions ContextOptions() => new()
    {
        Locale = "en-US",
        // Blazor Server pushes UI over a SignalR websocket; give navigations a moment to settle.
        BaseURL = AppFixture.BaseUrl,
    };

    [SetUp]
    public async Task ResetData() => await AppFixture.Factory.ResetAndSeedAsync();

    /// <summary>Navigate to a path on the app and wait for the Blazor circuit to render.</summary>
    protected async Task GoToAsync(string path)
    {
        await Page.GotoAsync(path, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
    }
}
