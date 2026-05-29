using Xunit;

namespace OpenBudget.Tests;

/// <summary>
/// MoveProjectAsync swaps a project with its immediate neighbor in the dashboard order. It
/// renumbers to normalize any tied/degenerate SortOrder values first, then swaps — EF only
/// writes the rows whose SortOrder actually changed. These pin the observable ordering.
/// </summary>
public class ProjectServiceTests
{
    private static async Task<TestDb> ThreeOrderedProjectsAsync()
    {
        var t = new TestDb();
        var a = t.AddProject(1, "A"); a.SortOrder = 0;
        var b = t.AddProject(2, "B"); b.SortOrder = 1;
        var c = t.AddProject(3, "C"); c.SortOrder = 2;
        await t.Db.SaveChangesAsync();
        return t;
    }

    private static async Task<int[]> OrderedIdsAsync(TestDb t)
        => (await t.Projects.GetProjectsAsync()).Select(p => p.Id).ToArray();

    [Fact]
    public async Task Move_up_swaps_with_the_previous_neighbor()
    {
        await using var t = await ThreeOrderedProjectsAsync();
        await t.Projects.MoveProjectAsync(2, -1); // B up
        Assert.Equal(new[] { 2, 1, 3 }, await OrderedIdsAsync(t));
    }

    [Fact]
    public async Task Move_down_swaps_with_the_next_neighbor()
    {
        await using var t = await ThreeOrderedProjectsAsync();
        await t.Projects.MoveProjectAsync(2, 1); // B down
        Assert.Equal(new[] { 1, 3, 2 }, await OrderedIdsAsync(t));
    }

    [Fact]
    public async Task Move_past_the_top_edge_is_a_noop()
    {
        await using var t = await ThreeOrderedProjectsAsync();
        await t.Projects.MoveProjectAsync(1, -1); // A is already first
        Assert.Equal(new[] { 1, 2, 3 }, await OrderedIdsAsync(t));
    }

    [Fact]
    public async Task Move_past_the_bottom_edge_is_a_noop()
    {
        await using var t = await ThreeOrderedProjectsAsync();
        await t.Projects.MoveProjectAsync(3, 1); // C is already last
        Assert.Equal(new[] { 1, 2, 3 }, await OrderedIdsAsync(t));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(-2)]
    public async Task Invalid_direction_is_rejected(int direction)
    {
        await using var t = await ThreeOrderedProjectsAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => t.Projects.MoveProjectAsync(1, direction));
    }
}
