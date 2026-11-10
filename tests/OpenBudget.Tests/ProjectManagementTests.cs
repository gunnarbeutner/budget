using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;
using OpenBudget.Services;
using OpenBudget.Tests.Connectors;
using Xunit;

namespace OpenBudget.Tests;

/// <summary>
/// The project switcher's context menu operations at the service level: drag-and-drop reorder (MoveProjectToAsync),
/// rename (UpdateProjectAsync), and who may reorder / rename / delete — reordering and deleting are the owner's,
/// renaming is open to editors, viewers can do none of it.
/// </summary>
public sealed class ProjectManagementTests
{
    private const string Alice = BankWorld.Alice;
    private const string Bob = BankWorld.Bob;

    private static async Task<TestDb> FourOrderedProjectsAsync()
    {
        var t = new TestDb();
        for (var i = 1; i <= 4; i++) t.AddProject(i, $"P{i}").SortOrder = i - 1;
        await t.Db.SaveChangesAsync();
        return t;
    }

    private static async Task<int[]> OrderedIdsAsync(TestDb t)
        => (await t.Projects.GetProjectsAsync()).Select(p => p.Id).ToArray();

    private static async Task<int> NewProjectAsync(BankWorld w, string user, string name)
        => (await w.View(user).Projects.CreateProjectAsync(name, user == Alice ? BankWorld.SourceAlice : 3)).Id;

    private static async Task<Project?> LoadAsync(BankWorld w, int projectId)
    {
        await using var db = await w.T.System.CreateDbContextAsync();
        return await db.Projects.AsNoTracking().SingleOrDefaultAsync(p => p.Id == projectId);
    }

    // ---- drag and drop ----

    [Fact]
    public async Task Dragging_down_lands_the_project_at_the_targets_place()
    {
        await using var t = await FourOrderedProjectsAsync();
        await t.Projects.MoveProjectToAsync(1, 3);
        Assert.Equal(new[] { 2, 3, 1, 4 }, await OrderedIdsAsync(t));
    }

    [Fact]
    public async Task Dragging_up_lands_the_project_at_the_targets_place()
    {
        await using var t = await FourOrderedProjectsAsync();
        await t.Projects.MoveProjectToAsync(4, 2);
        Assert.Equal(new[] { 1, 4, 2, 3 }, await OrderedIdsAsync(t));
    }

    [Fact]
    public async Task Dropping_on_itself_or_an_unknown_project_changes_nothing()
    {
        await using var t = await FourOrderedProjectsAsync();
        await t.Projects.MoveProjectToAsync(2, 2);
        await t.Projects.MoveProjectToAsync(2, 99);
        Assert.Equal(new[] { 1, 2, 3, 4 }, await OrderedIdsAsync(t));
    }

    // ---- rename ----

    [Fact]
    public async Task Update_renames_and_trims_and_clears_an_empty_description()
    {
        await using var t = await FourOrderedProjectsAsync();
        await t.Projects.UpdateProjectAsync(2, "  Example Market  ", "  Groceries  ");
        var p = await t.Projects.GetProjectAsync(2);
        Assert.Equal("Example Market", p!.Name);
        Assert.Equal("Groceries", p.Description);

        await t.Projects.UpdateProjectAsync(2, "Example Market", "   ");
        Assert.Null((await t.Projects.GetProjectAsync(2))!.Description);
    }

