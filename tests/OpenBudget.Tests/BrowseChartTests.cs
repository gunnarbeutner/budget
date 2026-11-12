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
    public void ResolveChartWindow_daily_ends_with_the_last_day()
    {
        var (start, end, count, _, _) = BrowseChart.ResolveChartWindow(
            new DateTime(2025, 12, 30), new DateTime(2026, 3, 1), "D", lastDay: new DateTime(2026, 3, 12));
        Assert.Equal(new DateTime(2025, 12, 30), start);
        Assert.Equal(new DateTime(2026, 3, 13), end);
        Assert.Equal(73, count); // 30.12.2025 .. 12.03.2026, nothing after the 12th
    }

    [Fact]
    public void Daily_balance_over_the_whole_history_highlights_only_the_selected_period()
    {
        var raw = new List<(DateTime, decimal)>
        {
            (new DateTime(2026, 1, 10), 500m),
            (new DateTime(2026, 2, 3), -100m),
        };

        var chart = BrowseChart.BuildChartSeries(
            earliest: new DateTime(2026, 1, 10), rightEdge: new DateTime(2026, 3, 1), granularity: "D",
            anchorYear: 2026, fromMonth: 2, toMonth: 2,
            requestInvertY: false, isAccountDaily: true,
            raw: raw, currentBalanceForDaily: 400m,
            today: new DateTime(2026, 3, 12),
            highlight: (new DateTime(2026, 2, 1), new DateTime(2026, 3, 1)),
            lastDay: new DateTime(2026, 3, 12));

        Assert.Equal(62, chart.Bars.Length);   // 10.01. .. 12.03.
        Assert.Equal(new DateTime(2026, 3, 12), chart.BarStarts[^1]);
        Assert.Equal(500m, chart.Bars[0].Value);
        Assert.Equal(400m, chart.Bars[^1].Value);
        var highlighted = Enumerable.Range(0, chart.Bars.Length).Where(i => chart.Bars[i].IsAnchor).ToArray();
        Assert.Equal(28, highlighted.Length);
        Assert.Equal(new DateTime(2026, 2, 1), chart.BarStarts[highlighted[0]]);
        Assert.Equal(new DateTime(2026, 2, 28), chart.BarStarts[highlighted[^1]]);
    }

    [Fact]
    public void A_highlight_range_flags_every_month_it_touches()
    {
        var chart = BrowseChart.BuildChartSeries(
            earliest: new DateTime(2025, 10, 1), rightEdge: new DateTime(2026, 3, 1), granularity: "M",
            anchorYear: 2026, fromMonth: 1, toMonth: 1,
            requestInvertY: false, isAccountDaily: false,
            raw: [], currentBalanceForDaily: null,
            highlight: (new DateTime(2025, 12, 15), new DateTime(2026, 2, 2)));

        Assert.Equal(new[] { false, false, true, true, true, false }, chart.Bars.Select(b => b.IsAnchor).ToArray());
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
        Assert.Equal(new[] { new DateTime(2026, 1, 1), new DateTime(2026, 2, 1), new DateTime(2026, 3, 1) }, chart.BarStarts);
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

    private static decimal[] Flat(decimal v) => Enumerable.Repeat(v, 12).ToArray();

    [Fact]
    public void BuildBudgetSeries_monthly_constant_and_negated_for_expenses()
    {
        var bars = new[] { (2026, 1), (2026, 2), (2026, 3) };
        var series = BrowseChart.BuildBudgetSeries(bars, "M", new Dictionary<int, decimal[]> { [2026] = Flat(100m) }, negate: true);
        Assert.Equal(new decimal?[] { -100m, -100m, -100m }, series);
    }

    [Fact]
    public void BuildBudgetSeries_follows_varying_months_and_gaps_without_a_budget()
    {
        var months = Flat(50m);
        months[11] = 300m; // December
        var bars = new[] { (2025, 11), (2025, 12), (2026, 1) };
        var series = BrowseChart.BuildBudgetSeries(bars, "M", new Dictionary<int, decimal[]> { [2025] = months }, negate: false);
        Assert.Equal(new decimal?[] { 50m, 300m, null }, series);
    }

    [Fact]
    public void BuildBudgetSeries_sums_quarters_and_years_and_skips_days()
    {
        var byYear = new Dictionary<int, decimal[]> { [2026] = Flat(10m) };
        Assert.Equal(new decimal?[] { 30m, 30m }, BrowseChart.BuildBudgetSeries(new[] { (2026, 1), (2026, 4) }, "Q", byYear, false));
        Assert.Equal(new decimal?[] { 120m }, BrowseChart.BuildBudgetSeries(new[] { (2026, 1) }, "Y", byYear, false));
        Assert.Null(BrowseChart.BuildBudgetSeries(new[] { (2026, 1) }, "D", byYear, false));
        Assert.Null(BrowseChart.BuildBudgetSeries(new[] { (2026, 1) }, "M", new Dictionary<int, decimal[]>(), false));
    }

    [Fact]
    public void BudgetMonthsFor_sums_a_groups_children_and_returns_null_without_budget()
    {
        var group = new CategoryGroupWithChildCount { CategoryGroup = new OpenBudget.Models.CategoryGroup { Id = 1, Name = "Home" }, ItemType = CategoryItemType.Group };
        var a = new CategoryGroupWithChildCount { CategoryGroup = new OpenBudget.Models.CategoryGroup { Id = 1 }, Category = new OpenBudget.Models.Category { Id = 10, GroupId = 1, Name = "Rent" }, ItemType = CategoryItemType.Category, ParentCategoryGroupId = 1 };
        var b = new CategoryGroupWithChildCount { CategoryGroup = new OpenBudget.Models.CategoryGroup { Id = 1 }, Category = new OpenBudget.Models.Category { Id = 11, GroupId = 1, Name = "Power" }, ItemType = CategoryItemType.Category, ParentCategoryGroupId = 1 };
        var hierarchy = new List<CategoryGroupWithChildCount> { group, a, b };
        var stored = new Dictionary<(long, CategoryItemType), decimal[]>
        {
            [(10, CategoryItemType.Category)] = Flat(500m),
            [(11, CategoryItemType.Category)] = Flat(80m),
        };

        Assert.Equal(Flat(580m), BrowseChart.BudgetMonthsFor(1, CategoryItemType.Group, stored, hierarchy));
        Assert.Equal(Flat(80m), BrowseChart.BudgetMonthsFor(11, CategoryItemType.Category, stored, hierarchy));
        Assert.Null(BrowseChart.BudgetMonthsFor(12, CategoryItemType.Category, stored, hierarchy));
    }

    [Fact]
    public void Daily_bars_start_on_consecutive_days_of_the_period()
    {
        var chart = BrowseChart.BuildChartSeries(
            earliest: new DateTime(2026, 2, 1), rightEdge: new DateTime(2026, 2, 1), granularity: "D",
            anchorYear: 2026, fromMonth: 2, toMonth: 2,
            requestInvertY: false, isAccountDaily: true,
            raw: new List<(DateTime, decimal)>(), currentBalanceForDaily: 10m);

        Assert.Equal(28, chart.BarStarts.Length);
        Assert.Equal(new DateTime(2026, 2, 1), chart.BarStarts[0]);
        Assert.Equal(new DateTime(2026, 2, 28), chart.BarStarts[^1]);
    }
}
