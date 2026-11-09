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
        await Page.GotoAsync("/projects/1/overview");

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
        await Page.GotoAsync($"/projects/{E2ESeed.ProjectId}/overview");
        await Expect(Page).ToHaveURLAsync(new Regex(".*/Account/Login.*"));

        await Page.GetByLabel("Username").FillAsync(E2ESeed.UserName);
        await Page.GetByLabel("Password").FillAsync(E2ESeed.Password);
        await Page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();

        await Expect(Page).ToHaveURLAsync(new Regex($".*/projects/{E2ESeed.ProjectId}/overview.*"));
        await Expect(Page.GetByRole(AriaRole.Link, new() { Name = E2ESeed.UserName })).ToBeVisibleAsync();
    }

    [Test]
    public async Task Signing_out_ends_the_session()
    {
        await LoginAsync(E2ESeed.UserName);
        await GoToAsync("/");
        await Expect(Page.GetByRole(AriaRole.Link, new() { Name = E2ESeed.ProjectName })).ToBeVisibleAsync();

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
}
