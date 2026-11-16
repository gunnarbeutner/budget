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

    [Fact]
    public void ProjectExpenses_keeps_the_current_months_remaining_budget_out_of_later_months()
    {
        // 100 a month; 80 spent in each of Jan–Oct (an underrun of 200), 30 so far in November (the current month).
        var budget = Enumerable.Repeat(100m, 12).ToArray();
        var spent = new decimal[12];
        for (int m = 0; m < 10; m++) spent[m] = 80m;
        spent[10] = 30m;

        var (expenses, remaining) = CashflowCalculator.ProjectExpenses(budget, spent, currentMonth: 11);

        Assert.Equal(70m, remaining);    // November still holds 70, for its own remaining days
        Assert.Equal(30m, expenses[10]); // November shows its actuals
        Assert.Equal(100m, expenses[11]); // December: its own budget; the 200 left unspent in Jan–Oct stays saved
        Assert.Equal(80m, expenses[0]);
    }

    [Fact]
    public void ProjectExpenses_lets_an_overrun_of_the_current_month_shrink_later_months()
    {
        var budget = Enumerable.Repeat(100m, 12).ToArray();
        var spent = new decimal[12];
        for (int m = 0; m < 10; m++) spent[m] = 100m;
        spent[10] = 150m;

        var (expenses, remaining) = CashflowCalculator.ProjectExpenses(budget, spent, currentMonth: 11);

        Assert.Equal(0m, remaining);
        Assert.Equal(50m, expenses[11]);
    }

    [Fact]
    public void ProjectExpenses_cuts_every_later_month_by_the_same_share_after_an_overrun()
    {
        // Budget 100 a month and 200 in December (1.300 a year); 1.050 spent through September (the current month)
        // leaves 250 for Oct–Dec, whose budgets add up to 400: each of them gets 62,5 % of its budget.
        var budget = Enumerable.Repeat(100m, 12).ToArray();
        budget[11] = 200m;
        var spent = new decimal[12];
        for (int m = 0; m < 8; m++) spent[m] = 100m;
        spent[8] = 250m;

        var (expenses, _) = CashflowCalculator.ProjectExpenses(budget, spent, currentMonth: 9);

        Assert.Equal(new[] { 62.5m, 62.5m, 125m }, expenses[9..]);
        Assert.Equal(1300m, expenses.Sum());
    }

    [Fact]
    public void ProjectExpenses_projects_nothing_once_the_yearly_budget_is_used_up()
    {
        var budget = Enumerable.Repeat(100m, 12).ToArray();
        var spent = new decimal[12];
        spent[0] = 1500m;

        var (expenses, _) = CashflowCalculator.ProjectExpenses(budget, spent, currentMonth: 2);

        Assert.All(expenses[2..], m => Assert.Equal(0m, m));
    }

    [Fact]
    public void ProjectExpenses_projects_every_month_of_a_future_year_and_none_of_a_past_one()
    {
        var budget = Enumerable.Repeat(100m, 12).ToArray();
        var spent = Enumerable.Repeat(40m, 12).ToArray();

        Assert.All(CashflowCalculator.ProjectExpenses(budget, new decimal[12], currentMonth: 0).Expenses, m => Assert.Equal(100m, m));
        var (past, remaining) = CashflowCalculator.ProjectExpenses(budget, spent, currentMonth: 13);
        Assert.Equal(spent, past);
        Assert.Equal(0m, remaining);
    }

    [Fact]
    public void ComputeBalance_spreads_the_current_months_remaining_budget_over_its_remaining_days_less_its_charges()
    {
        // Today is Nov 16 (day 320 of 2026): the row's November budget still holds 100, of which a recurring charge of 40
        // on Nov 20 is booked on its own; the other 60 go onto Nov 17–30 by the row's day pattern (the 1st is past, so
        // all of it lands on the 25th here).
        var data = new ProjectDailyDeltaResult
        {
            HasAccounts = true,
            StartingBalance = 500m,
            LastActualDayOfYear = 320,
            DailyDelta = new decimal[365],
        };
        var weights = new decimal[31];
        weights[0] = 0.5m;
        weights[24] = 0.5m;
        var expense = new CategoryCashflow
        {
            CategoryId = 7,
            ItemType = CategoryItemType.Category,
            IsAggregateRow = true,
            MonthlyAmounts = new decimal[12],
            CurrentMonthRemaining = 100m,
        };
        var histograms = new Dictionary<(long, CategoryItemType), DayOfMonthDistribution>
        {
            [(7, CategoryItemType.Category)] = new() { Weights = weights },
        };
        var scheduled = new ScheduledCashflow();
        scheduled.Occurrences.Add(new ScheduledOccurrence(new DateOnly(2026, 11, 20), -40m, "Subscription", OpenBudget.Connectors.ScheduledPaymentKind.Recurring));
        scheduled.CoveredByRow[(7, CategoryItemType.Category)] = MonthArray(month: 11, amount: -40m);

        var result = CashflowCalculator.ComputeBalance(
            data, selectedYear: 2026, currentYear: 2026, firstProjectedMonth: 12,
            new List<CategoryCashflow>(), new List<CategoryCashflow> { expense }, histograms, new List<WhatIfEntry>(), floor: 0m, scheduled);

        int nov20 = new DateTime(2026, 11, 20).DayOfYear - 1, nov25 = new DateTime(2026, 11, 25).DayOfYear - 1;
        Assert.Equal(500m, result.BalanceDaily[nov20 - 1]);
        Assert.Equal(460m, result.BalanceDaily[nov20]);
        Assert.Equal(400m, result.BalanceDaily[nov25]);
        Assert.Equal(400m, result.BalanceDaily[364]); // nothing of November reaches December
    }

    // 12-element monthly array with a single month (1-based) set.
    private static decimal[] MonthArray(int month, decimal amount)
    {
        var a = new decimal[12];
        a[month - 1] = amount;
        return a;
    }
}
