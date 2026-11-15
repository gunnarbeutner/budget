using OpenBudget.Models;
using OpenBudget.Shared;
using Xunit;

namespace OpenBudget.Tests;

/// <summary>Which project views the navigation lists for a project's accounts.</summary>
public class ProjectViewRequirementsTests
{
    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(0, 1, false)]
    [InlineData(1, 0, true)]
    [InlineData(1, 2, true)]
    public void Portfolio_needs_an_open_depot(int openDepots, int closedDepots, bool available) =>
        Assert.Equal(available, ProjectViewRequirements.IsAvailable(ProjectTab.Portfolio, new ProjectAccountSummary(openDepots, closedDepots, 3, 1)));

    [Fact]
    public void Views_without_a_requirement_are_always_available()
    {
        foreach (var tab in Enum.GetValues<ProjectTab>().Where(t => t != ProjectTab.Portfolio))
            Assert.True(ProjectViewRequirements.IsAvailable(tab, ProjectAccountSummary.Empty), tab.ToString());
    }

    [Fact]
    public void The_active_view_is_listed_even_when_unavailable()
    {
        Assert.True(ProjectViewRequirements.Shows(ProjectTab.Portfolio, ProjectAccountSummary.Empty, ProjectTab.Portfolio));
        Assert.False(ProjectViewRequirements.Shows(ProjectTab.Portfolio, ProjectAccountSummary.Empty, ProjectTab.Browse));
    }

    [Fact]
    public void Views_with_a_requirement_wait_for_the_summary()
    {
        Assert.False(ProjectViewRequirements.Shows(ProjectTab.Portfolio, null, ProjectTab.Browse));
        Assert.True(ProjectViewRequirements.Shows(ProjectTab.Budgets, null, ProjectTab.Browse));
    }

    [Fact]
    public async Task The_summary_counts_the_assigned_accounts_by_kind_and_closed_state()
    {
        await using var t = await AccessWorld.BuildAsync();
        var openDepot = t.AddAccount(AccessWorld.Id(AccessWorld.SourceAlice, 10), "Depot", AccessWorld.Alice);
        openDepot.Type = (long)AccountType.Depot;
        var closedDepot = t.AddAccount(AccessWorld.Id(AccessWorld.SourceAlice, 11), "Old Depot", AccessWorld.Alice);
        closedDepot.Type = (long)AccountType.Depot;
        closedDepot.Closed = true;
        var unassignedDepot = t.AddAccount(AccessWorld.Id(AccessWorld.SourceAlice, 12), "Other Depot", AccessWorld.Alice);
        unassignedDepot.Type = (long)AccountType.Depot;
        t.Db.ProjectAccountAssignments.Add(new ProjectAccountAssignment { ProjectId = AccessWorld.Shared, AccountId = openDepot.Id });
        t.Db.ProjectAccountAssignments.Add(new ProjectAccountAssignment { ProjectId = AccessWorld.Shared, AccountId = closedDepot.Id });
        await t.Db.SaveChangesAsync();

        Assert.Equal(new ProjectAccountSummary(1, 1, 1, 0), await t.Projects.GetAccountSummaryAsync(AccessWorld.Shared));
        Assert.Equal(new ProjectAccountSummary(0, 0, 1, 0), await t.Projects.GetAccountSummaryAsync(AccessWorld.Private));
    }
}
