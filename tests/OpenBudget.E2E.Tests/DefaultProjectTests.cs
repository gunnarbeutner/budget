using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using NUnit.Framework;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.E2E.Tests;

/// <summary>
/// "Default project for new accounts": the switch in the switcher's Settings… dialog (with the switcher badge),
/// and its refusal and automatic clearing (with a notice) on shared projects.
/// </summary>
[TestFixture]
public sealed class DefaultProjectTests : EndToEndTestBase
{
    private string SettingsUrl => $"/projects/{E2ESeed.ProjectId}/settings";
    private ILocator Toggle => Page.Locator("#default-project-toggle");
    private ILocator Badge => Page.GetByTestId("switcher-default-badge");

    private async Task OpenSwitcherAsync()
    {
        await Page.Locator("[data-testid=project-selector] > summary").ClickAsync();
        await Expect(Page.GetByTestId("switcher-project").First).ToBeVisibleAsync();
    }

    /// <summary>Opens the project's Settings… dialog from the switcher's context menu.</summary>
    private async Task OpenSettingsDialogAsync()
    {
        await GoToAsync($"/projects/{E2ESeed.ProjectId}/browse");
        await OpenSwitcherAsync();
        await Page.GetByTestId("switcher-project").Filter(new() { HasText = E2ESeed.ProjectName }).ClickAsync(new() { Button = MouseButton.Right });
        await Page.GetByTestId("ctx-settings").ClickAsync();
        await Expect(Page.GetByTestId("modal-dialog")).ToBeVisibleAsync();
    }

    private async Task ShareDirectlyAsync()
    {
        var factory = Factory.Services.GetRequiredService<SystemDbContextFactory>();
        await using var db = await factory.CreateDbContextAsync();
        var guest = await db.Users.SingleAsync(u => u.UserName == E2ESeed.SecondUserName);
        db.ProjectShares.Add(new ProjectShare { ProjectId = E2ESeed.ProjectId, UserId = guest.Id, Role = ProjectRole.Viewer });
        await db.SaveChangesAsync();
    }

    [Test]
    public async Task The_settings_dialog_switch_marks_the_project_as_default_and_the_switcher_shows_a_badge()
    {
        await OpenSettingsDialogAsync();
        await Expect(Toggle).Not.ToBeCheckedAsync();
        await Toggle.CheckAsync();
        await Page.GetByTestId("project-settings-save").ClickAsync();
        await Expect(Page.GetByTestId("modal-dialog")).ToHaveCountAsync(0);

        await OpenSwitcherAsync();
        await Expect(Badge).ToBeVisibleAsync();
        // The settings page has no switch.
        await GoToAsync(SettingsUrl);
        await Expect(Page.GetByTestId("accounts-card")).ToBeVisibleAsync();
        await Expect(Toggle).ToHaveCountAsync(0);

        await OpenSettingsDialogAsync();
        await Expect(Toggle).ToBeCheckedAsync();
        await Toggle.UncheckAsync();
        await Page.GetByTestId("project-settings-save").ClickAsync();
        await Expect(Page.GetByTestId("modal-dialog")).ToHaveCountAsync(0);
        await OpenSwitcherAsync();
        await Expect(Badge).ToHaveCountAsync(0);
    }

    [Test]
    public async Task A_shared_project_cannot_be_the_default_and_the_switch_explains_why()
    {
        await ShareDirectlyAsync();
        await OpenSettingsDialogAsync();

        await Expect(Toggle).ToBeDisabledAsync();
        await Expect(Page.Locator("#default-project-help")).ToContainTextAsync("shared");
    }

    [Test]
    public async Task Sharing_the_default_project_clears_it_and_shows_a_notice()
    {
        await OpenSettingsDialogAsync();
        await Toggle.CheckAsync();
        await Page.GetByTestId("project-settings-save").ClickAsync();
        await Expect(Page.GetByTestId("modal-dialog")).ToHaveCountAsync(0);

        await GoToAsync(SettingsUrl);
        await Page.Locator("#share-user").SelectOptionAsync(new SelectOptionValue { Label = E2ESeed.SecondUserName });
        await Page.Locator("#share-add").ClickAsync();
        await Expect(Page.Locator("#default-cleared-notice")).ToBeVisibleAsync();

        // The dialog shows it too, with the switch off and disabled; dismissing it there clears it everywhere.
        await OpenSettingsDialogAsync();
        await Expect(Toggle).Not.ToBeCheckedAsync();
        await Expect(Toggle).ToBeDisabledAsync();
        var notice = Page.Locator("#settings-default-cleared-notice");
        await Expect(notice).ToBeVisibleAsync();
        await notice.GetByRole(AriaRole.Button, new() { Name = "Dismiss" }).ClickAsync();
        await Expect(notice).ToHaveCountAsync(0);

        await GoToAsync(SettingsUrl);
        await Expect(Page.GetByTestId("accounts-card")).ToBeVisibleAsync();
        await Expect(Page.Locator("#default-cleared-notice")).ToHaveCountAsync(0);
    }
}
