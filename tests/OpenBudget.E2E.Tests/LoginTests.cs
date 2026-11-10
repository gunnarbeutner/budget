using System.Text.RegularExpressions;
using Microsoft.Playwright;
using NUnit.Framework;

namespace OpenBudget.E2E.Tests;

/// <summary>Login and sign-out through the real login form, and the app-wide login requirement.</summary>
[TestFixture]
public sealed class LoginTests : EndToEndTestBase
{
    protected override bool SignInAsOwner => false;

    [Test]
    public async Task Logged_out_visitors_are_sent_to_the_login_page()
    {
        await Page.GotoAsync("/projects/1/browse");

        await Expect(Page).ToHaveURLAsync(new Regex(".*/Account/Login.*"));
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Sign in" })).ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Sign in with a passkey" })).ToBeVisibleAsync();
    }

    [Test]
    public async Task A_wrong_password_shows_an_error_and_stays_on_the_login_page()
    {
        await Page.GotoAsync("/Account/Login");
        await Page.GetByLabel("Username").FillAsync(E2ESeed.UserName);
        await Page.GetByLabel("Password").FillAsync("not-the-password");
        await Page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();

        await Expect(Page.GetByText("Invalid username or password.")).ToBeVisibleAsync();
        await Expect(Page).ToHaveURLAsync(new Regex(".*/Account/Login.*"));
    }

    [Test]
    public async Task Signing_in_returns_to_the_requested_page_and_shows_the_user()
    {
        await Page.GotoAsync($"/projects/{E2ESeed.ProjectId}/browse");
        await Expect(Page).ToHaveURLAsync(new Regex(".*/Account/Login.*"));

        await Page.GetByLabel("Username").FillAsync(E2ESeed.UserName);
        await Page.GetByLabel("Password").FillAsync(E2ESeed.Password);
        await Page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();

        await Expect(Page).ToHaveURLAsync(new Regex($".*/projects/{E2ESeed.ProjectId}/browse.*"));
        await Expect(Page.GetByTestId("user-menu").Locator("summary")).ToHaveAttributeAsync("title", E2ESeed.UserName);   // the avatar in the top bar
    }

    [Test]
    public async Task Signing_out_ends_the_session()
    {
        await LoginAsync(E2ESeed.UserName);
        await GoToAsync("/");
        await Expect(Page.Locator("[data-testid=project-selector] .project-selector-name")).ToHaveTextAsync(E2ESeed.ProjectName);

        await LogoutAsync();

        await Page.GotoAsync("/");
        await Expect(Page).ToHaveURLAsync(new Regex(".*/Account/Login.*"));
    }

    [Test]
    public async Task The_account_page_offers_password_change_and_passkeys()
    {
        await LoginAsync(E2ESeed.UserName);

        await Page.GotoAsync("/Account");

        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Passkeys" })).ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Add passkey" })).ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Change password" })).ToBeVisibleAsync();
        await Expect(Page.GetByText("No passkeys registered yet.")).ToBeVisibleAsync();
    }

    [Test]
    public async Task A_passkey_can_be_registered_and_used_to_sign_in()
    {
        // WebAuthn refuses IP addresses as the relying party, so run this ceremony on localhost.
        var origin = Factory.BaseUrl.Replace("127.0.0.1", "localhost", StringComparison.Ordinal);

        // A virtual platform authenticator (like Touch ID) that approves every request.
        var cdp = await Page.Context.NewCDPSessionAsync(Page);
        await cdp.SendAsync("WebAuthn.enable");
        await cdp.SendAsync("WebAuthn.addVirtualAuthenticator", new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["options"] = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["protocol"] = "ctap2",
                ["transport"] = "internal",
                ["hasResidentKey"] = true,
                ["hasUserVerification"] = true,
                ["isUserVerified"] = true,
                ["automaticPresenceSimulation"] = true,
            },
        });

        // Register a passkey on the account page.
        await Page.GotoAsync(origin + "/Account/Login");
        await Page.GetByLabel("Username").FillAsync(E2ESeed.UserName);
        await Page.GetByLabel("Password").FillAsync(E2ESeed.Password);
        await Page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();
        await Expect(Page.GetByTestId("user-menu").Locator("summary")).ToHaveAttributeAsync("title", E2ESeed.UserName);   // the avatar in the top bar
        await Page.GotoAsync(origin + "/Account");
        await Page.GetByRole(AriaRole.Button, new() { Name = "Add passkey" }).ClickAsync();
        await Expect(Page.GetByText("could not be registered")).Not.ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Remove" })).ToBeVisibleAsync();

        // Sign out, then sign back in with the passkey alone.
        await Page.Context.ClearCookiesAsync();
        await Page.GotoAsync(origin + "/Account/Login");
        await Page.GetByRole(AriaRole.Button, new() { Name = "Sign in with a passkey" }).ClickAsync();
        await Expect(Page.GetByTestId("user-menu").Locator("summary")).ToHaveAttributeAsync("title", E2ESeed.UserName);   // the avatar in the top bar
    }
}
