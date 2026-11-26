using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests;

/// <summary>The Portfolio view's value series: the depots' stored daily values summed and carried forward.</summary>
public class PortfolioValueHistoryTests
{
    private static readonly DateTime Today = new(2026, 3, 20);

    private static Dictionary<long, List<(DateTime Day, decimal Value)>> History(
        params (long Id, (DateTime, decimal)[] Points)[] depots) =>
        depots.ToDictionary(d => d.Id, d => d.Points.ToList());

    [Fact]
    public void Sums_the_depots_carrying_each_value_forward_and_ends_with_the_current_total()
    {
        var history = History(
            (1, [(new DateTime(2026, 3, 15), 100m), (new DateTime(2026, 3, 17), 150m)]),
            (2, [(new DateTime(2026, 3, 16), 50m)]));

        var s = PortfolioValueHistory.Build(history, 260m, Today, from: null);

        Assert.Equal(
            [
                (new DateTime(2026, 3, 15), 100m),
                (new DateTime(2026, 3, 16), 150m),
                (new DateTime(2026, 3, 17), 200m),
                (new DateTime(2026, 3, 18), 200m),
                (new DateTime(2026, 3, 19), 200m),
                (Today, 260m),
            ],
            s.Points.Select(p => (p.Day, p.Value)));
        Assert.Equal(160m, s.Change);
        Assert.Equal(160m, s.Gain);
        Assert.Equal(1.6m, s.TimeWeightedReturn);
    }

    [Fact]
    public void A_depot_without_history_contributes_only_today()
    {
        // Depot 1 has history; a second depot without any stored point is in the current total only.
        var history = History((1, [(new DateTime(2026, 3, 18), 100m)]));

        var s = PortfolioValueHistory.Build(history, 100m + 500m, Today, from: null);

        Assert.Equal([(new DateTime(2026, 3, 18), 100m), (new DateTime(2026, 3, 19), 100m), (Today, 600m)], s.Points.Select(p => (p.Day, p.Value)));
    }

    [Fact]
    public void No_history_gives_only_today()
    {
        var s = PortfolioValueHistory.Build(History(), 500m, Today, from: null);

        Assert.Equal([(Today, 500m)], s.Points.Select(p => (p.Day, p.Value)));
        Assert.Equal(0m, s.Change);
        Assert.Null(s.TimeWeightedReturn);
    }

    [Fact]
    public void The_period_cuts_the_start_and_carries_the_earlier_value_in()
    {
        var history = History((1, [(new DateTime(2020, 1, 1), 80m), (new DateTime(2026, 3, 19), 120m)]));
        var from = PortfolioValueHistory.PeriodStart(PortfolioValuePeriod.OneYear, Today);

        var s = PortfolioValueHistory.Build(history, 130m, Today, from);

        Assert.Equal(new DateTime(2025, 3, 20), from);
        Assert.Equal((new DateTime(2025, 3, 20), 80m), (s.Points[0].Day, s.Points[0].Value));
        Assert.Equal((new DateTime(2026, 3, 19), 120m), (s.Points[^2].Day, s.Points[^2].Value));
        Assert.Equal(366, s.Points.Count);
        Assert.Equal(50m, s.Change);
    }

    [Fact]
    public void A_period_reaching_before_the_history_starts_at_the_first_point()
    {
        var history = History((1, [(new DateTime(2026, 1, 1), 80m)]));

        var s = PortfolioValueHistory.Build(history, 80m, Today,
            PortfolioValueHistory.PeriodStart(PortfolioValuePeriod.ThreeYears, Today));

        Assert.Equal(new DateTime(2026, 1, 1), s.Points[0].Day);
        Assert.Null(PortfolioValueHistory.PeriodStart(PortfolioValuePeriod.Max, Today));
    }

