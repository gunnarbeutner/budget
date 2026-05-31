using OpenBudget.Models;
using Xunit;

namespace OpenBudget.Tests;

public class ProjectLotsTests
{
    [Fact]
    public async Task GetProjectLots_returns_only_lots_from_linked_project_depots()
    {
        await using var t = new TestDb();
        t.AddProject(5);
        var linked = t.AddAccount(100, "Linked depot"); linked.Portfolio = 1;
        var unlinked = t.AddAccount(200, "Unlinked depot"); unlinked.Portfolio = 1;
        t.Db.ProjectAccountAssignments.Add(new ProjectAccountAssignment { ProjectId = 5, AccountId = 100 });
        t.Db.ProjectAccountAssignments.Add(new ProjectAccountAssignment { ProjectId = 5, AccountId = 200 });
        // Only depot 100 is mapped to a PP depot; pp-B is deliberately unlinked.
        t.Db.PpPortfolioLinks.Add(new PpPortfolioLink { SourceId = 1, PpPortfolioUuid = "pp-A", AccountId = 100 });
        t.Db.PpLots.AddRange(
            new PpLot { SourceId = 1, PortfolioUuid = "pp-A", Isin = "X", Acquired = new DateOnly(2020, 1, 1), Shares = 10m, Cost = 1000m },
            new PpLot { SourceId = 1, PortfolioUuid = "pp-A", Isin = "X", Acquired = new DateOnly(2021, 1, 1), Shares = 5m, Cost = 600m },
            new PpLot { SourceId = 1, PortfolioUuid = "pp-A", Isin = "Y", Acquired = new DateOnly(2019, 1, 1), Shares = 3m, Cost = 300m },
            new PpLot { SourceId = 1, PortfolioUuid = "pp-B", Isin = "X", Acquired = new DateOnly(2018, 1, 1), Shares = 99m, Cost = 9m });
        await t.Db.SaveChangesAsync();

        var lots = await t.Portfolio.GetProjectLotsAsync(5);

        Assert.Equal(2, lots.Count);                              // X and Y only
        Assert.Equal(2, lots["X"].Count);                         // pp-A's two X lots
        Assert.DoesNotContain(lots["X"], l => l.Shares == 99m);   // pp-B's lot excluded (unlinked)
        Assert.Single(lots["Y"]);
    }

    [Fact]
    public async Task GetProjectLots_is_empty_when_no_depot_is_linked()
    {
        await using var t = new TestDb();
        t.AddProject(6);
        var depot = t.AddAccount(300, "Depot"); depot.Portfolio = 1;
        t.Db.ProjectAccountAssignments.Add(new ProjectAccountAssignment { ProjectId = 6, AccountId = 300 });
        t.Db.PpLots.Add(new PpLot { SourceId = 1, PortfolioUuid = "pp-Z", Isin = "X", Acquired = new DateOnly(2020, 1, 1), Shares = 1m, Cost = 1m });
        await t.Db.SaveChangesAsync();

        Assert.Empty(await t.Portfolio.GetProjectLotsAsync(6));
    }
}
