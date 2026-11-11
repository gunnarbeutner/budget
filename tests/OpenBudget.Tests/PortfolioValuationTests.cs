using OpenBudget.Services;
using Xunit;
using static OpenBudget.Tests.PortfolioFixture;
using PType = OpenBudget.PortfolioPerformance.Proto.PTransaction.Types.Type;

namespace OpenBudget.Tests;

/// <summary>
/// The valuation of the synthetic <see cref="PortfolioFixture"/>; the expected numbers are what the pp-bridge helper and
/// its chart back-fill compute for the same data (worked out by hand below, and cross-checked against both).
/// </summary>
public class PortfolioValuationTests
{
    private static PpValuation Valuation() => PortfolioValuation.Compute(Client(), Today);

    private static PpPortfolioValuation Vault() => Valuation().Portfolios.Single(p => p.Uuid == "pf-a");

    [Fact]
    public void Positions_are_shares_times_latest_price_with_a_moving_average_purchase_price()
    {
        var positions = Vault().Positions;

        // Sorted by value, descending.
        Assert.Equal(["Gold", "Example ETF", "Real estate"], positions.Select(p => p.Security.Name));

        // Gold: +10 +5 −3 sold −2 transferred = 10 shares at the latest quote (62, not the last historical close 60).
        var gold = positions[0];
        Assert.Equal(10, gold.Shares, 9);
        Assert.Equal(62, gold.Price, 9);
        Assert.Equal(620, gold.Amount, 9);
        // Basis 510 − 5 tax + 290 = 795 for 15 (avg 53); the sale and the transfer each leave at 53 → 530 for 10.
        Assert.Equal(53, gold.PurchasePrice!.Value, 9);
        Assert.Equal("EUR", gold.PurchaseCurrency);

        // ETF: no latest quote → last historical close; priced in USD, so no (EUR) purchase price.
        var etf = positions[1];
        Assert.Equal(4, etf.Shares, 9);
        Assert.Equal(100, etf.Price, 9);
        Assert.Equal("USD", etf.Security.Currency);
        Assert.Null(etf.PurchasePrice);

        // Real estate: delivered without value and never priced.
        var house = positions[2];
        Assert.Equal(1, house.Shares, 9);
        Assert.Null(house.Security.Price);
        Assert.Equal(0, house.Amount, 9);
        Assert.Null(house.PurchasePrice);
    }

    [Fact]
    public void A_transfer_moves_shares_and_their_basis_to_the_receiving_portfolio()
    {
        var broker = Valuation().Portfolios.Single(p => p.Uuid == "pf-b");
        var gold = Assert.Single(broker.Positions);
        Assert.Equal(2, gold.Shares, 9);
        Assert.Equal(124, gold.Amount, 9);
        Assert.Equal(53, gold.PurchasePrice!.Value, 9);
        Assert.Empty(broker.Ledger);   // the transfer is booked on the sending portfolio only
    }

    [Fact]
    public void Ledger_itemises_fees_and_taxes_so_the_lines_net_to_the_cash_amount()
    {
        var ledger = Vault().Ledger;
        Assert.Equal(11, ledger.Count);
        Assert.Equal(ledger.OrderByDescending(e => e.Timestamp).Select(e => e.Key), ledger.Select(e => e.Key));

        var buy = ledger.Where(e => e.Key.StartsWith("t-buy1", StringComparison.Ordinal)).ToDictionary(e => e.Key, StringComparer.Ordinal);
        Assert.Equal(("Kauf", -500.0, "Kauf: Gold (10)"), (buy["t-buy1"].Type, buy["t-buy1"].Amount, buy["t-buy1"].Purpose));
        Assert.Equal(("Gebühr", -5.0, "Gebühr: Gold"), (buy["t-buy1:fee"].Type, buy["t-buy1:fee"].Amount, buy["t-buy1:fee"].Purpose));
        Assert.Equal(("Steuer", -5.0, "Steuer: Gold"), (buy["t-buy1:tax"].Type, buy["t-buy1:tax"].Amount, buy["t-buy1:tax"].Purpose));

        var sell = ledger.Where(e => e.Key.StartsWith("t-sell", StringComparison.Ordinal)).ToList();
        Assert.Equal([180.0, -2.0, -3.0], sell.Select(e => e.Amount));
        Assert.Equal(175.0, sell.Sum(e => e.Amount), 9);

        Assert.Equal("Kauf: Gold (5) — second lot", ledger.Single(e => e.Key == "t-buy2").Purpose);
        Assert.Equal(("Übertrag", 0.0), (ledger.Single(e => e.Key == "t-xfer").Type, ledger.Single(e => e.Key == "t-xfer").Amount));
        Assert.Equal(("Einlieferung", -360.0), (ledger.Single(e => e.Key == "t-in").Type, ledger.Single(e => e.Key == "t-in").Amount));
        // The dividend carries no portfolio: it lands on the portfolio that last moved the security.
        Assert.Equal(("Dividende", 8.0, "Dividende: Gold"), (ledger.Single(e => e.Key == "t-div").Type, ledger.Single(e => e.Key == "t-div").Amount, ledger.Single(e => e.Key == "t-div").Purpose));
        // Deposits and a fee without security belong to no portfolio.
        Assert.DoesNotContain(ledger, e => e.Key is "t-dep" or "t-fee");

        Assert.Equal(-977.0, ledger.Sum(e => e.Amount), 9);   // −510 −290 +175 +8 −360
    }

