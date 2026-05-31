using OpenBudget.Models;
using Xunit;

namespace OpenBudget.Tests;

public class ProjectTaxProfilesTests
{
    [Fact]
    public async Task Assembles_profiles_for_isin_and_mapped_isin_less_positions_with_resolved_regimes()
    {
        await using var t = new TestDb();
        t.AddProject(7);
        var depot = t.AddAccount(100, "Depot"); depot.Portfolio = 1;
        t.Db.ProjectAccountAssignments.Add(new ProjectAccountAssignment { ProjectId = 7, AccountId = 100 });
        // Depot link: PP depot "pp-d" → MoneyMoney depot 100.
        t.Db.PpPortfolioLinks.Add(new PpPortfolioLink { SourceId = 1, PpPortfolioUuid = "pp-d", AccountId = 100 });
        // Instrument link: ISIN-less gold uuid → the MoneyMoney holding key.
        t.Db.PpSecurityLinks.Add(new PpSecurityLink { SourceId = 1, PpSecurityUuid = "gold", MmHoldingKey = "sym:XAU" });
        // Reclassify the gold explicitly (default would also be gold §23).
        t.Db.SecurityTaxSettings.Add(new SecurityTaxSetting { SourceId = 1, SecurityUuid = "gold", Kind = (int)InstrumentTaxKind.MovableSection23, TeilfreistellungBp = 0 });
        t.Db.PpLots.AddRange(
            new PpLot { SourceId = 1, PortfolioUuid = "pp-d", SecurityUuid = "etf", Isin = "IE00X", Acquired = new DateOnly(2020, 1, 1), Shares = 10m, Cost = 1000m },
            new PpLot { SourceId = 1, PortfolioUuid = "pp-d", SecurityUuid = "gold", Isin = "", Acquired = new DateOnly(2023, 1, 1), Shares = 1m, Cost = 20000m },
            // An ISIN-less lot with no instrument mapping must be dropped.
            new PpLot { SourceId = 1, PortfolioUuid = "pp-d", SecurityUuid = "ghost", Isin = "", Acquired = new DateOnly(2023, 1, 1), Shares = 1m, Cost = 1m });
        await t.Db.SaveChangesAsync();

        var profiles = await t.Portfolio.GetProjectTaxProfilesAsync(7);

        Assert.Equal(2, profiles.Count);   // etf + mapped gold; ghost dropped
        var etf = profiles["isin:IE00X"];
        Assert.Equal(TaxAssetKind.Security, etf.AssetKind);
        Assert.Equal(0.30m, etf.TeilfreistellungRate);          // ISIN default 30%
        Assert.Single(etf.Lots);

        var gold = profiles["sym:XAU"];                          // keyed by the mapped holding key
        Assert.Equal(TaxAssetKind.PrivateSale, gold.AssetKind);
        Assert.Equal(1, gold.SpekulationsfristYears);            // gold §23 → 1 year
        Assert.False(profiles.ContainsKey("name:ghost"));
    }
}