    [Fact]
    public void Flows_are_split_from_the_gain_and_left_out_of_the_time_weighted_return()
    {
        // 100 → 110 (+10 %), then 1.000 paid in and the day ends at 1.110 (flat), then +10 % to 1.221.
        var history = History((1, [(new DateTime(2026, 3, 16), 100m), (new DateTime(2026, 3, 17), 110m), (new DateTime(2026, 3, 18), 1110m)]));
        var flows = new Dictionary<DateTime, decimal> { [new DateTime(2026, 3, 18)] = 1000m };

        var s = PortfolioValueHistory.Build(history, 1221m, Today, from: null, flows);

        Assert.Equal(1121m, s.Change);
        Assert.Equal(1000m, s.NetFlows);
        Assert.Equal(121m, s.Gain);
        Assert.Equal(0.21m, s.TimeWeightedReturn);
        Assert.Equal([100m, 100m, 1100m, 1100m, 1100m], s.Points.Select(p => p.Invested));
    }

    [Fact]
    public void Withdrawals_count_as_negative_flows_and_flows_on_the_first_day_are_in_the_start_value()
    {
        var history = History((1, [(new DateTime(2026, 3, 18), 500m), (new DateTime(2026, 3, 19), 300m)]));
        var flows = new Dictionary<DateTime, decimal>
        {
            [new DateTime(2026, 3, 1)] = 400m,   // before the period
            [new DateTime(2026, 3, 18)] = 500m,  // the first day: part of the start value
            [new DateTime(2026, 3, 19)] = -250m, // a sale paid out
        };

        var s = PortfolioValueHistory.Build(history, 300m, Today, from: null, flows);

        Assert.Equal(-200m, s.Change);
        Assert.Equal(-250m, s.NetFlows);
        Assert.Equal(50m, s.Gain);
        Assert.Equal(0.1m, s.TimeWeightedReturn);
    }

    [Fact]
    public void A_day_after_a_zero_value_measures_its_value_against_its_flows()
    {
        var history = History((1, [(new DateTime(2026, 3, 18), 0m), (new DateTime(2026, 3, 19), 105m)]));
        var flows = new Dictionary<DateTime, decimal> { [new DateTime(2026, 3, 19)] = 100m };

        var s = PortfolioValueHistory.Build(history, 110m, Today, from: null, flows);

        Assert.Equal(10m, s.Gain);
        Assert.Equal(0.1m, s.TimeWeightedReturn);
    }

    [Theory]
    [InlineData(PortfolioValuePeriod.OneWeek, "2026-03-13")]
    [InlineData(PortfolioValuePeriod.OneMonth, "2026-02-20")]
    [InlineData(PortfolioValuePeriod.YearToDate, "2025-12-31")]
    [InlineData(PortfolioValuePeriod.OneYear, "2025-03-20")]
    [InlineData(PortfolioValuePeriod.ThreeYears, "2023-03-20")]
    public void Period_starts(PortfolioValuePeriod period, string expected) =>
        Assert.Equal(DateTime.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), PortfolioValueHistory.PeriodStart(period, Today));

    [Fact]
    public void A_day_compares_against_the_last_stored_day_before_today()
    {
        var history = History(
            (1, [(new DateTime(2026, 3, 10), 100m), (new DateTime(2026, 3, 16), 120m), (Today, 130m)]),
            (2, [(new DateTime(2026, 3, 12), 50m)]));

        var last = PortfolioValueHistory.LastStoredDayBefore(history, Today);

        Assert.Equal(new DateTime(2026, 3, 16), last);
        Assert.Equal(new DateTime(2026, 3, 16), PortfolioValueHistory.PeriodStart(PortfolioValuePeriod.OneDay, Today, last));
        Assert.Equal(new DateTime(2026, 3, 19), PortfolioValueHistory.PeriodStart(PortfolioValuePeriod.OneDay, Today, null));
        Assert.Null(PortfolioValueHistory.LastStoredDayBefore(History(), Today));
    }
}
