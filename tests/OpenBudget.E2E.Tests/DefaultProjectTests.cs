using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using NUnit.Framework;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.E2E.Tests;

/// <summary>
/// The project settings card "Default project for new accounts": the toggle (with its dashboard badge),
/// its refusal and automatic clearing on shared projects, and the one-time "assign all accounts that are
/// in no project yet" button (with a confirmation on shared projects).
/// </summary>
[TestFixture]
public sealed class DefaultProjectTests : EndToEndTestBase
{
    private string SettingsUrl => $"/projects/{E2ESeed.ProjectId}/settings";
    private ILocator Toggle => Page.Locator("#default-project-toggle");

    private static async Task SeedUnassignedAccountsAsync()
    {
        var factory = AppFixture.Factory.Services.GetRequiredService<SystemDbContextFactory>();
        await using var db = await factory.CreateDbContextAsync();
        db.Accounts.Add(new Account { Id = SourceScope.Namespace(E2ESeed.SourceId, 1), SourceId = E2ESeed.SourceId, Name = "Example Giro" });
        db.Accounts.Add(new Account { Id = SourceScope.Namespace(E2ESeed.SourceId, 2), SourceId = E2ESeed.SourceId, Name = "Example Savings" });
        await db.SaveChangesAsync();
    }

    private static async Task ShareDirectlyAsync()
    {
        var factory = AppFixture.Factory.Services.GetRequiredService<SystemDbContextFactory>();
        await using var db = await factory.CreateDbContextAsync();
        var guest = await db.Users.SingleAsync(u => u.UserName == E2ESeed.SecondUserName);
        db.ProjectShares.Add(new ProjectShare { ProjectId = E2ESeed.ProjectId, UserId = guest.Id, Role = ProjectRole.Viewer });
        await db.SaveChangesAsync();
    }

    [Test]
    public async Task The_toggle_marks_the_project_as_default_and_the_dashboard_shows_a_badge()
    {
        await GoToAsync(SettingsUrl);
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Default project for new accounts" })).ToBeVisibleAsync();
        await Expect(Toggle).Not.ToBeCheckedAsync();

        await Toggle.CheckAsync();
        await Expect(Toggle).ToBeCheckedAsync();

        await GoToAsync("/");
        await Expect(Page.GetByTestId("default-project-badge")).ToBeVisibleAsync();

        await GoToAsync(SettingsUrl);
        await Toggle.UncheckAsync();
        await Expect(Toggle).Not.ToBeCheckedAsync();
        await GoToAsync("/");
        await Expect(Page.GetByTestId("default-project-badge")).ToHaveCountAsync(0);
    }

    [Test]
    public async Task A_shared_project_cannot_be_the_default_and_the_toggle_explains_why()
    {
        await ShareDirectlyAsync();
        await GoToAsync(SettingsUrl);

        await Expect(Toggle).ToBeDisabledAsync();
        await Expect(Page.Locator("#default-project-help")).ToContainTextAsync("shared");
    }

    [Test]
    public async Task Sharing_the_default_project_clears_it_and_shows_a_notice()
    {
        await GoToAsync(SettingsUrl);
        await Toggle.CheckAsync();
        await Expect(Toggle).ToBeCheckedAsync();

        await Page.Locator("#share-user").SelectOptionAsync(new SelectOptionValue { Label = E2ESeed.SecondUserName });
        await Page.Locator("#share-add").ClickAsync();

        await Expect(Page.Locator("#default-cleared-notice")).ToBeVisibleAsync();
        await Expect(Toggle).Not.ToBeCheckedAsync();
        await Expect(Toggle).ToBeDisabledAsync();

        await Page.Locator("#default-cleared-notice").GetByRole(AriaRole.Button, new() { Name = "Dismiss" }).ClickAsync();
        await Expect(Page.Locator("#default-cleared-notice")).ToHaveCountAsync(0);
    }

    [Test]
    public async Task The_assign_button_assigns_all_unassigned_accounts_and_reports_the_count()
    {
        await SeedUnassignedAccountsAsync();
        await GoToAsync(SettingsUrl);

        await Page.Locator("#assign-unassigned").ClickAsync();

        await Expect(Page.Locator("#assign-result")).ToHaveTextAsync("2 accounts assigned.");
        await Expect(Page.Locator("#default-project-card")).ToBeVisibleAsync();
        await Expect(Page.GetByText("Example Giro").First).ToBeVisibleAsync();

        await Page.Locator("#assign-unassigned").ClickAsync();
        await Expect(Page.Locator("#assign-result")).ToHaveTextAsync("0 accounts assigned.");
    }

    [Test]
    public async Task On_a_shared_project_the_assign_button_asks_for_confirmation_first()
    {
        await SeedUnassignedAccountsAsync();
        await ShareDirectlyAsync();
        await GoToAsync(SettingsUrl);

        await Page.Locator("#assign-unassigned").ClickAsync();
        var modal = Page.GetByTestId("modal-dialog");
        await Expect(modal).ToContainTextAsync("visible to everyone this project is shared with");

        // Cancelling assigns nothing.
        await modal.GetByRole(AriaRole.Button, new() { Name = "Cancel" }).ClickAsync();
        await Expect(Page.Locator("#assign-result")).ToHaveCountAsync(0);

        await Page.Locator("#assign-unassigned").ClickAsync();
        await Page.Locator("#assign-confirm").ClickAsync();
        await Expect(Page.Locator("#assign-result")).ToHaveTextAsync("2 accounts assigned.");
    }
}
