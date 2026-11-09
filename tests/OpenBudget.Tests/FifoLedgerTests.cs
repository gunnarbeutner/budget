using System.Globalization;
using Google.Protobuf.WellKnownTypes;
using OpenBudget.PortfolioPerformance.Proto;
using OpenBudget.Services;
using Xunit;
using PType = OpenBudget.PortfolioPerformance.Proto.PTransaction.Types.Type;

namespace OpenBudget.Tests;

public class FifoLedgerTests
{
    // Builds a share-moving transaction with human-scale shares/euros (the ledger un-scales PP's
    // ×1e8 shares and cents internally).
    private static PTransaction Tx(PType type, string date, decimal shares, decimal euros, string portfolio = "p1", string? toPortfolio = null)
    {
        var dt = DateTime.SpecifyKind(DateTime.Parse(date, CultureInfo.InvariantCulture), DateTimeKind.Utc);
        var t = new PTransaction
        {
            Type = type,
            Security = "s1",
            CurrencyCode = "EUR",
            Portfolio = portfolio,
            Date = Timestamp.FromDateTime(dt),
            Shares = (long)(shares * 100_000_000m),
            Amount = (long)(euros * 100m)
        };
        if (toPortfolio is not null) t.OtherPortfolio = toPortfolio;
        return t;
    }

    private static PClient ClientWith(params PTransaction[] txns)
    {
        var c = new PClient();
        c.Securities.Add(new PSecurity { Uuid = "s1", Isin = "DE000A", Name = "A" });
        c.Transactions.AddRange(txns);
        return c;
    }

    [Fact]
    public void Partial_sale_consumes_the_oldest_lot_first_and_splits_its_cost()
    {
        var client = ClientWith(
            Tx(PType.Purchase, "2020-01-01", shares: 10, euros: 1000),
            Tx(PType.Purchase, "2021-01-01", shares: 10, euros: 1500),
            Tx(PType.Sale, "2022-01-01", shares: 5, euros: 900));

        var lots = FifoLedger.BuildOpenLots(client).OrderBy(l => l.Acquired).ToList();

        Assert.Equal(2, lots.Count);
        Assert.Equal(new DateOnly(2020, 1, 1), lots[0].Acquired);
        Assert.Equal(5m, lots[0].Shares);
        Assert.Equal(500m, lots[0].Cost);     // half the oldest lot's cost left with the 5 shares sold
        Assert.Equal(new DateOnly(2021, 1, 1), lots[1].Acquired);
        Assert.Equal(10m, lots[1].Shares);
        Assert.Equal(1500m, lots[1].Cost);
    }

    [Fact]
    public void Sale_spanning_lots_closes_the_first_and_dents_the_second()
    {
        var client = ClientWith(
            Tx(PType.Purchase, "2020-01-01", shares: 10, euros: 1000),
            Tx(PType.Purchase, "2021-01-01", shares: 10, euros: 1500),
            Tx(PType.Sale, "2022-01-01", shares: 12, euros: 2400));

        var lot = Assert.Single(FifoLedger.BuildOpenLots(client));
        Assert.Equal(new DateOnly(2021, 1, 1), lot.Acquired);
        Assert.Equal(8m, lot.Shares);
        Assert.Equal(1200m, lot.Cost);
    }

    [Fact]
    public void Transfer_relocates_the_lot_to_the_target_depot_keeping_its_acquisition_date()
    {
        var client = ClientWith(
            Tx(PType.Purchase, "2020-01-01", shares: 10, euros: 1000, portfolio: "depot-a"),
            Tx(PType.SecurityTransfer, "2021-06-01", shares: 10, euros: 0, portfolio: "depot-a", toPortfolio: "depot-b"));

        var lot = Assert.Single(FifoLedger.BuildOpenLots(client));
        Assert.Equal("depot-b", lot.PortfolioUuid);             // moved depots…
        Assert.Equal(new DateOnly(2020, 1, 1), lot.Acquired);   // …but the Spekulationsfrist clock is intact
        Assert.Equal(10m, lot.Shares);
        Assert.Equal(1000m, lot.Cost);
    }

    [Fact]
    public void Transferred_in_old_lot_sells_before_a_newer_local_lot()
    {
        var client = ClientWith(
            Tx(PType.Purchase, "2021-01-01", shares: 5, euros: 750, portfolio: "depot-b"),   // newer, local
            Tx(PType.Purchase, "2018-01-01", shares: 10, euros: 1000, portfolio: "depot-a"), // older
            Tx(PType.SecurityTransfer, "2022-01-01", shares: 10, euros: 0, portfolio: "depot-a", toPortfolio: "depot-b"),
            Tx(PType.Sale, "2023-01-01", shares: 5, euros: 900, portfolio: "depot-b"));

        var lots = FifoLedger.BuildOpenLots(client).OrderBy(l => l.Acquired).ToList();

        Assert.Equal(2, lots.Count);
        Assert.All(lots, l => Assert.Equal("depot-b", l.PortfolioUuid));
        Assert.Equal(new DateOnly(2018, 1, 1), lots[0].Acquired);  // the sale ate the transferred-in 2018 lot first
        Assert.Equal(5m, lots[0].Shares);
        Assert.Equal(500m, lots[0].Cost);
        Assert.Equal(new DateOnly(2021, 1, 1), lots[1].Acquired);  // local lot untouched
        Assert.Equal(5m, lots[1].Shares);
        Assert.Equal(750m, lots[1].Cost);
    }

    [Fact]
    public void Reconstructs_lots_for_an_isin_less_instrument_keyed_by_uuid()
    {
        var c = new PClient();
        c.Securities.Add(new PSecurity { Uuid = "gold", Name = "Gold" }); // no ISIN
        var dt = DateTime.SpecifyKind(DateTime.Parse("2021-01-01", CultureInfo.InvariantCulture), DateTimeKind.Utc);
        c.Transactions.Add(new PTransaction
        {
            Type = PType.Purchase,
            Security = "gold",
            CurrencyCode = "EUR",
            Portfolio = "vault",
            Date = Timestamp.FromDateTime(dt),
            Shares = (long)(2m * 100_000_000m),
            Amount = (long)(60000m * 100m)
        });

        var lot = Assert.Single(FifoLedger.BuildOpenLots(c));
        Assert.Equal("gold", lot.SecurityUuid);
        Assert.Equal("", lot.Isin);                 // ISIN-less, joined to the source app by mapping later
        Assert.Equal(2m, lot.Shares);
        Assert.Equal(60000m, lot.Cost);
    }

    [Fact]
    public void Inbound_delivery_opens_a_lot_and_a_full_sale_closes_everything()
    {
        var client = ClientWith(
            Tx(PType.InboundDelivery, "2020-01-01", shares: 4, euros: 400),
            Tx(PType.Sale, "2021-01-01", shares: 4, euros: 500));

        Assert.Empty(FifoLedger.BuildOpenLots(client));
    }
}
