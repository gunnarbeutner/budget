using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using NUnit.Framework;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.E2E.Tests;

/// <summary>
/// The project switcher's row context menu: opening it (right-click, Shift+F10), Settings… (rename, live in the
/// top bar and in another session's switcher), Move up / Move down, drag-and-drop reordering, Delete with the
/// typed-name confirmation, and the owner-only entries disabled for an editor.
/// </summary>
[TestFixture]
public sealed class ProjectSwitcherMenuTests : EndToEndTestBase
{
    private const string Second = "Example Market";
    private const string Third = "Example Holidays";

    private static SystemDbContextFactory Db => AppFixture.Factory.Services.GetRequiredService<SystemDbContextFactory>();

    private static async Task<(int Second, int Third)> AddProjectsAsync()
    {
        await using var db = await Db.CreateDbContextAsync();
        var first = await db.Projects.SingleAsync(p => p.Id == E2ESeed.ProjectId);
        var second = new Project { Name = Second, SourceId = first.SourceId, OwnerUserId = first.OwnerUserId, SortOrder = first.SortOrder + 1, CreatedAt = DateTime.UtcNow };
        var third = new Project { Name = Third, SourceId = first.SourceId, OwnerUserId = first.OwnerUserId, SortOrder = first.SortOrder + 2, CreatedAt = DateTime.UtcNow };
        db.Projects.AddRange(second, third);
        await db.SaveChangesAsync();
        return (second.Id, third.Id);
    }

    private ILocator Rows => Page.GetByTestId("switcher-project");
    private ILocator Row(string name) => Rows.Filter(new() { HasText = name });
    private ILocator Menu => Page.GetByTestId("context-menu");

    private async Task OpenSwitcherAsync(IPage? page = null)
    {
        page ??= Page;
        await page.Locator("[data-testid=project-selector] > summary").ClickAsync();
        await Expect(page.GetByTestId("switcher-project").First).ToBeVisibleAsync();
    }

    private async Task ExpectOrderAsync(params string[] names)
    {
        await Expect(Rows.Locator(".project-switcher-name > .text-truncate")).ToHaveTextAsync(names);
    }

    [Test]
    public async Task Right_click_opens_the_menu_with_settings_move_and_delete()
    {
        await AddProjectsAsync();
        await GoToAsync($"/projects/{E2ESeed.ProjectId}/browse");
        await OpenSwitcherAsync();

        await Row(E2ESeed.ProjectName).ClickAsync(new() { Button = MouseButton.Right });

        await Expect(Menu).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("ctx-settings")).ToBeEnabledAsync();
        await Expect(Page.GetByTestId("ctx-up")).ToBeDisabledAsync();      // already first
        await Expect(Page.GetByTestId("ctx-down")).ToBeEnabledAsync();
        await Expect(Page.GetByTestId("ctx-delete")).ToBeEnabledAsync();
        // No per-row buttons: the menu is the only way in.
        await Expect(Rows.First.Locator("button")).ToHaveCountAsync(0);

