using System.Globalization;
using System.IO.Compression;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using OpenBudget.PortfolioPerformance.Proto;
using PType = OpenBudget.PortfolioPerformance.Proto.PTransaction.Types.Type;
using UType = OpenBudget.PortfolioPerformance.Proto.PTransactionUnit.Types.Type;

namespace OpenBudget.Tests;

/// <summary>
/// A synthetic PortfolioPerformance file (base EUR, "today" 2024-01-10):
/// <list type="bullet">
/// <item>securities Gold (EUR, closes 50/55/60 on Jan 1/3/5, latest 62 on Jan 9), Example ETF (USD, close 100 on
/// Jan 2, no latest) and Real estate (EUR, no prices);</item>
/// <item>portfolios "Vault" (pf-a), "Broker" (pf-b) and the retired "Old" (pf-r); cash account "Cash";</item>
/// <item>Vault buys 10 Gold on Jan 2 for 510 (fee 5, tax 5) and 5 on Jan 4 for 290, sells 3 on Jan 6 for 175 (fee 2,
/// tax 3), transfers 2 to Broker on Jan 7, receives 4 ETF (worth 360) on Jan 3 and 1 Real estate on Jan 2; a Gold
/// dividend of 8 is booked on the cash account on Jan 8, a deposit of 1000 on Jan 1, a fee of 1 without security.</item>
/// </list>
/// </summary>
internal static class PortfolioFixture
{
    public static readonly DateOnly Today = new(2024, 1, 10);

    public static long Day(int month, int day) =>
        new DateOnly(2024, month, day).DayNumber - DateOnly.FromDateTime(DateTime.UnixEpoch).DayNumber;

    public static PClient Client()
    {
        var c = new PClient { Version = 1, BaseCurrency = "EUR" };
        var gold = new PSecurity { Uuid = "sec-gold", Name = "Gold", CurrencyCode = "EUR", Latest = new PFullHistoricalPrice { Date = Day(1, 9), Close = 62_00000000 } };
        gold.Prices.Add(new PHistoricalPrice { Date = Day(1, 1), Close = 50_00000000 });
        gold.Prices.Add(new PHistoricalPrice { Date = Day(1, 3), Close = 55_00000000 });
        gold.Prices.Add(new PHistoricalPrice { Date = Day(1, 5), Close = 60_00000000 });
        var etf = new PSecurity { Uuid = "sec-etf", Name = "Example ETF", CurrencyCode = "USD", Isin = "IE00EXAMPLE1" };
        etf.Prices.Add(new PHistoricalPrice { Date = Day(1, 2), Close = 100_00000000 });
        var house = new PSecurity { Uuid = "sec-re", Name = "Real estate", CurrencyCode = "EUR" };
        c.Securities.Add(gold);
        c.Securities.Add(etf);
        c.Securities.Add(house);
        c.Accounts.Add(new PAccount { Uuid = "acc-1", Name = "Cash", CurrencyCode = "EUR" });
        c.Portfolios.Add(new PPortfolio { Uuid = "pf-a", Name = "Vault", ReferenceAccount = "acc-1" });
        c.Portfolios.Add(new PPortfolio { Uuid = "pf-b", Name = "Broker" });
        c.Portfolios.Add(new PPortfolio { Uuid = "pf-r", Name = "Old", IsRetired = true });

        c.Transactions.Add(Tx("t-dep", PType.Deposit, "2024-01-01", 1000m, account: "acc-1"));
        c.Transactions.Add(Tx("t-buy1", PType.Purchase, "2024-01-02T12:00", 510m, "sec-gold", 10m, "pf-a", account: "acc-1", fee: 5m, tax: 5m));
        c.Transactions.Add(Tx("t-re", PType.InboundDelivery, "2024-01-02", 0m, "sec-re", 1m, "pf-a"));
        c.Transactions.Add(Tx("t-in", PType.InboundDelivery, "2024-01-03", 360m, "sec-etf", 4m, "pf-a"));
        c.Transactions.Add(Tx("t-buy2", PType.Purchase, "2024-01-04", 290m, "sec-gold", 5m, "pf-a", note: "second lot"));
        c.Transactions.Add(Tx("t-sell", PType.Sale, "2024-01-06", 175m, "sec-gold", 3m, "pf-a", account: "acc-1", fee: 2m, tax: 3m));
        c.Transactions.Add(Tx("t-xfer", PType.SecurityTransfer, "2024-01-07", 0m, "sec-gold", 2m, "pf-a", toPortfolio: "pf-b"));
        c.Transactions.Add(Tx("t-div", PType.Dividend, "2024-01-08", 8m, "sec-gold", account: "acc-1"));
        c.Transactions.Add(Tx("t-fee", PType.Fee, "2024-01-09", 1m, account: "acc-1"));
        return c;
    }

    public static PTransaction Tx(
        string uuid, PType type, string date, decimal amount, string? security = null, decimal shares = 0, string? portfolio = null,
        string? account = null, string? toPortfolio = null, decimal fee = 0, decimal tax = 0, string? note = null)
    {
        var dt = DateTime.SpecifyKind(DateTime.Parse(date, CultureInfo.InvariantCulture), DateTimeKind.Utc);
        var t = new PTransaction
        {
            Uuid = uuid,
            Type = type,
            Date = Timestamp.FromDateTime(dt),
            CurrencyCode = "EUR",
            Amount = (long)(amount * 100m),
        };
        if (shares != 0) t.Shares = (long)(shares * 100_000_000m);
        if (security != null) t.Security = security;
        if (portfolio != null) t.Portfolio = portfolio;
        if (account != null) t.Account = account;
        if (toPortfolio != null) t.OtherPortfolio = toPortfolio;
        if (note != null) t.Note = note;
        if (fee != 0) t.Units.Add(new PTransactionUnit { Type = UType.Fee, Amount = (long)(fee * 100m), CurrencyCode = "EUR" });
        if (tax != 0) t.Units.Add(new PTransactionUnit { Type = UType.Tax, Amount = (long)(tax * 100m), CurrencyCode = "EUR" });
        return t;
    }

    /// <summary>The client packed like PortfolioPerformance saves it: a ZIP whose "data.portfolio" is "PPPBV1" + the message.</summary>
    public static byte[] Pack(PClient client)
    {
        var payload = client.ToByteArray();
        using var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var es = archive.CreateEntry("data.portfolio").Open();
            es.Write("PPPBV1"u8);
            es.Write(payload);
        }
        return zip.ToArray();
    }
}
