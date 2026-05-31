using OpenBudget.Models;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests;

public class TaxMathTests
{
    private static OpenLot Lot(int y, int m, int d, decimal shares, decimal cost) =>
        new() { Isin = "X", Acquired = new DateOnly(y, m, d), Shares = shares, Cost = cost };

    [Fact]
    public void Security_gain_is_fully_taxable_without_teilfreistellung()
    {
        var lots = new[] { Lot(2020, 1, 1, 10, 1000) };

        var r = TaxMath.RealizeSale(lots, sharesToSell: 10, pricePerShare: 150, new DateOnly(2024, 1, 1), TaxAssetKind.Security);

        Assert.Equal(1500m, r.Proceeds);
        Assert.Equal(1000m, r.Cost);
        Assert.Equal(500m, r.GrossGain);
        Assert.Equal(500m, r.TaxableGain);
        Assert.Equal(0m, r.UncoveredShares);
    }

    [Fact]
    public void Teilfreistellung_exempts_a_fraction_of_a_fund_gain()
    {
        var lots = new[] { Lot(2020, 1, 1, 10, 1000) };

        var r = TaxMath.RealizeSale(lots, 10, 150, new DateOnly(2024, 1, 1), TaxAssetKind.Security, teilfreistellungRate: 0.30m);

        Assert.Equal(500m, r.GrossGain);
        Assert.Equal(350m, r.TaxableGain);   // 70% of the gain counts
    }

    [Fact]
    public void Sale_matches_lots_fifo_oldest_first_pro_rating_a_partial_lot()
    {
        var lots = new[]
        {
            Lot(2021, 1, 1, 10, 1500),   // newer — bought later but listed first
            Lot(2019, 1, 1, 10, 1000),   // oldest — must be consumed first
        };

        var r = TaxMath.RealizeSale(lots, sharesToSell: 15, pricePerShare: 200, new DateOnly(2024, 1, 1), TaxAssetKind.Security);

        Assert.Equal(2, r.Lots.Count);
        Assert.Equal(new DateOnly(2019, 1, 1), r.Lots[0].Acquired);  // oldest first
        Assert.Equal(10m, r.Lots[0].Shares);
        Assert.Equal(new DateOnly(2021, 1, 1), r.Lots[1].Acquired);
        Assert.Equal(5m, r.Lots[1].Shares);
        Assert.Equal(750m, r.Lots[1].Cost);                          // 1500 × 5/10
        Assert.Equal(3000m, r.Proceeds);
        Assert.Equal(1750m, r.Cost);
        Assert.Equal(1250m, r.GrossGain);
    }

    [Fact]
    public void Private_sale_held_over_a_year_is_tax_free()
    {
        var lots = new[] { Lot(2022, 1, 1, 1, 10000) };

        var r = TaxMath.RealizeSale(lots, 1, 30000, new DateOnly(2024, 1, 1), TaxAssetKind.PrivateSale);

        Assert.Equal(20000m, r.GrossGain);
        Assert.Equal(0m, r.TaxableGain);            // §23 exemption
        Assert.True(r.Lots[0].HoldingPeriodExempt);
    }

    [Fact]
    public void Private_sale_within_a_year_is_fully_taxable_income()
    {
        var lots = new[] { Lot(2023, 6, 1, 1, 10000) };

        var r = TaxMath.RealizeSale(lots, 1, 15000, new DateOnly(2023, 12, 1), TaxAssetKind.PrivateSale);

        Assert.False(r.Lots[0].HoldingPeriodExempt);
        Assert.Equal(5000m, r.TaxableGain);
    }

    [Fact]
    public void Private_sale_at_exactly_one_year_is_still_taxable()
    {
        // The exemption needs *more* than a year; selling on the anniversary is still within scope.
        var lots = new[] { Lot(2023, 1, 1, 1, 10000) };

        var r = TaxMath.RealizeSale(lots, 1, 12000, new DateOnly(2024, 1, 1), TaxAssetKind.PrivateSale);

        Assert.False(r.Lots[0].HoldingPeriodExempt);
        Assert.Equal(2000m, r.TaxableGain);
    }

    [Fact]
    public void Teilfreistellung_also_shrinks_a_deductible_loss()
    {
        var lots = new[] { Lot(2020, 1, 1, 10, 2000) };

        var r = TaxMath.RealizeSale(lots, 10, 150, new DateOnly(2024, 1, 1), TaxAssetKind.Security, teilfreistellungRate: 0.30m);

        Assert.Equal(-500m, r.GrossGain);
        Assert.Equal(-350m, r.TaxableGain);   // only 70% of the loss is deductible
    }

    [Fact]
    public void Selling_more_than_is_held_reports_the_uncovered_shares()
    {
        var lots = new[] { Lot(2020, 1, 1, 5, 500) };

        var r = TaxMath.RealizeSale(lots, sharesToSell: 8, pricePerShare: 120, new DateOnly(2024, 1, 1), TaxAssetKind.Security);

        Assert.Equal(5m, r.SharesSold);
        Assert.Equal(3m, r.UncoveredShares);
        Assert.Equal(100m, r.GrossGain);      // only the 5 held shares are realised
    }
}
