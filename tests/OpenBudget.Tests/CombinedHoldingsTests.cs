using OpenBudget.Models;
using Xunit;

namespace OpenBudget.Tests;

/// <summary>
/// PortfolioService.GetCombinedHoldingsAsync aggregates several depots into one view, merging a
/// security held in more than one depot into a single row.
/// </summary>
public class CombinedHoldingsTests
{
    private long _nextId = 1000;

    private void AddHolding(
        TestDb t, long accountId, string? isin, string? symbol, string name,
        double quantity, double amount, double? purchasePerUnit)
    {
        t.Db.Holdings.Add(new Holding
        {
            Id = _nextId++,
            SourceId = 1,
            AccountId = accountId,
            Name = name,
            Isin = isin,
            Symbol = symbol,
            Quantity = quantity,
            Amount = amount,
            AmountCurrency = "EUR",
            Price = quantity > 0 ? amount / quantity : 0,
            Purchase = purchasePerUnit
        });
    }

    [Fact]
    public async Task Combined_merges_same_isin_across_depots_and_keeps_gold_separate()
    {
        await using var t = new TestDb();
        t.AddProject(1);
        var a = t.AddAccount(20, "Depot A");
        a.Type = 7;
        var b = t.AddAccount(21, "Depot B");
        b.Type = 7;

        // Same ETF in both depots — should merge.
        AddHolding(t, 20, "IE000WORLD0", "VWRL", "World ETF", quantity: 10, amount: 1000, purchasePerUnit: 80);
        AddHolding(t, 21, "IE000WORLD0", "VWRL", "World ETF", quantity: 5, amount: 500, purchasePerUnit: 80);
        // ISIN-less gold in one depot — stays its own row, no cost basis.
        AddHolding(t, 20, null, "XAU", "Gold", quantity: 1, amount: 2000, purchasePerUnit: null);
        await t.Db.SaveChangesAsync();

        var combined = await t.Portfolio.GetCombinedHoldingsAsync(1, new long[] { 20, 21 });

        Assert.NotNull(combined);
        Assert.Equal(3500m, combined!.TotalValue);
        Assert.Equal(2, combined.Rows.Count);

        var etf = combined.Rows.Single(r => r.Isin == "IE000WORLD0");
        Assert.Equal(15, etf.Quantity);          // 10 + 5
        Assert.Equal(1500m, etf.Value);          // 1000 + 500
        Assert.Equal(1200m, etf.CostBasis);      // 80 * 15
        Assert.Equal(300m, etf.Gain);            // 1500 - 1200

        var gold = combined.Rows.Single(r => r.Isin == null);
        Assert.Equal(2000m, gold.Value);
        Assert.Null(gold.Gain);                   // no purchase price for gold
    }

    [Fact]
    public async Task Combined_returns_null_when_no_holdings()
    {
        await using var t = new TestDb();
        t.AddProject(1);
        t.AddAccount(20, "Empty depot").Type = 7;
        await t.Db.SaveChangesAsync();

        var combined = await t.Portfolio.GetCombinedHoldingsAsync(1, new long[] { 20 });

        Assert.Null(combined);
    }
}
