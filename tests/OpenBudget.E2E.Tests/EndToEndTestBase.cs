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

    /// <summary>Most tests start signed in as the baseline world's owner; the login tests opt out.</summary>
    protected virtual bool SignInAsOwner => true;

    [SetUp]
    public async Task ResetData()
    {
        await AppFixture.Factory.ResetAndSeedAsync();
        if (SignInAsOwner) await LoginAsync(E2ESeed.UserName);
    }

    /// <summary>Sign in through the real login form.</summary>
    protected async Task LoginAsync(string userName, string password = E2ESeed.Password)
    {
        await Page.GotoAsync("/Account/Login");
        await Page.GetByLabel("Username").FillAsync(userName);
        await Page.GetByLabel("Password").FillAsync(password);
        await Page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();
        await Page.WaitForURLAsync(url => !url.Contains("/Account/Login", StringComparison.Ordinal));
    }

    /// <summary>Sign out through the confirmation page (sign-out is a CSRF-protected POST).</summary>
    protected async Task LogoutAsync()
    {
        await Page.GotoAsync("/Account/Logout");
        await Page.GetByRole(AriaRole.Button, new() { Name = "Sign out" }).ClickAsync();
        await Page.WaitForURLAsync(url => url.Contains("/Account/Login", StringComparison.Ordinal));
    }

    /// <summary>Navigate to a path on the app and wait until the Blazor circuit has rendered interactively.</summary>
    /// <remarks>Network idle alone is not enough: the circuit's websocket does not count as network activity, so
    /// a test could type into or open (a &lt;details&gt; menu) the static prerender, which the first interactive render
    /// then replaces, losing the input or closing the menu. MainLayout sets <c>data-interactive</c> on the root
    /// element after that render.</remarks>
    protected async Task GoToAsync(string path)
    {
        await Page.GotoAsync(path, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });
        await WaitForInteractiveAsync();
    }

    /// <summary>Waits for the current page's circuit to render interactively (see <see cref="GoToAsync"/>).</summary>
    protected async Task WaitForInteractiveAsync() =>
        await Expect(Page.Locator("html[data-interactive]")).ToHaveCountAsync(1);
}
