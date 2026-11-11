using OpenBudget.Models;
using Xunit;

namespace OpenBudget.Tests;

/// <summary>
/// PortfolioService.GetProjectHoldingsAsync aggregates a project's depots into one view, merging a
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
        t.Db.ProjectAccountAssignments.Add(new ProjectAccountAssignment { ProjectId = 1, AccountId = 20 });
        t.Db.ProjectAccountAssignments.Add(new ProjectAccountAssignment { ProjectId = 1, AccountId = 21 });

        // Same ETF in both depots — should merge.
        AddHolding(t, 20, "IE000WORLD0", "VWRL", "World ETF", quantity: 10, amount: 1000, purchasePerUnit: 80);
        AddHolding(t, 21, "IE000WORLD0", "VWRL", "World ETF", quantity: 5, amount: 500, purchasePerUnit: 80);
        // ISIN-less gold in one depot — stays its own row, no cost basis.
        AddHolding(t, 20, null, "XAU", "Gold", quantity: 1, amount: 2000, purchasePerUnit: null);
        await t.Db.SaveChangesAsync();

        var combined = await t.Portfolio.GetProjectHoldingsAsync(1);

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
    public async Task LookThrough_rolls_unmatched_value_including_gold_into_unclassified()
    {
        await using var t = new TestDb();
        t.AddProject(1);
        t.AddAccount(20, "Depot").Type = 7;
        // 300 of classified equity, 700 of ISIN-less gold.
        AddHolding(t, 20, "IE000EQ0", "EQ", "Equity ETF", quantity: 3, amount: 300, purchasePerUnit: null);
        AddHolding(t, 20, null, "XAU", "Gold", quantity: 1, amount: 700, purchasePerUnit: null);
        t.Db.SecurityTaxonomies.Add(new SecurityTaxonomy
        {
            SourceId = 1,
            Isin = "IE000EQ0",
            TaxonomyId = "t1",
            TaxonomyName = "Regions",
            ClassificationId = "c1",
            ClassificationName = "America",
            ClassificationPath = "America",
            WeightBp = 10000
        });
        await t.Db.SaveChangesAsync();

        var view = await t.Portfolio.GetAccountHoldingsAsync(1, 20);

        Assert.NotNull(view);
        var regions = view!.Taxonomies.Single(x => x.TaxonomyName == "Regions");
        // The crux: slices sum to the whole account value, not just the classified part.
        Assert.Equal(1000m, regions.Slices.Sum(s => s.Value));
        Assert.Equal(300m, regions.Slices.Single(s => s.Label == "America").Value);
        Assert.Equal(700m, regions.Slices.Single(s => s.Label == "Unclassified").Value);
    }

    private static SecurityTaxonomy Tax(string isin, string taxonomy, string cls, int bp) => new()
    {
        SourceId = 1,
        Isin = isin,
        TaxonomyId = taxonomy,
        TaxonomyName = taxonomy,
        ClassificationId = cls,
        ClassificationName = cls,
        ClassificationPath = cls,
        WeightBp = bp
    };

    [Fact]
    public async Task AssetClass_lookthrough_falls_back_to_moneymoney_type_for_positions_pp_does_not_know()
    {
        await using var t = new TestDb();
        t.AddProject(1);
        t.AddAccount(20, "Depot").Type = 7;
        // PP-classified equity ETF, a position PP doesn't know (MoneyMoney type "Other (3)"),
        // a commodity PP classifies, and an equity stock PP doesn't know (MoneyMoney "Equity").
        AddHolding(t, 20, "IE000EQ0", "EQ", "Equity ETF", quantity: 4, amount: 400, purchasePerUnit: null);
        AddHolding(t, 20, "DE000OTH0", "OTH", "Other Position", quantity: 1, amount: 300, purchasePerUnit: null);
        AddHolding(t, 20, "DE000COM0", "COM", "Commodity ETC", quantity: 2, amount: 200, purchasePerUnit: null);
        AddHolding(t, 20, "US000STK0", "STK", "Some Stock", quantity: 1, amount: 100, purchasePerUnit: null);
        var list = t.Db.ChangeTracker.Entries<Holding>().Select(e => e.Entity).ToList();
        list.Single(h => h.Isin == "IE000EQ0").AssetClass = 6;
        list.Single(h => h.Isin == "DE000OTH0").AssetClass = 3;
        list.Single(h => h.Isin == "DE000COM0").AssetClass = 3;
        list.Single(h => h.Isin == "US000STK0").AssetClass = 6;
        t.Db.SecurityTaxonomies.AddRange(
            Tax("IE000EQ0", "Asset Classes", "equity", 10000),
            Tax("DE000COM0", "Asset Classes", "Commodity", 10000),
            Tax("IE000EQ0", "Regions", "America", 10000));
        await t.Db.SaveChangesAsync();

        var view = await t.Portfolio.GetAccountHoldingsAsync(1, 20);

        var assets = view!.Taxonomies.Single(x => x.TaxonomyName == "Asset Classes");
        Assert.Equal(1m, assets.Slices.Sum(s => s.Fraction));
        Assert.DoesNotContain(assets.Slices, s => s.Label == "Unclassified");
        Assert.Equal(500m, assets.Slices.Single(s => s.Label == "equity").Value);   // PP 400 + MoneyMoney "Equity" 100
        Assert.Equal(200m, assets.Slices.Single(s => s.Label == "Commodity").Value);
        Assert.Equal(300m, assets.Slices.Single(s => s.Label == "Other (3)").Value);

        // Other taxonomies keep "Unclassified" for what PP doesn't know.
        var regions = view.Taxonomies.Single(x => x.TaxonomyName == "Regions");
        Assert.Equal(1m, regions.Slices.Sum(s => s.Fraction));
        Assert.Equal(600m, regions.Slices.Single(s => s.Label == "Unclassified").Value);
    }

    private static SecurityTaxonomy UuidTax(string uuid, string taxonomy, string cls, int bp)
    {
        var row = Tax("", taxonomy, cls, bp);
        row.SecurityUuid = uuid;
        return row;
    }

    [Fact]
    public async Task LookThrough_classifies_isinless_holdings_by_pp_security_uuid()
    {
        await using var t = new TestDb();
        t.AddProject(1);
        t.AddAccount(20, "Depot").Type = 7;
        // An ETF matched by ISIN, a sync-written holding carrying its PP uuid, an imported holding
        // linked to a PP security by hand, and an unmatched one.
        AddHolding(t, 20, "IE000EQ0", "EQ", "Equity ETF", quantity: 1, amount: 100, purchasePerUnit: null);
        AddHolding(t, 20, null, "ZZQ-EUR", "Sample Token", quantity: 1, amount: 200, purchasePerUnit: null);
        AddHolding(t, 20, null, null, "Sample House", quantity: 1, amount: 300, purchasePerUnit: null);
        AddHolding(t, 20, null, "ZZX", "Unknown Thing", quantity: 1, amount: 400, purchasePerUnit: null);
        var list = t.Db.ChangeTracker.Entries<Holding>().Select(e => e.Entity).ToList();
        list.Single(h => h.Symbol == "ZZQ-EUR").PpSecurityUuid = "pp-token";
        t.Db.PpSecurityLinks.Add(new PpSecurityLink { SourceId = 1, PpSecurityUuid = "pp-house", MmHoldingKey = "name:Sample House" });
        t.Db.SecurityTaxonomies.AddRange(
            Tax("IE000EQ0", "Asset Classes", "Equity", 10000),
            UuidTax("pp-token", "Asset Classes", "Alternatives", 10000),
            UuidTax("pp-house", "Asset Classes", "Real Estate", 10000),
            // A classified security nobody holds stays out of the view.
            UuidTax("pp-unrelated", "Asset Classes", "Bonds", 10000));
        await t.Db.SaveChangesAsync();

        var view = await t.Portfolio.GetAccountHoldingsAsync(1, 20);

        var assets = view!.Taxonomies.Single(x => x.TaxonomyName == "Asset Classes");
        Assert.Equal(100m, assets.Slices.Single(s => s.Label == "Equity").Value);
        Assert.Equal(200m, assets.Slices.Single(s => s.Label == "Alternatives").Value);
        Assert.Equal(300m, assets.Slices.Single(s => s.Label == "Real Estate").Value);
        Assert.DoesNotContain(assets.Slices, s => s.Label == "Bonds");
        Assert.Equal(1000m, assets.Slices.Sum(s => s.Value));

        Assert.Equal("uuid:pp-token", view.Rows.Single(r => r.Symbol == "ZZQ-EUR").TaxonomyKey);
        Assert.Equal("uuid:pp-house", view.Rows.Single(r => r.Name == "Sample House").TaxonomyKey);
        Assert.Equal("isin:IE000EQ0", view.Rows.Single(r => r.Isin == "IE000EQ0").TaxonomyKey);
        Assert.Null(view.Rows.Single(r => r.Symbol == "ZZX").TaxonomyKey);

        var weights = await t.Portfolio.GetSecurityTaxonomyWeightsAsync(1, "Asset Classes",
            new[] { "isin:IE000EQ0", "uuid:pp-token", "uuid:pp-house" });
        Assert.Equal(1m, weights["isin:IE000EQ0"]["Equity"]);
        Assert.Equal(1m, weights["uuid:pp-token"]["Alternatives"]);
        Assert.Equal(1m, weights["uuid:pp-house"]["Real Estate"]);
    }

    [Theory]
    [InlineData("Asset Classes", true)]
    [InlineData("anlageklassen", true)]
    [InlineData("Regions", false)]
    [InlineData(null, false)]
    public void IsAssetClassTaxonomy_matches_pp_default_names(string? name, bool expected) =>
        Assert.Equal(expected, OpenBudget.Services.PortfolioService.IsAssetClassTaxonomy(name));

    [Fact]
    public async Task Combined_returns_null_when_no_holdings()
    {
        await using var t = new TestDb();
        t.AddProject(1);
        t.AddAccount(20, "Empty depot").Type = 7;
        t.Db.ProjectAccountAssignments.Add(new ProjectAccountAssignment { ProjectId = 1, AccountId = 20 });
        await t.Db.SaveChangesAsync();

        var combined = await t.Portfolio.GetProjectHoldingsAsync(1);

        Assert.Null(combined);
    }
}