    [Fact]
    public async Task Update_refuses_an_empty_name()
    {
        await using var t = await FourOrderedProjectsAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => t.Projects.UpdateProjectAsync(2, " ", null));
    }

    // ---- access ----

    [Fact]
    public async Task The_owner_reorders_only_among_own_projects()
    {
        await using var w = await BankWorld.CreateAsync();
        var a1 = await NewProjectAsync(w, Alice, "Household");
        var a2 = await NewProjectAsync(w, Alice, "Holidays");
        var b1 = await NewProjectAsync(w, Bob, "Example Market");
        var alice = w.View(Alice);

        await alice.Projects.MoveProjectToAsync(a2, a1);
        Assert.True((await LoadAsync(w, a2))!.SortOrder < (await LoadAsync(w, a1))!.SortOrder);
        var bobOrder = (await LoadAsync(w, b1))!.SortOrder;

        await alice.Projects.MoveProjectAsync(a1, -1);
        Assert.Equal(bobOrder, (await LoadAsync(w, b1))!.SortOrder);
    }

    [Theory]
    [InlineData(ProjectRole.Editor)]
    [InlineData(ProjectRole.Viewer)]
    public async Task A_sharee_cannot_reorder_or_delete(ProjectRole role)
    {
        await using var w = await BankWorld.CreateAsync();
        var a1 = await NewProjectAsync(w, Alice, "Household");
        var a2 = await NewProjectAsync(w, Alice, "Holidays");
        await w.View(Alice).Sharing.SetShareAsync(a1, Bob, role);
        await w.View(Alice).Sharing.SetShareAsync(a2, Bob, role);
        var bob = w.View(Bob);

        await Assert.ThrowsAsync<AccessDeniedException>(() => bob.Projects.MoveProjectAsync(a2, -1));
        await Assert.ThrowsAsync<AccessDeniedException>(() => bob.Projects.MoveProjectToAsync(a2, a1));
        await Assert.ThrowsAsync<AccessDeniedException>(() => bob.Projects.DeleteProjectAsync(a1));
        Assert.NotNull(await LoadAsync(w, a1));
    }

    [Fact]
    public async Task The_owner_cannot_drop_an_own_project_onto_a_shared_one()
    {
        await using var w = await BankWorld.CreateAsync();
        var a1 = await NewProjectAsync(w, Alice, "Household");
        var b1 = await NewProjectAsync(w, Bob, "Example Market");
        await w.View(Bob).Sharing.SetShareAsync(b1, Alice, ProjectRole.Editor);

        await Assert.ThrowsAsync<AccessDeniedException>(() => w.View(Alice).Projects.MoveProjectToAsync(a1, b1));
    }

    [Fact]
    public async Task An_editor_may_rename_but_a_viewer_may_not()
    {
        await using var w = await BankWorld.CreateAsync();
        var a1 = await NewProjectAsync(w, Alice, "Household");
        await w.View(Alice).Sharing.SetShareAsync(a1, Bob, ProjectRole.Editor);
        await w.View(Bob).Projects.UpdateProjectAsync(a1, "Example Household", "Shared costs");
        Assert.Equal("Example Household", (await LoadAsync(w, a1))!.Name);

        await w.View(Alice).Sharing.SetShareAsync(a1, Bob, ProjectRole.Viewer);
        await Assert.ThrowsAsync<AccessDeniedException>(() => w.View(Bob).Projects.UpdateProjectAsync(a1, "Renamed", null));
        Assert.Equal("Example Household", (await LoadAsync(w, a1))!.Name);
    }

    [Fact]
    public async Task An_unrelated_user_cannot_touch_the_project()
    {
        await using var w = await BankWorld.CreateAsync();
        var a1 = await NewProjectAsync(w, Alice, "Household");
        var bob = w.View(Bob);

        await Assert.ThrowsAsync<AccessDeniedException>(() => bob.Projects.MoveProjectAsync(a1, 1));
        await Assert.ThrowsAsync<AccessDeniedException>(() => bob.Projects.DeleteProjectAsync(a1));
        Assert.NotNull(await LoadAsync(w, a1));
    }

    [Fact]
    public async Task The_owner_deletes_the_project_with_its_shares()
    {
        await using var w = await BankWorld.CreateAsync();
        var a1 = await NewProjectAsync(w, Alice, "Household");
        await w.View(Alice).Sharing.SetShareAsync(a1, Bob, ProjectRole.Viewer);

        await w.View(Alice).Projects.DeleteProjectAsync(a1);

        Assert.Null(await LoadAsync(w, a1));
        await using var db = await w.T.System.CreateDbContextAsync();
        Assert.False(await db.ProjectShares.AnyAsync(s => s.ProjectId == a1));
    }
}
