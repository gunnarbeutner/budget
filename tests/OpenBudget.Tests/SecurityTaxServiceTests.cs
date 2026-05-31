using OpenBudget.Models;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests;

public class SecurityTaxServiceTests
{
    [Fact]
    public async Task Lists_instruments_with_default_kinds_and_saves_an_override()
    {
        await using var t = new TestDb();
        t.Db.PpSecurities.AddRange(
            new PpSecurity { SourceId = 1, Uuid = "etf", Isin = "IE00EQ", Name = "Equity ETF" },
            new PpSecurity { SourceId = 1, Uuid = "gold", Isin = "", Name = "Gold" });
        t.Db.PpLots.AddRange(
            new PpLot { SourceId = 1, PortfolioUuid = "p", SecurityUuid = "etf", Isin = "IE00EQ", Acquired = new DateOnly(2020, 1, 1), Shares = 1m, Cost = 1m },
            new PpLot { SourceId = 1, PortfolioUuid = "p", SecurityUuid = "gold", Isin = "", Acquired = new DateOnly(2020, 1, 1), Shares = 1m, Cost = 1m });
        await t.Db.SaveChangesAsync();

        var settings = await t.SecurityTax.GetSettingsAsync(1);
        Assert.Equal(2, settings.Count);

        var etf = settings.Single(s => s.Uuid == "etf");
        Assert.Equal((int)InstrumentTaxKind.Security, etf.Kind);               // ISIN → Security
        Assert.Equal(SecurityTaxService.DefaultTeilfreistellungBp, etf.TeilfreistellungBp);
        Assert.True(etf.HasIsin);

        var gold = settings.Single(s => s.Uuid == "gold");
        Assert.Equal((int)InstrumentTaxKind.MovableSection23, gold.Kind);        // ISIN-less → gold §23
        Assert.False(gold.HasIsin);

        // Reclassify a position to real estate (§23, 10 yr) with an inherited acquisition date.
        await t.SecurityTax.SetAsync(1, "gold", (int)InstrumentTaxKind.RealEstateSection23, 0, new DateOnly(2008, 5, 1));
        var after = (await t.SecurityTax.GetSettingsAsync(1)).Single(s => s.Uuid == "gold");
        Assert.Equal((int)InstrumentTaxKind.RealEstateSection23, after.Kind);
        Assert.Equal(new DateOnly(2008, 5, 1), after.AcquiredOverride);
    }
}
