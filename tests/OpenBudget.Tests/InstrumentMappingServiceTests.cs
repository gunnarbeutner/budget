using OpenBudget.Models;
using Xunit;

namespace OpenBudget.Tests;

public class InstrumentMappingServiceTests
{
    [Fact]
    public async Task Lists_isin_less_instruments_and_holdings_and_round_trips_a_link()
    {
        await using var t = new TestDb();
        // ISIN-less gold instrument + lot, and an ISIN ETF that must NOT appear here.
        t.Db.PpSecurities.AddRange(
            new PpSecurity { OwnerUserId = TestDb.DefaultOwner, Uuid = "gold", Isin = "", Name = "Gold" },
            new PpSecurity { OwnerUserId = TestDb.DefaultOwner, Uuid = "etf", Isin = "IE00X", Name = "World ETF" });
        t.Db.PpLots.AddRange(
            new PpLot { OwnerUserId = TestDb.DefaultOwner, PortfolioUuid = "vault", SecurityUuid = "gold", Isin = "", Acquired = new DateOnly(2021, 1, 1), Shares = 2m, Cost = 60000m },
            new PpLot { OwnerUserId = TestDb.DefaultOwner, PortfolioUuid = "ing", SecurityUuid = "etf", Isin = "IE00X", Acquired = new DateOnly(2021, 1, 1), Shares = 5m, Cost = 500m });
        // imported holdings: a gold (no ISIN) and an ISIN ETF (must not be offered).
        t.AddAccount(10, "Depot");
        t.Db.Holdings.AddRange(
            new Holding { Id = 1, OwnerUserId = TestDb.DefaultOwner, AccountId = 10, Symbol = "XAU", Name = "Gold", Quantity = 2 },
            new Holding { Id = 2, OwnerUserId = TestDb.DefaultOwner, AccountId = 10, Isin = "IE00X", Name = "World ETF", Quantity = 5 });
        await t.Db.SaveChangesAsync();

        var m = await t.InstrumentMapping.GetAsync(TestDb.DefaultOwner);
        var inst = Assert.Single(m.Instruments);                 // only the ISIN-less gold
        Assert.Equal("gold", inst.PpUuid);
        Assert.Equal(1, inst.LotCount);
        Assert.Null(inst.LinkedHoldingKey);
        Assert.Contains(m.Holdings, h => h.Key == "sym:XAU");     // gold holding offered
        Assert.DoesNotContain(m.Holdings, h => h.Key == "isin:IE00X"); // ISIN ETF not offered

        await t.InstrumentMapping.SaveLinkAsync(TestDb.DefaultOwner, "gold", "sym:XAU");
        Assert.Equal("sym:XAU", (await t.InstrumentMapping.GetAsync(TestDb.DefaultOwner)).Instruments.Single().LinkedHoldingKey);

        await t.InstrumentMapping.SaveLinkAsync(TestDb.DefaultOwner, "gold", null);
        Assert.Null((await t.InstrumentMapping.GetAsync(TestDb.DefaultOwner)).Instruments.Single().LinkedHoldingKey);
    }
}
