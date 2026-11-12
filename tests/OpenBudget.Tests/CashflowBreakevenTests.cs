using OpenBudget.Models;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests;

/// <summary>
/// "Pace to break even" income: the balance line of the In-cashflow accounts ends the year at
/// its starting balance.
/// </summary>
public class CashflowBreakevenTests
{
    private const int Year = 2026;

    private static DayOfMonthDistribution Spread(int day)
    {
        var w = new decimal[31];
        w[day - 1] = 1m;
        return new DayOfMonthDistribution { Weights = w };
    }

    private static CategoryCashflow Row(long id, string name, decimal[] monthly) => new()
    {
        CategoryId = id,
        ItemType = CategoryItemType.Category,
        CategoryName = name,
        MonthlyAmounts = monthly,
        BaseMonthlyAmounts = (decimal[])monthly.Clone(),
        IsAggregateRow = true,
    };

    private sealed record Setup(
        ProjectDailyDeltaResult Data, List<CategoryCashflow> Income, List<CategoryCashflow> Expenses,
        Dictionary<(long, CategoryItemType), DayOfMonthDistribution> Histograms, int FirstProjectedMonth);

    /// <summary>
    /// Start 2,000. The In-cashflow account sees 300 income and 400 expenses per actual month
    /// (on the 1st and 15th); projected months carry 400 expenses. Expense row actuals also
    /// include 1,000 per month booked on an account that is not in cashflow.
    /// </summary>
    private static Setup Build(int firstProjectedMonth, decimal startingBalance = 2000m, params string[] sources)
    {
        var delta = new decimal[365];
        int lastActual = new DateTime(Year, firstProjectedMonth, 1).DayOfYear - 1;
        for (int m = 1; m < firstProjectedMonth; m++)
        {
            delta[new DateTime(Year, m, 1).DayOfYear - 1] += 300m;
            delta[new DateTime(Year, m, 15).DayOfYear - 1] -= 400m;
        }
        var data = new ProjectDailyDeltaResult
        {
            HasAccounts = true,
            StartingBalance = startingBalance,
            LastActualDayOfYear = lastActual,
            DailyDelta = delta,
        };

        var expense = new decimal[12];
        for (int m = 0; m < 12; m++) expense[m] = m < firstProjectedMonth - 1 ? 1400m : 400m;
        var expenses = new List<CategoryCashflow> { Row(100, "Household", expense) };

        var income = new List<CategoryCashflow>();
        var hist = new Dictionary<(long, CategoryItemType), DayOfMonthDistribution>
        {
            [(100, CategoryItemType.Category)] = Spread(15),
        };
        long id = 1;
        foreach (var s in sources.Length == 0 ? ["Salary"] : sources)
        {
            var monthly = new decimal[12];
            for (int m = 0; m < firstProjectedMonth - 1; m++) monthly[m] = 300m / Math.Max(1, sources.Length);
            income.Add(Row(id, s, monthly));
            hist[(id, CategoryItemType.Category)] = Spread(1);
            id++;
        }
        return new Setup(data, income, expenses, hist, firstProjectedMonth);
    }

    private static (BreakevenIncome Be, decimal YearEnd) Run(Setup s, Dictionary<string, decimal> splits, List<WhatIfEntry>? whatIfs = null)
    {
        whatIfs ??= [];
        var be = CashflowCalculator.ComputeBreakevenIncome(s.Data, Year, Year, s.FirstProjectedMonth,
            s.Income, s.Expenses, s.Histograms, whatIfs, splits);
        for (int i = 0; i < s.Income.Count; i++) Array.Copy(be.RowMonthly[i], s.Income[i].MonthlyAmounts, 12);
        foreach (var w in whatIfs)
        {
            var key = w.ParseCategory();
            var row = s.Income.FirstOrDefault(c => key != null && c.CategoryId == key.Value.Id);
            row?.MonthlyAmounts[w.Date.Month - 1] += w.Amount;
        }
        var line = CashflowCalculator.ComputeBalance(s.Data, Year, Year, s.FirstProjectedMonth,
            s.Income, s.Expenses, s.Histograms, whatIfs, 0m);
        return (be, line.BalanceDaily[^1]);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(12)]
    public void Year_end_balance_equals_the_starting_balance(int firstProjectedMonth)
    {
        var s = Build(firstProjectedMonth);
        var (be, yearEnd) = Run(s, new(StringComparer.Ordinal) { ["Salary"] = 100m });

        Assert.Equal(2000m, Math.Round(yearEnd, 2));
        // Actual months lost 100 each; projected months need their 400 expenses.
        int actual = firstProjectedMonth - 1, projected = 13 - firstProjectedMonth;
        Assert.Equal(100m * actual + 400m * projected, be.Sources["Salary"]);
    }

    [Fact]
    public void Expenses_on_accounts_outside_cashflow_do_not_raise_the_target()
    {
        var s = Build(7);
        var withOutside = Run(s, new(StringComparer.Ordinal) { ["Salary"] = 100m }).Be.Sources["Salary"];

        var t = Build(7);
        for (int m = 0; m < 6; m++) t.Expenses[0].MonthlyAmounts[m] = 400m;
        var without = Run(t, new(StringComparer.Ordinal) { ["Salary"] = 100m }).Be.Sources["Salary"];

        Assert.Equal(without, withOutside);
    }

    [Fact]
    public void Several_sources_share_the_need_by_split()
    {
        var s = Build(7, 2000m, "Alex", "Sam");
        var (be, yearEnd) = Run(s, new(StringComparer.Ordinal) { ["Alex"] = 60m, ["Sam"] = 40m });

        Assert.Equal(3000m * 0.6m, be.Sources["Alex"]);
        Assert.Equal(3000m * 0.4m, be.Sources["Sam"]);
        Assert.Equal(2000m, Math.Round(yearEnd, 2));
    }

    [Fact]
    public void A_source_prepayment_counts_toward_its_own_share_only()
    {
        var s = Build(7, 2000m, "Alex", "Sam");
        var whatIfs = new List<WhatIfEntry>
        {
            new() { Date = new DateTime(Year, 8, 10), Amount = 500m, CategoryKey = "1:1" },
        };
        var (be, yearEnd) = Run(s, new(StringComparer.Ordinal) { ["Alex"] = 60m, ["Sam"] = 40m }, whatIfs);

        Assert.Equal(1800m - 500m, be.Sources["Alex"]);
        Assert.Equal(1200m, be.Sources["Sam"]);
        Assert.Equal(2000m, Math.Round(yearEnd, 2));
    }

    [Fact]
    public void Balance_above_start_needs_nothing()
    {
        var s = Build(12);
        s.Data.DailyDelta[10] += 10_000m;
        var (be, _) = Run(s, new(StringComparer.Ordinal) { ["Salary"] = 100m });

        Assert.True(be.Needed < 0m);
        Assert.Equal(0m, be.Sources["Salary"]);
        Assert.All(be.RowMonthly[0][11..], v => Assert.Equal(0m, v));
    }

    [Fact]
    public void Past_year_has_no_remaining_months()
    {
        var s = Build(7);
        var be = CashflowCalculator.ComputeBreakevenIncome(s.Data, Year, Year, 13,
            s.Income, s.Expenses, s.Histograms, [], new Dictionary<string, decimal>(StringComparer.Ordinal) { ["Salary"] = 100m });

        Assert.Equal(0, be.RemainingMonths);
        Assert.Equal(0m, be.Sources["Salary"]);
    }
}
