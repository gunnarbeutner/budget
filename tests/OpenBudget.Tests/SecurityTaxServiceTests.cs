using OpenBudget.Models;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests;

public class SecurityTaxServiceTests
{
    [Fact]
    public async Task Lists_lot_bearing_securities_defaulting_to_30_percent_and_saves_an_override()
    {
        await using var t = new TestDb();
        t.Db.PpSecurities.AddRange(
            new PpSecurity { SourceId = 1, Isin = "IE00EQ", Name = "Equity ETF" },
            new PpSecurity { SourceId = 1, Isin = "IE00BD", Name = "Bond ETF" });
        t.Db.PpLots.AddRange(
            new PpLot { SourceId = 1, PortfolioUuid = "p", Isin = "IE00EQ", Acquired = new DateOnly(2020, 1, 1), Shares = 1m, Cost = 1m },
            new PpLot { SourceId = 1, PortfolioUuid = "p", Isin = "IE00BD", Acquired = new DateOnly(2020, 1, 1), Shares = 1m, Cost = 1m });
        await t.Db.SaveChangesAsync();

        var settings = await t.SecurityTax.GetSettingsAsync(1);
        Assert.Equal(2, settings.Count);
        Assert.All(settings, s => Assert.Equal(SecurityTaxService.DefaultTeilfreistellungBp, s.TeilfreistellungBp)); // 30% default
        Assert.Contains(settings, s => s.Isin == "IE00EQ" && s.Name == "Equity ETF");
        Assert.Empty(await t.SecurityTax.GetRatesAsync(1));   // no overrides yet

        // Override the bond ETF to 0%.
        await t.SecurityTax.SetTeilfreistellungAsync(1, "IE00BD", 0);

        var rates = await t.SecurityTax.GetRatesAsync(1);
        Assert.Equal(0m, rates["IE00BD"]);
        Assert.False(rates.ContainsKey("IE00EQ"));            // unset securities stay on the default
        Assert.Equal(0, (await t.SecurityTax.GetSettingsAsync(1)).Single(s => s.Isin == "IE00BD").TeilfreistellungBp);
    }
}
