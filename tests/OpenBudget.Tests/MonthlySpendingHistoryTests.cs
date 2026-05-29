using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests;

/// <summary>
/// BudgetPredictionService's monthly-history reads now run a single windowed query and bucket
/// in memory (formerly one query per month). These pin the bucketing and the group leaf-union.
/// The window ends in the current month, so transactions are placed relative to UtcNow to stay
/// deterministic regardless of when the suite runs.
/// </summary>
public class MonthlySpendingHistoryTests
{
    private static System.DateTime MonthMidpoint(int monthsAgo)
    {
        var now = System.DateTime.UtcNow;
        return new System.DateTime(now.Year, now.Month, 15, 0, 0, 0, System.DateTimeKind.Utc).AddMonths(-monthsAgo);
    }

    [Fact]
    public async Task Category_history_buckets_each_transaction_into_its_calendar_month()
    {
        await using var t = new TestDb();
        t.AddGroup(100);
        t.AddLeaf(101, parent: 100);
        t.AddProject(1);
        var thisMonth = MonthMidpoint(0);
        var threeAgo = MonthMidpoint(3);
        t.AddTransaction(id: 1, categoryId: 101, amountCents: -5000, date: thisMonth);
        t.AddTransaction(id: 2, categoryId: 101, amountCents: -2000, date: threeAgo);
        await t.Db.SaveChangesAsync();

        var result = await t.Predictions.GetMonthlySpendingForCategoryAsync(101, sourceId: 1, months: 12);

        Assert.Equal(12, result.Count);
        Assert.Equal(50m, result.Single(m => m.Year == thisMonth.Year && m.Month == thisMonth.Month).Amount);
        Assert.Equal(20m, result.Single(m => m.Year == threeAgo.Year && m.Month == threeAgo.Month).Amount);
        Assert.Equal(70m, result.Sum(m => m.Amount)); // nothing else leaked into other buckets
    }

    [Fact]
    public async Task Category_history_income_mode_keeps_only_positive_amounts()
    {
        await using var t = new TestDb();
        t.AddGroup(100);
        t.AddLeaf(101, parent: 100);
        t.AddProject(1);
        var thisMonth = MonthMidpoint(0);
        t.AddTransaction(id: 1, categoryId: 101, amountCents: 8000, date: thisMonth);  // income
        t.AddTransaction(id: 2, categoryId: 101, amountCents: -3000, date: thisMonth); // expense, ignored
        await t.Db.SaveChangesAsync();

        var result = await t.Predictions.GetMonthlySpendingForCategoryAsync(101, sourceId: 1, months: 12, isIncome: true);

        Assert.Equal(80m, result.Single(m => m.Year == thisMonth.Year && m.Month == thisMonth.Month).Amount);
        Assert.Equal(80m, result.Sum(m => m.Amount));
    }

    [Fact]
    public async Task Group_history_sums_over_descendant_leaves()
    {
        await using var t = new TestDb();
        t.AddGroup(100);
        t.AddGroup(200, parent: 100);
        t.AddLeaf(101, parent: 100);
        t.AddLeaf(201, parent: 200); // nested leaf — must be included
        t.AddProject(1);
        var thisMonth = MonthMidpoint(0);
        t.AddTransaction(id: 1, categoryId: 101, amountCents: -3000, date: thisMonth);
        t.AddTransaction(id: 2, categoryId: 201, amountCents: -2500, date: thisMonth);
        await t.Db.SaveChangesAsync();

        var result = await t.Predictions.GetMonthlySpendingAsync(100, sourceId: 1, months: 12);

        Assert.Equal(55m, result.Single(m => m.Year == thisMonth.Year && m.Month == thisMonth.Month).Amount);
    }

    [Fact]
    public async Task Group_with_no_leaves_returns_empty()
    {
        await using var t = new TestDb();
        t.AddGroup(100);
        t.AddProject(1);
        await t.Db.SaveChangesAsync();

        var result = await t.Predictions.GetMonthlySpendingAsync(100, sourceId: 1, months: 12);

        Assert.Empty(result);
    }
}
