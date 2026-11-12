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
            s.Points);
        Assert.Equal(160m, s.Change);
        Assert.Equal(1.6m, s.ChangeFraction);
    }

    [Fact]
    public void A_depot_without_history_contributes_only_today()
    {
        // Depot 1 has history; a second depot without any stored point is in the current total only.
        var history = History((1, [(new DateTime(2026, 3, 18), 100m)]));

        var s = PortfolioValueHistory.Build(history, 100m + 500m, Today, from: null);

        Assert.Equal([(new DateTime(2026, 3, 18), 100m), (new DateTime(2026, 3, 19), 100m), (Today, 600m)], s.Points);
    }

    [Fact]
    public void No_history_gives_only_today()
    {
        var s = PortfolioValueHistory.Build(History(), 500m, Today, from: null);

        Assert.Equal([(Today, 500m)], s.Points);
        Assert.Equal(0m, s.Change);
    }

    [Fact]
    public void The_period_cuts_the_start_and_carries_the_earlier_value_in()
    {
        var history = History((1, [(new DateTime(2020, 1, 1), 80m), (new DateTime(2026, 3, 19), 120m)]));
        var from = PortfolioValueHistory.PeriodStart(PortfolioValuePeriod.OneYear, Today);

        var s = PortfolioValueHistory.Build(history, 130m, Today, from);

        Assert.Equal(new DateTime(2025, 3, 20), from);
        Assert.Equal((new DateTime(2025, 3, 20), 80m), s.Points[0]);
        Assert.Equal((new DateTime(2026, 3, 19), 120m), s.Points[^2]);
        Assert.Equal(366, s.Points.Count);
        Assert.Equal(50m, s.Change);
    }

    [Fact]
    public void A_period_reaching_before_the_history_starts_at_the_first_point()
    {
        var history = History((1, [(new DateTime(2026, 1, 1), 80m)]));

        var s = PortfolioValueHistory.Build(history, 80m, Today,
            PortfolioValueHistory.PeriodStart(PortfolioValuePeriod.FiveYears, Today));

        Assert.Equal(new DateTime(2026, 1, 1), s.Points[0].Day);
        Assert.Null(PortfolioValueHistory.PeriodStart(PortfolioValuePeriod.Max, Today));
    }
}
