using OpenBudget.Models;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests;

/// <summary>
/// Balance charts of account sets that include depots: depots contribute their stored value
/// history (portfolio_chart), the other accounts their booking-based running balance.
/// </summary>
public class DepotBalanceChartTests
{
    private static readonly DateTime Today = new(2026, 3, 20);

    private static DepotValueSeries Depot(decimal current, params (DateTime, decimal)[] points) => new(points, current);

    [Fact]
    public void DepotValueAt_carries_the_last_point_forward_and_is_zero_before_the_first()
    {
        var depot = Depot(900m, (new DateTime(2026, 1, 10), 100m), (new DateTime(2026, 2, 5), 200m));

        Assert.Equal(0m, BrowseChart.DepotValueAt(depot, new DateTime(2026, 1, 10), Today));
        Assert.Equal(100m, BrowseChart.DepotValueAt(depot, new DateTime(2026, 1, 11), Today));
        Assert.Equal(100m, BrowseChart.DepotValueAt(depot, new DateTime(2026, 2, 1), Today));
        Assert.Equal(200m, BrowseChart.DepotValueAt(depot, new DateTime(2026, 3, 1), Today));
        // A bucket reaching today shows the depot's current value.
        Assert.Equal(900m, BrowseChart.DepotValueAt(depot, new DateTime(2026, 4, 1), Today));
    }

    [Fact]
    public void Monthly_balance_sums_depot_values_and_the_ledger_running_balance()
    {
        // Ledger: a delivery of -1000 in January, a sale of +400 in March; balance today -600.
        var raw = new List<(DateTime, decimal)>
        {
            (new DateTime(2026, 1, 15), -1000m),
            (new DateTime(2026, 3, 10), 400m),
        };
        var depot = Depot(1500m, (new DateTime(2026, 1, 15), 1000m), (new DateTime(2026, 2, 20), 1800m));

        var chart = BrowseChart.BuildChartSeries(
            earliest: new DateTime(2026, 1, 15), rightEdge: new DateTime(2026, 3, 1), granularity: "M",
            anchorYear: 2026, fromMonth: 3, toMonth: 3,
            requestInvertY: false, isAccountDaily: true,
            raw: raw, currentBalanceForDaily: -600m,
            depots: new[] { depot }, today: Today);

        Assert.Equal(new[] { 0m, 800m, 900m }, chart.Bars.Select(b => b.Value));
    }

    [Fact]
    public void Daily_balance_steps_with_each_depot_point()
    {
        var depot = Depot(300m, (new DateTime(2026, 3, 2), 100m), (new DateTime(2026, 3, 4), 250m));

        var chart = BrowseChart.BuildChartSeries(
            earliest: new DateTime(2026, 3, 1), rightEdge: new DateTime(2026, 3, 1), granularity: "D",
            anchorYear: 2026, fromMonth: 3, toMonth: 3,
            requestInvertY: false, isAccountDaily: true,
            raw: new List<(DateTime, decimal)>(), currentBalanceForDaily: 0m,
            depots: new[] { depot }, today: Today);

        Assert.Equal(0m, chart.Bars[0].Value);
        Assert.Equal(100m, chart.Bars[1].Value);
        Assert.Equal(100m, chart.Bars[2].Value);
        Assert.Equal(250m, chart.Bars[3].Value);
        Assert.Equal(250m, chart.Bars[18].Value);  // 19 March
        Assert.Equal(300m, chart.Bars[19].Value);  // today
        Assert.Equal(300m, chart.Bars[30].Value);
    }

    private static long Noon(DateTime d) =>
        new DateTimeOffset(d.Year, d.Month, d.Day, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

    [Fact]
    public async Task Group_of_depot_and_ledger_charts_their_sum()
    {
        await using var t = new TestDb();
        t.AddAccount(10, "Depot");
        t.AddAccount(11, "Ledger");
        t.AddGroup(1);
        t.AddLeaf(2, 1);
        t.AddTransaction(1, 2, -100000, new DateTime(2025, 6, 15), accountId: 11);
        t.AddTransaction(2, 2, 100000, new DateTime(2026, 2, 15), accountId: 11);
        // A total row on one day, per-security rows only on another (summed for that day).
        t.Db.PortfolioChartPoints.AddRange(
            new PortfolioChartPoint { Id = 1, SourceId = 1, AccountId = 10, Timestamp = Noon(new DateTime(2025, 6, 15)), Balance = 1000.0 },
            new PortfolioChartPoint { Id = 2, SourceId = 1, AccountId = 10, Timestamp = Noon(new DateTime(2025, 6, 15)), Balance = 999.0, SecurityId = 5 },
            new PortfolioChartPoint { Id = 3, SourceId = 1, AccountId = 10, Timestamp = Noon(new DateTime(2025, 12, 1)), Balance = 700.0, SecurityId = 5 },
            new PortfolioChartPoint { Id = 4, SourceId = 1, AccountId = 10, Timestamp = Noon(new DateTime(2025, 12, 1)), Balance = 500.0, SecurityId = 6 });
        await t.Db.SaveChangesAsync();

        var history = await t.TransactionQueries.GetDepotValueHistoryAsync(1, new long[] { 10, 11 });
        Assert.Equal(new[] { 10L }, history.Keys);
        Assert.Equal(new[] { (new DateTime(2025, 6, 15), 1000m), (new DateTime(2025, 12, 1), 1200m) }, history[10]);

        var raw = await t.TransactionQueries.GetRawByDateRangeAsync(
            1, null, new long[] { 11 }, new DateTime(2025, 6, 1), new DateTime(2026, 4, 1));
        var chart = BrowseChart.BuildChartSeries(
            earliest: new DateTime(2025, 6, 15), rightEdge: new DateTime(2026, 3, 1), granularity: "M",
            anchorYear: 2026, fromMonth: 3, toMonth: 3,
            requestInvertY: false, isAccountDaily: true,
            raw: raw, currentBalanceForDaily: 0m,
            depots: new[] { new DepotValueSeries(history[10], 0m) }, today: Today);

        var values = chart.Bars.Select(b => b.Value).ToArray();
        Assert.Equal(0m, values[0]);        // Jun '25: delivery -1000 + depot 1000
        Assert.Equal(200m, values[6]);      // Dec '25: -1000 + 1200
        Assert.Equal(1200m, values[8]);     // Feb '26: sale brings the ledger to 0
        Assert.Equal(0m, values[9]);        // Mar '26 reaches today: depot sold out
    }
}
