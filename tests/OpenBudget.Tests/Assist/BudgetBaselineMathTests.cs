using OpenBudget.Assist;
using Xunit;

namespace OpenBudget.Tests.Assist;

/// <summary>Deterministic budget statistics on synthetic series.</summary>
public sealed class BudgetBaselineMathTests
{
    private static readonly DateOnly AsOf = new(2026, 1, 1);
    private static long _id;

    private static BaselineBooking B(int year, int month, decimal amount, string payee = "EXAMPLE MARKET", int day = 5) =>
        new(++_id, new DateOnly(year, month, day), amount, payee);

    private static IEnumerable<(int Year, int Month)> Months(int fromYear, int count) =>
        Enumerable.Range(0, count).Select(i => (fromYear + (i / 12), (i % 12) + 1));

    [Fact]
    public void Median_and_mad()
    {
        Assert.Equal(3m, BudgetBaselineMath.Median([5m, 1m, 3m]));
        Assert.Equal(2.5m, BudgetBaselineMath.Median([1m, 2m, 3m, 4m]));
        Assert.Equal(1m, BudgetBaselineMath.Mad([1m, 2m, 3m, 4m, 100m]));
        Assert.Equal(0, BudgetBaselineMath.RobustZ(5, 5, 0));
    }

    [Fact]
    public void A_steady_category_gets_its_median_month_and_a_one_off_purchase_is_left_out()
    {
        var bookings = Months(2023, 36).SelectMany(m => new[] { B(m.Year, m.Month, 100m), B(m.Year, m.Month, 120m, day: 20) }).ToList();
        var oneOff = B(2025, 3, 1890m, "EXAMPLE FURNITURE");
        bookings.Add(oneOff);

        var result = BudgetBaselineMath.Compute(bookings, [], 2026, AsOf);

        Assert.Equal([oneOff], result.Outliers);
        Assert.All(result.Monthly, m => Assert.Equal(220m, m));
        Assert.Equal("median", result.Basis);
        Assert.Equal(36, result.MonthsOfData);
    }

    [Fact]
    public void A_yearly_payment_lands_in_its_month_and_is_no_outlier()
    {
        var bookings = Months(2023, 36).Select(m => B(m.Year, m.Month, 50m, "BAKERY")).ToList();
        bookings.AddRange([B(2023, 4, 600m, "EXAMPLE INSURANCE"), B(2024, 4, 610m, "EXAMPLE INSURANCE"), B(2025, 4, 620m, "EXAMPLE INSURANCE")]);

        var result = BudgetBaselineMath.Compute(bookings, [], 2026, AsOf);

        Assert.Empty(result.Outliers);
        var yearly = Assert.Single(result.Recurring, r => r.IntervalMonths == 12);
        Assert.Equal("EXAMPLE INSURANCE", yearly.PayeeKey);
        Assert.Equal(50m + 610m, result.Monthly[3]);
        Assert.Equal(50m, result.Monthly[4]);
    }

    [Fact]
    public void Quarterly_payments_within_ten_percent_are_recurring_bigger_jumps_are_not()
    {
        var steady = new[] { B(2025, 1, 90m, "WATER"), B(2025, 4, 95m, "WATER"), B(2025, 7, 92m, "WATER"), B(2025, 10, 94m, "WATER") };
        var series = Assert.Single(BudgetBaselineMath.FindRecurring(steady));
        Assert.Equal(3, series.IntervalMonths);
        Assert.Equal([0, 3, 6, 9], BudgetBaselineMath.MonthsOf(series, 2026));

        var jumpy = new[] { B(2025, 1, 90m, "GYM"), B(2025, 4, 200m, "GYM") };
        Assert.Empty(BudgetBaselineMath.FindRecurring(jumpy));
    }

    [Fact]
    public void A_known_standing_order_counts_even_with_short_history()
    {
        var bookings = new List<BaselineBooking> { B(2025, 6, 300m, "EXAMPLE CLUB") };
        bookings.AddRange(Months(2025, 12).Select(m => B(m.Year, m.Month, 10m, "KIOSK")));
        var schedule = new BaselineSchedule("EXAMPLE CLUB", 300m, 6, new DateOnly(2025, 12, 1));

        var result = BudgetBaselineMath.Compute(bookings, [schedule], 2026, AsOf);

        Assert.Equal(310m, result.Monthly[5]);
        Assert.Equal(310m, result.Monthly[11]);
        Assert.Equal(10m, result.Monthly[0]);
    }

    [Fact]
    public void Winter_heavy_spending_shows_as_seasonal_and_keeps_the_year_total()
    {
        var bookings = Months(2023, 36).Select(m => B(m.Year, m.Month, m.Month is 12 or 1 or 2 ? 250m + ((m.Year - 2023) * 50m) : 100m, "EXAMPLE ENERGY", day: 15 + (m.Month % 3))).ToList();

        var result = BudgetBaselineMath.Compute(bookings, [], 2026, AsOf);

        Assert.Equal("seasonal", result.Basis);
        Assert.True(result.Monthly[0] > result.Monthly[6] * 1.5m);
        Assert.InRange(result.Monthly.Sum(), 12 * 100m * 0.95m, 12 * 100m * 1.05m); // the median month times twelve, redistributed
    }

    [Fact]
    public void An_edited_year_total_scales_the_months_and_keeps_their_shape()
    {
        decimal[] months = [100, 100, 100, 100, 100, 100, 100, 100, 100, 100, 100, 700];
        var scaled = BudgetBaselineMath.ScaleTo(months, 900);
        Assert.Equal(900m, scaled.Sum());
        Assert.Equal(50m, scaled[0]);
        Assert.Equal(350m, scaled[11]);
        Assert.Equal(1000m, BudgetBaselineMath.ScaleTo(new decimal[12], 1000).Sum());
    }

    [Fact]
    public void No_bookings_no_budget()
    {
        var result = BudgetBaselineMath.Compute([], [], 2026, AsOf);
        Assert.Equal("none", result.Basis);
        Assert.All(result.Monthly, m => Assert.Equal(0m, m));
    }
}