    [Fact]
    public void History_is_shares_held_each_day_times_the_close_on_or_before_it()
    {
        var history = Vault().History.ToDictionary(h => h.SecurityName, StringComparer.Ordinal);
        Assert.False(history.ContainsKey("Real estate"));   // no prices, no value line

        var gold = history["Gold"].Values;
        Assert.Equal(Day(1, 2), gold.Keys.First());
        Assert.Equal(Day(1, 10), gold.Keys.Last());           // runs up to today
        Assert.Equal(500, gold[Day(1, 2)], 9);   // 10 × 50
        Assert.Equal(550, gold[Day(1, 3)], 9);   // 10 × 55
        Assert.Equal(825, gold[Day(1, 4)], 9);   // 15 × 55
        Assert.Equal(900, gold[Day(1, 5)], 9);   // 15 × 60
        Assert.Equal(720, gold[Day(1, 6)], 9);   // 12 × 60
        Assert.Equal(600, gold[Day(1, 10)], 9);  // 10 × 60 (the latest quote is not part of the history)

        var etf = history["Example ETF"].Values;
        Assert.Equal(8, etf.Count);
        Assert.All(etf.Values, v => Assert.Equal(400, v, 9));

        var broker = Valuation().Portfolios.Single(p => p.Uuid == "pf-b").History.Single();
        Assert.Equal([Day(1, 7), Day(1, 8), Day(1, 9), Day(1, 10)], broker.Values.Keys);
        Assert.All(broker.Values.Values, v => Assert.Equal(120, v, 9));
    }

    [Fact]
    public void Days_before_the_first_close_have_no_value_and_a_closed_position_drops_out()
    {
        var c = Client();
        c.Transactions.Clear();
        c.Transactions.Add(Tx("a", PType.Purchase, "2023-12-30", 100m, "sec-gold", 1m, "pf-a"));   // before any close
        c.Transactions.Add(Tx("b", PType.Sale, "2024-01-03", 55m, "sec-gold", 1m, "pf-a"));
        var vault = PortfolioValuation.Compute(c, Today).Portfolios.Single(p => p.Uuid == "pf-a");

        Assert.Empty(vault.Positions);
        var gold = vault.History.Single().Values;
        Assert.Equal([Day(1, 1), Day(1, 2)], gold.Keys);
    }

    [Fact]
    public void Cash_accounts_carry_their_balance()
    {
        var cash = Assert.Single(Valuation().CashAccounts);
        Assert.Equal("Cash", cash.Name);
        Assert.Equal(672, cash.Balance, 9);   // +1000 −510 +175 +8 −1
    }

    [Theory]
    [InlineData(15.0, "15")]
    [InlineData(0.42, "0.42")]
    [InlineData(0.00001, "1e-05")]
    [InlineData(1.23456789, "1.23457")]
    public void Share_counts_print_like_printf_g(double shares, string expected) =>
        Assert.Equal(expected, PortfolioValuation.TrimShares(shares));
}
