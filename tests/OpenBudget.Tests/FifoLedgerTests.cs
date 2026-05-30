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
    private static PTransaction Tx(PType type, string secUuid, string date, decimal shares, decimal euros)
    {
        var dt = DateTime.SpecifyKind(DateTime.Parse(date, CultureInfo.InvariantCulture), DateTimeKind.Utc);
        return new PTransaction
        {
            Type = type,
            Security = secUuid,
            CurrencyCode = "EUR",
            Date = Timestamp.FromDateTime(dt),
            Shares = (long)(shares * 100_000_000m),
            Amount = (long)(euros * 100m)
        };
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
            Tx(PType.Purchase, "s1", "2020-01-01", shares: 10, euros: 1000),
            Tx(PType.Purchase, "s1", "2021-01-01", shares: 10, euros: 1500),
            Tx(PType.Sale, "s1", "2022-01-01", shares: 5, euros: 900));

        var lots = FifoLedger.BuildOpenLots(client).OrderBy(l => l.Acquired).ToList();

        Assert.Equal(2, lots.Count);
        // Oldest lot loses 5 of its 10 shares — half its cost goes with them.
        Assert.Equal(new DateOnly(2020, 1, 1), lots[0].Acquired);
        Assert.Equal(5m, lots[0].Shares);
        Assert.Equal(500m, lots[0].Cost);
        // Newer lot is untouched.
        Assert.Equal(new DateOnly(2021, 1, 1), lots[1].Acquired);
        Assert.Equal(10m, lots[1].Shares);
        Assert.Equal(1500m, lots[1].Cost);
    }

    [Fact]
    public void Sale_spanning_lots_closes_the_first_and_dents_the_second()
    {
        var client = ClientWith(
            Tx(PType.Purchase, "s1", "2020-01-01", shares: 10, euros: 1000),
            Tx(PType.Purchase, "s1", "2021-01-01", shares: 10, euros: 1500),
            Tx(PType.Sale, "s1", "2022-01-01", shares: 12, euros: 2400));

        var lot = Assert.Single(FifoLedger.BuildOpenLots(client));
        Assert.Equal(new DateOnly(2021, 1, 1), lot.Acquired);
        Assert.Equal(8m, lot.Shares);            // 10 bought − 2 consumed
        Assert.Equal(1200m, lot.Cost);           // 1500 × 8/10
    }

    [Fact]
    public void Internal_security_transfer_is_ignored()
    {
        // A transfer between the user's own depots neither opens nor closes a lot.
        var transfer = Tx(PType.SecurityTransfer, "s1", "2021-06-01", shares: 10, euros: 0);
        transfer.Portfolio = "depot-a";
        transfer.OtherPortfolio = "depot-b";
        var client = ClientWith(
            Tx(PType.Purchase, "s1", "2020-01-01", shares: 10, euros: 1000),
            transfer);

        var lot = Assert.Single(FifoLedger.BuildOpenLots(client));
        Assert.Equal(new DateOnly(2020, 1, 1), lot.Acquired);   // original acquisition date survives
        Assert.Equal(10m, lot.Shares);
        Assert.Equal(1000m, lot.Cost);
    }

    [Fact]
    public void Inbound_delivery_opens_a_lot_and_a_full_sale_closes_everything()
    {
        var client = ClientWith(
            Tx(PType.InboundDelivery, "s1", "2020-01-01", shares: 4, euros: 400),
            Tx(PType.Sale, "s1", "2021-01-01", shares: 4, euros: 500));

        Assert.Empty(FifoLedger.BuildOpenLots(client));
    }
}
