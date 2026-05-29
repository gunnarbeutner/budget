using OpenBudget.Models;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests;

/// <summary>
/// Characterization tests for the cashflow balance math extracted from Cashflow.razor:
/// day-of-month weight distribution, cumulative balance, watermarks, and the year/future minima
/// + floor-crossing detection.
/// </summary>
public class CashflowCalculatorTests
{
    private static DayOfMonthDistribution Day1() // all monthly mass on the 1st
    {
        var w = new decimal[31];
        w[0] = 1m;
        return new DayOfMonthDistribution { Weights = w };
    }

    [Fact]
    public void DistributeAcrossMonth_folds_overflow_weight_into_the_last_day()
    {
        var deltas = new decimal[60];
        var weights = new decimal[31];
        weights[0] = 0.2m; weights[1] = 0.3m; weights[2] = 0.1m; weights[30] = 0.4m; // 0.4 on "31st"

        CashflowCalculator.DistributeAcrossMonth(deltas, startDoy: 1, daysInMonth: 3, amount: 100m, weights, sign: +1);

        Assert.Equal(20m, deltas[0]);
        Assert.Equal(30m, deltas[1]);
        Assert.Equal(50m, deltas[2]); // 0.1 + folded 0.4 = 0.5 × 100
        Assert.Equal(100m, deltas[0] + deltas[1] + deltas[2]); // mass conserved
    }

    [Fact]
    public void ComputeBalance_with_no_accounts_returns_empty()
    {
        var result = CashflowCalculator.ComputeBalance(
            new ProjectDailyDeltaResult { HasAccounts = false }, 2026, 2026, 13,
            new List<CategoryCashflow>(), new List<CategoryCashflow>(), null, new List<WhatIfEntry>(), 0m);

        Assert.False(result.HasAccounts);
        Assert.Empty(result.BalanceDaily);
        Assert.Empty(result.Watermarks);
    }

    [Fact]
    public void ComputeBalance_cumulates_daily_deltas_from_the_starting_balance()
    {
        var delta = new decimal[365];
        delta[0] = -200m;   // Jan 1
        delta[100] = 50m;   // mid-April
        var data = new ProjectDailyDeltaResult
        {
            HasAccounts = true,
            StartingBalance = 1000m,
            LastActualDayOfYear = 365,
            DailyDelta = delta,
        };

        var result = CashflowCalculator.ComputeBalance(
            data, selectedYear: 2026, currentYear: 2026, firstProjectedMonth: 13,
            new List<CategoryCashflow>(), new List<CategoryCashflow>(), null, new List<WhatIfEntry>(), floor: 0m);

        Assert.Equal(800m, result.BalanceDaily[0]);
        Assert.Equal(800m, result.BalanceDaily[99]);
        Assert.Equal(850m, result.BalanceDaily[100]);
        Assert.Equal(850m, result.BalanceDaily[364]);
        Assert.Equal(800m, result.YearMinBalance);
        Assert.Equal(1, result.YearMinDayOfYear);
        Assert.Equal(850m, result.CurrentBalanceForYear); // current year → at last actual day
        Assert.False(result.CurrentlyNegative);
        Assert.Equal(0, result.FirstFutureNegativeDayOfYear);
        Assert.Equal(12, result.Watermarks.Length);
    }

    [Fact]
    public void ComputeBalance_flags_the_first_future_dip_below_the_floor()
    {
        var delta = new decimal[365];
        delta[200] = -500m; // a future dip (after today = day 10)
        var data = new ProjectDailyDeltaResult
        {
            HasAccounts = true,
            StartingBalance = 100m,
            LastActualDayOfYear = 10,
            DailyDelta = delta,
        };

        var result = CashflowCalculator.ComputeBalance(
            data, selectedYear: 2026, currentYear: 2026, firstProjectedMonth: 13,
            new List<CategoryCashflow>(), new List<CategoryCashflow>(), null, new List<WhatIfEntry>(), floor: 0m);

        Assert.Equal(100m, result.CurrentBalanceForYear);
        Assert.False(result.CurrentlyNegative);
        Assert.Equal(201, result.FirstFutureNegativeDayOfYear); // index 200 → day 201
        Assert.Equal(-400m, result.FutureMinBalance);
        Assert.Equal(201, result.FutureMinDayOfYear);
    }

    [Fact]
    public void ComputeBalance_spreads_projected_income_via_the_histogram()
    {
        var data = new ProjectDailyDeltaResult
        {
            HasAccounts = true,
            StartingBalance = 0m,
            LastActualDayOfYear = 31, // January actuals only
            DailyDelta = new decimal[365],
        };
        var income = new List<CategoryCashflow>
        {
            new() { CategoryId = 1, ItemType = CategoryItemType.Group, IsAggregateRow = true,
                    MonthlyAmounts = MonthArray(month: 2, amount: 280m) }, // €280 in February
        };
        var histograms = new Dictionary<(long, CategoryItemType), DayOfMonthDistribution>
        {
            [(1, CategoryItemType.Group)] = Day1(),
        };

        var result = CashflowCalculator.ComputeBalance(
            data, selectedYear: 2026, currentYear: 2026, firstProjectedMonth: 2,
            income, new List<CategoryCashflow>(), histograms, new List<WhatIfEntry>(), floor: 0m);

        Assert.Equal(0m, result.BalanceDaily[30]);   // Jan 31 (index 30): nothing yet
        Assert.Equal(280m, result.BalanceDaily[31]); // Feb 1 (index 31): payday lands here
        Assert.Equal(280m, result.BalanceDaily[364]); // carries to year end
    }

    // 12-element monthly array with a single month (1-based) set.
    private static decimal[] MonthArray(int month, decimal amount)
    {
        var a = new decimal[12];
        a[month - 1] = amount;
        return a;
    }
}
