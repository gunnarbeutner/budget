using OpenBudget.Models;
using Xunit;

namespace OpenBudget.Tests;

public class DepotMappingServiceTests
{
    [Fact]
    public async Task Lists_depots_with_lot_counts_and_round_trips_a_link()
    {
        await using var t = new TestDb();
        var depot = t.AddAccount(100, "Example Depot");
        depot.Portfolio = 1;                       // a imported depot account
        t.AddAccount(200, "Checking").Type = 1;    // a non-depot account — must not be offered
        t.Db.PpPortfolios.Add(new PpPortfolio { OwnerUserId = TestDb.DefaultOwner, Uuid = "pp-1", Name = "Example-Depot" });
        t.Db.PpLots.AddRange(
            new PpLot { OwnerUserId = TestDb.DefaultOwner, PortfolioUuid = "pp-1", Isin = "IE00A", Acquired = new DateOnly(2020, 1, 1), Shares = 10m, Cost = 1000m },
            new PpLot { OwnerUserId = TestDb.DefaultOwner, PortfolioUuid = "pp-1", Isin = "IE00B", Acquired = new DateOnly(2021, 1, 1), Shares = 5m, Cost = 500m });
        await t.Db.SaveChangesAsync();

        var mapping = await t.DepotMapping.GetAsync(TestDb.DefaultOwner);
        var row = Assert.Single(mapping.Depots);
        Assert.Equal("Example-Depot", row.PpName);
        Assert.Equal(2, row.LotCount);
        Assert.Equal(2, row.IsinCount);
        Assert.Null(row.LinkedAccountId);
        Assert.Contains(mapping.Accounts, a => a.Id == 100);          // depot offered
        Assert.DoesNotContain(mapping.Accounts, a => a.Id == 200);    // checking account not offered

        // Link it, read it back.
        await t.DepotMapping.SaveLinkAsync(TestDb.DefaultOwner, "pp-1", 100);
        Assert.Equal(100, (await t.DepotMapping.GetAsync(TestDb.DefaultOwner)).Depots.Single().LinkedAccountId);

        // Re-link to a different account updates in place (no duplicate).
        await t.DepotMapping.SaveLinkAsync(TestDb.DefaultOwner, "pp-1", 100);
        // Clear it.
        await t.DepotMapping.SaveLinkAsync(TestDb.DefaultOwner, "pp-1", null);
        Assert.Null((await t.DepotMapping.GetAsync(TestDb.DefaultOwner)).Depots.Single().LinkedAccountId);
    }
}
