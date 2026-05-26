using OpenBudget.Models;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests;

/// <summary>
/// Integration tests over the dashboard's main entry point. The seeded scenarios are the
/// ones that bit us recently — they pin the rules so a regression yells at red-build time
/// instead of red-Musterstraße time.
/// </summary>
public class GetSpendingSummaryAsyncTests
{
    /// <summary>
    /// The 34.5k bug: when a tracked parent has a tracked descendant AND both carry
    /// budget entries, TotalBudget must sum both rather than silently losing the parent's
    /// own amount via the descendant-rollup overwrite.
    /// </summary>
    [Fact]
    public async Task TotalBudget_sums_parent_and_tracked_descendant_stored_amounts()
    {
        await using var t = new TestDb();
        t.AddGroup(100);
        t.AddLeaf(101, parent: 100);
        t.AddProject(1);
        t.TrackGroup(1, 100);
        t.TrackCategory(1, 101);
        t.AddBudget(1, 2026,
            (100, CategoryItemType.Group, 10m),    // parent's own
            (101, CategoryItemType.Category, 50m)); // tracked descendant
        await t.Db.SaveChangesAsync();

        var summary = await t.Spending.GetSpendingSummaryAsync(1, 2026, 1, 12);

        // (10 + 50) * 12 months = 720
        Assert.Equal(720m, summary.TotalBudget);
    }

    /// <summary>
    /// The 70k bug: parent and tracked descendant rendered as separate rows, displaying
    /// the same money twice in the dashboard's category list.
    /// </summary>
    [Fact]
    public async Task CategorySpending_hides_tracked_parent_when_a_descendant_is_also_tracked()
    {
        await using var t = new TestDb();
        t.AddGroup(100);
        t.AddLeaf(101, parent: 100);
        t.AddProject(1);
        t.TrackGroup(1, 100);
        t.TrackCategory(1, 101);
        t.AddBudget(1, 2026, (101, CategoryItemType.Category, 50m));
        await t.Db.SaveChangesAsync();

        var summary = await t.Spending.GetSpendingSummaryAsync(1, 2026, 1, 12);

        var rows = summary.CategorySpending.Where(c => !c.IsIncome).ToList();
        Assert.Single(rows);
        Assert.Equal(101L, rows[0].CategoryId);
        Assert.Equal(CategoryItemType.Category, rows[0].ItemType);
    }

    [Fact]
    public async Task Transactions_aggregate_into_TotalSpent_via_leaf_union()
    {
        await using var t = new TestDb();
        t.AddGroup(100);
        t.AddLeaf(101, parent: 100);
        t.AddLeaf(102, parent: 100);
        t.AddProject(1);
        t.TrackGroup(1, 100);
        t.AddBudget(1, 2026, (100, CategoryItemType.Group, 100m));
        // -3000 cents = €30, -2500 cents = €25; one transaction per leaf
        t.AddTransaction(id: 1, categoryId: 101, amountCents: -3000, date: new DateTime(2026, 3, 15));
        t.AddTransaction(id: 2, categoryId: 102, amountCents: -2500, date: new DateTime(2026, 4, 1));
        await t.Db.SaveChangesAsync();

        var summary = await t.Spending.GetSpendingSummaryAsync(1, 2026, 1, 12);

        Assert.Equal(55m, summary.TotalSpent);
        Assert.Equal(55m, summary.MonthExpenses);
    }

    [Fact]
    public async Task Spent_does_not_double_count_when_parent_and_child_are_both_tracked()
    {
        // Parent's natural total is the leaf union (includes the child's transactions);
        // child's natural total is its own. Without leaf-union dedup, summing parent +
        // child would count the same transaction twice.
        await using var t = new TestDb();
        t.AddGroup(100);
        t.AddLeaf(101, parent: 100);
        t.AddProject(1);
        t.TrackGroup(1, 100);
        t.TrackCategory(1, 101);
        t.AddBudget(1, 2026);
        t.AddTransaction(id: 1, categoryId: 101, amountCents: -10000, date: new DateTime(2026, 6, 1));
        await t.Db.SaveChangesAsync();

        var summary = await t.Spending.GetSpendingSummaryAsync(1, 2026, 1, 12);

        Assert.Equal(100m, summary.TotalSpent);     // €100, not €200
        Assert.Equal(100m, summary.MonthExpenses);
    }

    [Fact]
    public async Task Income_tracked_items_do_not_pollute_expense_TotalBudget()
    {
        await using var t = new TestDb();
        t.AddGroup(100);
        t.AddLeaf(101, parent: 100);
        t.AddProject(1);
        t.TrackCategory(1, 101);
        // Mark the category as income for this project.
        t.Db.CategoryTypeAssignments.Add(new CategoryTypeAssignment
        {
            ProjectId = 1,
            CategoryId = 101,
            ItemType = CategoryItemType.Category,
            IsIncome = true,
        });
        t.AddBudget(1, 2026, (101, CategoryItemType.Category, 200m));
        await t.Db.SaveChangesAsync();

        var summary = await t.Spending.GetSpendingSummaryAsync(1, 2026, 1, 12);

        Assert.Equal(0m, summary.TotalBudget); // no expense budgets
    }

    [Fact]
    public async Task Month_period_sums_only_the_picked_month()
    {
        await using var t = new TestDb();
        t.AddGroup(100);
        t.AddLeaf(101, parent: 100);
        t.AddProject(1);
        t.TrackCategory(1, 101);
        t.AddBudget(1, 2026, (101, CategoryItemType.Category, 50m));
        await t.Db.SaveChangesAsync();

        var summary = await t.Spending.GetSpendingSummaryAsync(1, 2026, 3, 3);

        Assert.Equal(50m, summary.TotalBudget); // one month only
    }

    [Fact]
    public async Task Multi_level_nested_groups_aggregate_correctly()
    {
        // G1 contains G2 contains a leaf. Tracking the inner sub-group should still
        // correctly aggregate transactions on its descendant leaf.
        await using var t = new TestDb();
        t.AddGroup(100);
        t.AddGroup(200, parent: 100);
        t.AddLeaf(201, parent: 200);
        t.AddProject(1);
        t.TrackGroup(1, 200); // track the intermediate sub-group only
        t.AddBudget(1, 2026, (200, CategoryItemType.Group, 30m));
        t.AddTransaction(id: 1, categoryId: 201, amountCents: -5000, date: new DateTime(2026, 6, 1));
        await t.Db.SaveChangesAsync();

        var summary = await t.Spending.GetSpendingSummaryAsync(1, 2026, 1, 12);

        Assert.Equal(360m, summary.TotalBudget); // 30 * 12
        Assert.Equal(50m, summary.TotalSpent);   // €50 transaction
    }
}