        await Page.Keyboard.PressAsync("Escape");
        await Expect(Menu).ToHaveCountAsync(0);
        await Expect(Page.Locator("[data-testid=project-selector]")).ToHaveAttributeAsync("open", "");
    }

    [Test]
    public async Task Shift_F10_opens_the_menu_and_move_down_and_up_reorder()
    {
        await AddProjectsAsync();
        await GoToAsync($"/projects/{E2ESeed.ProjectId}/browse");
        await OpenSwitcherAsync();
        await ExpectOrderAsync(E2ESeed.ProjectName, Second, Third);

        await Row(E2ESeed.ProjectName).FocusAsync();
        await Page.Keyboard.PressAsync("Shift+F10");
        await Expect(Menu).ToBeVisibleAsync();
        await Page.GetByTestId("ctx-down").ClickAsync();
        await ExpectOrderAsync(Second, E2ESeed.ProjectName, Third);

        // The switcher stays open for the next move.
        await Row(Third).ClickAsync(new() { Button = MouseButton.Right });
        await Expect(Page.GetByTestId("ctx-down")).ToBeDisabledAsync();   // already last
        await Page.GetByTestId("ctx-up").ClickAsync();
        await ExpectOrderAsync(Second, Third, E2ESeed.ProjectName);

        // The order is stored.
        await GoToAsync($"/projects/{E2ESeed.ProjectId}/browse");
        await OpenSwitcherAsync();
        await ExpectOrderAsync(Second, Third, E2ESeed.ProjectName);
    }

    [Test]
    public async Task Dragging_a_row_reorders_the_projects()
    {
        await AddProjectsAsync();
        await GoToAsync($"/projects/{E2ESeed.ProjectId}/browse");
        await OpenSwitcherAsync();

        await Row(Third).DragToAsync(Row(E2ESeed.ProjectName));
        await ExpectOrderAsync(Third, E2ESeed.ProjectName, Second);

        await Row(Third).DragToAsync(Row(Second));
        await ExpectOrderAsync(E2ESeed.ProjectName, Second, Third);
    }

    [Test]
    public async Task Settings_dialog_renames_the_project_live_in_the_top_bar_and_in_another_session()
    {
        // A second tab of the same user with its switcher open.
        var other = await Context.NewPageAsync();
        await other.GotoAsync($"/projects/{E2ESeed.ProjectId}/budgets");
        await OpenSwitcherAsync(other);

        await GoToAsync($"/projects/{E2ESeed.ProjectId}/browse");
        await OpenSwitcherAsync();
        await Row(E2ESeed.ProjectName).ClickAsync(new() { Button = MouseButton.Right });
        await Page.GetByTestId("ctx-settings").ClickAsync();

        var modal = Page.GetByTestId("modal-dialog");
        await Expect(modal).ToBeVisibleAsync();
        await Expect(Page.Locator("[data-testid=project-selector]")).Not.ToHaveAttributeAsync("open", "");
        await Expect(modal.GetByLabel("Name")).ToHaveValueAsync(E2ESeed.ProjectName);
        await Expect(modal.GetByTestId("project-settings-more")).ToHaveAttributeAsync("href", $"/projects/{E2ESeed.ProjectId}/settings");
        await modal.GetByLabel("Name").FillAsync("Example Household");
        await modal.GetByTestId("project-settings-save").ClickAsync();

        await Expect(Page.GetByTestId("modal-dialog")).ToHaveCountAsync(0);
        await Expect(Page.Locator("[data-testid=project-selector] .project-selector-name")).ToHaveTextAsync("Example Household");
        await Expect(other.GetByTestId("switcher-project").Filter(new() { HasText = "Example Household" })).ToBeVisibleAsync();
        await Expect(other.Locator("[data-testid=project-selector] .project-selector-name")).ToHaveTextAsync("Example Household");
    }

    [Test]
    public async Task Delete_needs_the_typed_name_and_removes_the_project()
    {
        await AddProjectsAsync();
        await GoToAsync($"/projects/{E2ESeed.ProjectId}/browse");
        await OpenSwitcherAsync();
        await Row(Second).ClickAsync(new() { Button = MouseButton.Right });
        await Page.GetByTestId("ctx-delete").ClickAsync();

        var confirm = Page.GetByTestId("delete-project-confirm");
        await Expect(confirm).ToBeDisabledAsync();
        await Page.GetByTestId("delete-project-name").FillAsync("example market");   // wrong case
        await Expect(confirm).ToBeDisabledAsync();
        await Page.GetByTestId("delete-project-name").FillAsync(Second);
        await Expect(confirm).ToBeEnabledAsync();
        await confirm.ClickAsync();

        await Expect(Page.GetByTestId("modal-dialog")).ToHaveCountAsync(0);
        await Expect(Page).ToHaveURLAsync(new Regex($".*/projects/{E2ESeed.ProjectId}/browse$"));
        await OpenSwitcherAsync();
        await ExpectOrderAsync(E2ESeed.ProjectName, Third);
    }

    [Test]
    public async Task Deleting_the_open_project_goes_to_the_next_one()
    {
        var (_, third) = await AddProjectsAsync();
        await GoToAsync($"/projects/{third}/browse");
        await OpenSwitcherAsync();
        await Row(Third).ClickAsync(new() { Button = MouseButton.Right });
        await Page.GetByTestId("ctx-delete").ClickAsync();
        await Page.GetByTestId("delete-project-name").FillAsync(Third);
        await Page.GetByTestId("delete-project-confirm").ClickAsync();

        await Expect(Page).ToHaveURLAsync(new Regex($".*/projects/{E2ESeed.ProjectId}/browse$"));
    }

    [Test]
    public async Task An_editor_gets_settings_but_the_owner_only_entries_are_disabled_with_a_reason()
    {
        await using (var db = await Db.CreateDbContextAsync())
        {
            var guest = await db.Users.SingleAsync(u => u.UserName == E2ESeed.SecondUserName);
            db.ProjectShares.Add(new ProjectShare { ProjectId = E2ESeed.ProjectId, UserId = guest.Id, Role = ProjectRole.Editor });
            await db.SaveChangesAsync();
        }
        await LogoutAsync();
        await LoginAsync(E2ESeed.SecondUserName);
        await GoToAsync($"/projects/{E2ESeed.ProjectId}/browse");
        await OpenSwitcherAsync();

        await Row(E2ESeed.ProjectName).ClickAsync(new() { Button = MouseButton.Right });
        await Expect(Page.GetByTestId("ctx-settings")).ToBeEnabledAsync();
        foreach (var key in new[] { "ctx-up", "ctx-down", "ctx-delete" })
        {
            await Expect(Page.GetByTestId(key)).ToBeDisabledAsync();
            await Expect(Page.GetByTestId(key)).ToHaveAttributeAsync("title", "Only the project's owner can do this.");
        }
        await Expect(Row(E2ESeed.ProjectName)).ToHaveAttributeAsync("draggable", "false");

        // The editor's settings dialog has name and description, but not the owner's parent / default settings.
        await Page.GetByTestId("ctx-settings").ClickAsync();
        var modal = Page.GetByTestId("modal-dialog");
        await Expect(modal.GetByLabel("Name")).ToBeVisibleAsync();
        await Expect(modal.Locator("#default-project-toggle")).ToHaveCountAsync(0);
        await Expect(modal.Locator("#project-settings-parent")).ToHaveCountAsync(0);
    }
}
