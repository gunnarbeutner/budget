using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;
using OpenBudget.Services;
using OpenBudget.Tests.Connectors;
using Xunit;

namespace OpenBudget.Tests;

/// <summary>
/// The project switcher's context menu operations at the service level: drag-and-drop reorder (MoveProjectToAsync),
/// rename (UpdateProjectAsync), and who may reorder / rename / delete — every user reorders their own list of
/// the projects they see, deleting is the owner's, renaming is open to editors.
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
        => (await w.View(user).Projects.CreateProjectAsync(name)).Id;

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

    private static async Task<int[]> ListOfAsync(BankWorld w, string user, params int[] ids)
        => (await w.View(user).Projects.GetProjectsAsync()).Select(p => p.Id).Where(ids.Contains).ToArray();

    // Alice owns a1 and a2, Bob owns b1; both of Alice's projects are shared with Bob.
    private static async Task<(BankWorld W, int A1, int A2, int B1)> SharedWorldAsync(ProjectRole role = ProjectRole.Editor)
    {
        var w = await BankWorld.CreateAsync();
        var a1 = await NewProjectAsync(w, Alice, "Household");
        var a2 = await NewProjectAsync(w, Alice, "Holidays");
        var b1 = await NewProjectAsync(w, Bob, "Example Market");
        await w.View(Alice).Sharing.SetShareAsync(a1, Bob, role);
        await w.View(Alice).Sharing.SetShareAsync(a2, Bob, role);
        // Bob's list ends with a1, a2, b1 (dragging onto the last row lands a project at the end).
        var bob = w.View(Bob).Projects;
        foreach (var id in new[] { a1, a2, b1 })
        {
            var last = (await bob.GetProjectsAsync())[^1].Id;
            if (last != id) await bob.MoveProjectToAsync(id, last);
        }
        return (w, a1, a2, b1);
    }

    [Fact]
    public async Task Each_user_orders_a_shared_project_independently()
    {
        var (w, a1, a2, b1) = await SharedWorldAsync();
        await using var _ = w;
        Assert.Equal(new[] { a1, a2 }, await ListOfAsync(w, Alice, a1, a2));
        Assert.Equal(new[] { a1, a2, b1 }, await ListOfAsync(w, Bob, a1, a2, b1));

        await w.View(Bob).Projects.MoveProjectToAsync(b1, a1);
        await w.View(Alice).Projects.MoveProjectToAsync(a2, a1);

        Assert.Equal(new[] { b1, a1, a2 }, await ListOfAsync(w, Bob, a1, a2, b1));
        Assert.Equal(new[] { a2, a1 }, await ListOfAsync(w, Alice, a1, a2));
        // The projects themselves keep their creation order.
        Assert.True((await LoadAsync(w, a1))!.SortOrder < (await LoadAsync(w, a2))!.SortOrder);
    }

    [Theory]
    [InlineData(ProjectRole.Editor)]
    [InlineData(ProjectRole.Viewer)]
    public async Task A_sharee_reorders_her_own_list_but_cannot_delete(ProjectRole role)
    {
        var (w, a1, a2, b1) = await SharedWorldAsync(role);
        await using var _ = w;
        var bob = w.View(Bob);

        await bob.Projects.MoveProjectAsync(a2, -1);
        Assert.Equal(new[] { a2, a1, b1 }, await ListOfAsync(w, Bob, a1, a2, b1));
        await bob.Projects.MoveProjectToAsync(a1, b1);
        Assert.Equal(new[] { a2, b1, a1 }, await ListOfAsync(w, Bob, a1, a2, b1));
        Assert.Equal(new[] { a1, a2 }, await ListOfAsync(w, Alice, a1, a2));

        await Assert.ThrowsAsync<AccessDeniedException>(() => bob.Projects.DeleteProjectAsync(a1));
        Assert.NotNull(await LoadAsync(w, a1));
    }

    [Fact]
    public async Task The_owner_reordering_leaves_the_sharees_list_alone()
    {
        var (w, a1, a2, b1) = await SharedWorldAsync();
        await using var _ = w;
        await w.View(Bob).Projects.MoveProjectAsync(a1, 1);
        Assert.Equal(new[] { a2, a1, b1 }, await ListOfAsync(w, Bob, a1, a2, b1));

        await w.View(Alice).Projects.MoveProjectAsync(a2, 1);
        await w.View(Alice).Projects.MoveProjectToAsync(a2, a1);
        Assert.Equal(new[] { a2, a1, b1 }, await ListOfAsync(w, Bob, a1, a2, b1));
    }

    [Fact]
    public async Task A_new_project_lands_after_the_ordered_ones()
    {
        var (w, a1, a2, _) = await SharedWorldAsync();
        await using var __ = w;
        await w.View(Alice).Projects.MoveProjectAsync(a2, -1);
        var a3 = await NewProjectAsync(w, Alice, "Example Savings");
        Assert.Equal(new[] { a2, a1, a3 }, await ListOfAsync(w, Alice, a1, a2, a3));
    }

    [Fact]
    public async Task Losing_access_or_deleting_drops_the_order_rows()
    {
        var (w, a1, a2, b1) = await SharedWorldAsync();
        await using var _ = w;
        await w.View(Bob).Projects.MoveProjectAsync(a2, -1);
        await w.View(Alice).Sharing.RevokeShareAsync(a2, Bob);
        Assert.Equal(new[] { a1, b1 }, await ListOfAsync(w, Bob, a1, a2, b1));
        await w.View(Alice).Projects.DeleteProjectAsync(a1);

        await using var db = await w.T.System.CreateDbContextAsync();
        Assert.False(await db.UserProjectOrders.AnyAsync(o => o.ProjectId == a1 || o.ProjectId == a2));
    }

    [Fact]
    public async Task An_unrelated_user_cannot_move_a_project_into_her_list()
    {
        var (w, a1, _, b1) = await SharedWorldAsync();
        await using var __ = w;
        await w.View(Alice).Sharing.RevokeShareAsync(a1, Bob);
        await Assert.ThrowsAsync<AccessDeniedException>(() => w.View(Bob).Projects.MoveProjectToAsync(b1, a1));
        await Assert.ThrowsAsync<AccessDeniedException>(() => w.View(Bob).Projects.MoveProjectToAsync(a1, b1));
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
