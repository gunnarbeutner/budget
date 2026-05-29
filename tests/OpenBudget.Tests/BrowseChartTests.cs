using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests;

/// <summary>
/// Characterization tests for the Browse history chart math extracted from Browse.razor:
/// window resolution, per-bucket summing, anchor-period flagging, the invert-y drop rule, and
/// the account-daily running-balance back-walk.
/// </summary>
public class BrowseChartTests
{
    [Theory]
    [InlineData("M", 3)]   // Jan..Mar 2026
    [InlineData("Q", 3)]   // Q1..Q3 2026
    [InlineData("Y", 3)]   // 2024..2026
    public void ResolveChartWindow_counts_buckets_per_granularity(string gran, int expectedCount)
    {
        var earliest = gran == "Y" ? new DateTime(2024, 5, 1)
                     : gran == "Q" ? new DateTime(2026, 1, 1)
                     : new DateTime(2026, 1, 15);
        var anchor = gran == "Q" ? new DateTime(2026, 9, 1) : new DateTime(2026, 3, 1);

        var (_, _, count, _, _) = BrowseChart.ResolveChartWindow(earliest, anchor, gran);

        Assert.Equal(expectedCount, count);
    }

    [Fact]
    public void ResolveChartWindow_daily_spans_one_day_per_bucket()
    {
        var (_, _, count, _, _) = BrowseChart.ResolveChartWindow(
            new DateTime(2026, 3, 1), new DateTime(2026, 3, 1), "D");
        Assert.Equal(31, count); // March 1..31
    }

    [Fact]
    public void Monthly_series_sums_rows_into_their_month_and_flags_the_anchor()
    {
        var raw = new List<(DateTime, decimal)>
        {
            (new DateTime(2026, 1, 20), -100m),
            (new DateTime(2026, 2, 10), -50m),
            (new DateTime(2026, 3, 5), 30m),
        };

        var chart = BrowseChart.BuildChartSeries(
            earliest: new DateTime(2026, 1, 15), rightEdge: new DateTime(2026, 3, 1), granularity: "M",
            anchorYear: 2026, fromMonth: 3, toMonth: 3,
            requestInvertY: true, isAccountDaily: false,
            raw: raw, currentBalanceForDaily: null);

        Assert.Equal(3, chart.Bars.Length);
        Assert.Equal(-100m, chart.Bars[0].Value);
        Assert.Equal(-50m, chart.Bars[1].Value);
        Assert.Equal(30m, chart.Bars[2].Value);
        Assert.Equal(new[] { (2026, 1), (2026, 2), (2026, 3) }, chart.BarMonths);
        // Only March is in the anchor period.
        Assert.False(chart.Bars[0].IsAnchor);
        Assert.False(chart.Bars[1].IsAnchor);
        Assert.True(chart.Bars[2].IsAnchor);
        // A positive bar present → inversion is dropped even though it was requested.
        Assert.False(chart.InvertY);
    }

    [Fact]
    public void Invert_y_is_kept_when_every_bar_is_nonpositive()
    {
        var raw = new List<(DateTime, decimal)>
        {
            (new DateTime(2026, 1, 20), -100m),
            (new DateTime(2026, 2, 10), -50m),
        };

        var chart = BrowseChart.BuildChartSeries(
            earliest: new DateTime(2026, 1, 1), rightEdge: new DateTime(2026, 2, 1), granularity: "M",
            anchorYear: 2026, fromMonth: 2, toMonth: 2,
            requestInvertY: true, isAccountDaily: false,
            raw: raw, currentBalanceForDaily: null);

        Assert.True(chart.InvertY);
    }

    [Fact]
    public void Account_daily_back_walks_a_running_closing_balance()
    {
        // March, daily. Current balance 1000. One tx after the window (€20) is excluded from
        // bars but lowers the window's closing balance to 980.
        var raw = new List<(DateTime, decimal)>
        {
            (new DateTime(2026, 3, 2), 100m),
            (new DateTime(2026, 3, 3), -50m),
            (new DateTime(2026, 4, 5), 20m), // after window
        };

        var chart = BrowseChart.BuildChartSeries(
            earliest: new DateTime(2026, 3, 1), rightEdge: new DateTime(2026, 3, 1), granularity: "D",
            anchorYear: 2026, fromMonth: 3, toMonth: 3,
            requestInvertY: false, isAccountDaily: true,
            raw: raw, currentBalanceForDaily: 1000m);

        // closing[end] = 1000 − 20 = 980; back-walk: bar[1]=980+50=1030, bar[0]=1030−100=930.
        Assert.Equal(930m, chart.Bars[0].Value);   // Mar 1
        Assert.Equal(1030m, chart.Bars[1].Value);  // Mar 2
        Assert.Equal(980m, chart.Bars[2].Value);   // Mar 3
        Assert.Equal(980m, chart.Bars[^1].Value);  // Mar 31 (window close)
    }
}
