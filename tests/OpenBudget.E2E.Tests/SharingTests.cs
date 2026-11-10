using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using NUnit.Framework;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.E2E.Tests;

/// <summary>
/// Project sharing through the UI: the owner shares from the project settings page, the other user
/// then sees exactly that project (read-only for a viewer), and everything else of the owner's stays
/// "not found".
/// </summary>
[TestFixture]
public sealed class SharingTests : EndToEndTestBase
{
    private const string NotFound = "Sorry, there's nothing at this address.";
    private const string HiddenProject = "Private Project";

    private async Task SeedHiddenProjectAsync()
    {
        var factory = AppFixture.Factory.Services.GetRequiredService<SystemDbContextFactory>();
        await using var db = await factory.CreateDbContextAsync();
        var owner = await db.Users.SingleAsync(u => u.UserName == E2ESeed.UserName);
        db.Projects.Add(new Project { Id = 50, Name = HiddenProject, SourceId = E2ESeed.SourceId, OwnerUserId = owner.Id, SortOrder = 5, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
    }

    private static async Task ShareDirectlyAsync(ProjectRole role)
    {
        var factory = AppFixture.Factory.Services.GetRequiredService<SystemDbContextFactory>();
        await using var db = await factory.CreateDbContextAsync();
        var guest = await db.Users.SingleAsync(u => u.UserName == E2ESeed.SecondUserName);
        db.ProjectShares.Add(new ProjectShare { ProjectId = E2ESeed.ProjectId, UserId = guest.Id, Role = role });
        await db.SaveChangesAsync();
    }

    [Test]
    public async Task Owner_shares_a_project_as_viewer_and_the_guest_sees_only_that_project()
    {
        await SeedHiddenProjectAsync();

        // Owner: share from the settings page.
        await GoToAsync($"/projects/{E2ESeed.ProjectId}/settings");
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Sharing" })).ToBeVisibleAsync();
        await Page.Locator("#share-user").SelectOptionAsync(new SelectOptionValue { Label = E2ESeed.SecondUserName });
        await Page.Locator("#share-role").SelectOptionAsync(new SelectOptionValue { Label = "Viewer" });
        await Page.Locator("#share-add").ClickAsync();
        await Expect(Page.Locator("#sharing-card").GetByRole(AriaRole.Cell, new() { Name = E2ESeed.SecondUserName })).ToBeVisibleAsync();

        // Guest: sees the shared project (and says whose it is), not the owner's other project.
        await LogoutAsync();
        await LoginAsync(E2ESeed.SecondUserName);
        await GoToAsync("/projects/manage");
        await Expect(Page.GetByRole(AriaRole.Link, new() { Name = E2ESeed.ProjectName })).ToBeVisibleAsync();
        await Expect(Page.GetByText($"Shared by {E2ESeed.UserName} · view only")).ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Link, new() { Name = HiddenProject })).ToHaveCountAsync(0);

        await Page.GetByRole(AriaRole.Link, new() { Name = E2ESeed.ProjectName }).ClickAsync();
        await Expect(Page.GetByRole(AriaRole.Navigation, new() { Name = "Project views" })).ToBeVisibleAsync();

        // A viewer has no settings cog, and the settings / budget-edit URLs are "not found".
        await Expect(Page.GetByRole(AriaRole.Link, new() { Name = "Project settings" })).ToHaveCountAsync(0);
        await GoToAsync($"/projects/{E2ESeed.ProjectId}/settings");
        await Expect(Page.GetByText(NotFound)).ToBeVisibleAsync();
        await GoToAsync($"/budgets/{E2ESeed.BudgetId}/edit");
        await Expect(Page.GetByText(NotFound)).ToBeVisibleAsync();

        // The owner's other project is not found either.
        await GoToAsync("/projects/50/browse");
        await Expect(Page.GetByText(NotFound)).ToBeVisibleAsync();

        // The budgets page works read-only: no "New Budget".
        await GoToAsync($"/projects/{E2ESeed.ProjectId}/budgets");
        await Expect(Page.GetByRole(AriaRole.Link, new() { Name = "New Budget" })).ToHaveCountAsync(0);
    }

    [Test]
    public async Task An_editor_can_edit_the_budget_but_sees_no_sharing_or_account_controls()
    {
        await ShareDirectlyAsync(ProjectRole.Editor);
        await LogoutAsync();
        await LoginAsync(E2ESeed.SecondUserName);

        await GoToAsync($"/projects/{E2ESeed.ProjectId}/budgets");
        await Expect(Page.GetByRole(AriaRole.Link, new() { Name = "New Budget" })).ToBeVisibleAsync();

        await GoToAsync($"/projects/{E2ESeed.ProjectId}/settings");
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Category Assignments" })).ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Sharing" })).ToHaveCountAsync(0);
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Accounts" })).ToHaveCountAsync(0);
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Delete Project" })).ToHaveCountAsync(0);

        await GoToAsync($"/budgets/{E2ESeed.BudgetId}/edit");
        await Expect(Page.GetByRole(AriaRole.Heading, new() { Name = "Monthly amounts" })).ToBeVisibleAsync();
    }

    [Test]
    public async Task Revoking_a_share_makes_the_project_disappear_for_the_guest()
    {
        await ShareDirectlyAsync(ProjectRole.Viewer);
        await LogoutAsync();
        await LoginAsync(E2ESeed.SecondUserName);
        await GoToAsync($"/projects/{E2ESeed.ProjectId}/browse");
        await Expect(Page.GetByRole(AriaRole.Navigation, new() { Name = "Project views" })).ToBeVisibleAsync();

        var factory = AppFixture.Factory.Services.GetRequiredService<SystemDbContextFactory>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.ProjectShares.RemoveRange(db.ProjectShares);
            await db.SaveChangesAsync();
        }

        // A fresh page load starts a new circuit, which computes its access scope from scratch.
        await GoToAsync($"/projects/{E2ESeed.ProjectId}/browse");
        await Expect(Page.GetByText(NotFound)).ToBeVisibleAsync();
    }

    [Test]
    public async Task The_sources_page_shows_only_the_users_own_sources_with_their_tokens()
    {
        await GoToAsync("/sources");
        await Expect(Page.GetByRole(AriaRole.Row).Filter(new() { HasText = "legacy" })).ToBeVisibleAsync();
        await Page.GetByRole(AriaRole.Row).Filter(new() { HasText = "legacy" }).GetByRole(AriaRole.Button, new() { Name = "Upload token" }).ClickAsync();
        await Expect(Page.Locator("[data-source-token='legacy']")).ToHaveTextAsync("e2e-token-legacy");

        await LogoutAsync();
        await LoginAsync(E2ESeed.SecondUserName);
        await GoToAsync("/sources");
        await Expect(Page.GetByText("No sources registered yet.")).ToBeVisibleAsync();
        await Expect(Page.GetByText("e2e-token-legacy")).ToHaveCountAsync(0);
    }
}
