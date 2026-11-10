using OpenBudget.Models;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests;

/// <summary>
/// The Budget view's table regroups the spending summary under the parent groups; its totals must be the
/// summary's (what the switcher, the sidebar badge and the Transactions strip show), and the group rows must add
/// up their categories.
/// </summary>
public class BudgetTableTests
{
    private static async Task<(ProjectSpendingSummary Summary, BudgetTableModel Table)> BuildAsync(TestDb t, int from, int to, int? budgetId = null)
    {
        var summary = await t.Spending.GetSpendingSummaryAsync(1, 2026, from, to, budgetId);
        var hierarchy = await t.Hierarchy.GetAllCategoriesWithChildCountsAsync(1);
        return (summary, BudgetTableModel.Build(summary, hierarchy));
    }

    private static TestDb Seed()
    {
        var t = new TestDb();
        t.AddGroup(10, name: "Living");
        t.AddLeaf(101, parent: 10, name: "Groceries");
        t.AddLeaf(102, parent: 10, name: "Restaurants");
        t.AddGroup(20, name: "Mobility");
        t.AddLeaf(201, parent: 20, name: "Fuel");
        t.AddLeaf(301, parent: 20, name: "Salary");
        t.AddProject(1);
        foreach (var c in new long[] { 101, 102, 201, 301 }) t.TrackCategory(1, c);
        t.Db.CategoryTypeAssignments.Add(new CategoryTypeAssignment { ProjectId = 1, CategoryId = 301, ItemType = CategoryItemType.Category, IsIncome = true });
        t.AddBudget(1, 2026,
            (101, CategoryItemType.Category, 450m),
            (102, CategoryItemType.Category, 100m),
            (201, CategoryItemType.Category, 160m),
            (301, CategoryItemType.Category, 3000m));
        t.AddTransaction(1, 101, -342_17, new DateTime(2026, 3, 3));
        t.AddTransaction(2, 102, -148_20, new DateTime(2026, 3, 9));
        t.AddTransaction(3, 201, -73_07, new DateTime(2026, 3, 12));
        t.AddTransaction(4, 201, -50_00, new DateTime(2026, 1, 12));
        t.AddTransaction(5, 301, 3000_00, new DateTime(2026, 3, 1));
        return t;
    }

    [Theory]
    [InlineData(3, 3)]
    [InlineData(1, 3)]
    [InlineData(1, 12)]
    public async Task Totals_are_the_summary_totals_and_groups_add_up_their_rows(int from, int to)
    {
        await using var t = Seed();
        await t.Db.SaveChangesAsync();

        var (summary, table) = await BuildAsync(t, from, to);

        Assert.Equal(summary.TotalBudget, table.TotalBudget);
        Assert.Equal(summary.TotalSpent, table.TotalSpent);
        Assert.Equal(table.TotalBudget, table.Sections.Sum(s => s.Budget));
        Assert.Equal(table.TotalSpent, table.Sections.Sum(s => s.Spent));
        foreach (var s in table.Sections)
        {
            Assert.Equal(s.Rows.Sum(r => r.Budget), s.Budget);
            Assert.Equal(s.Rows.Sum(r => r.Spent), s.Spent);
        }
    }

    [Fact]
    public async Task Rows_sit_under_their_parent_group_and_income_is_left_out()
    {
        await using var t = Seed();
        await t.Db.SaveChangesAsync();

        var (_, table) = await BuildAsync(t, 3, 3);

        Assert.Equal([10L, 20L], table.Sections.Select(s => s.GroupId!.Value));
        Assert.Equal([101L, 102L], table.Sections[0].Rows.Select(r => r.Id));
        Assert.Equal([201L], table.Sections[1].Rows.Select(r => r.Id));
        var restaurants = table.Sections[0].Rows[1];
        Assert.Equal(100m, restaurants.Budget);
        Assert.Equal(148.20m, restaurants.Spent);
        Assert.Equal(-48.20m, restaurants.Left);
        Assert.Equal("cat-102", restaurants.Selection);
        Assert.Equal(710m, table.TotalBudget);
        Assert.Equal(563.44m, table.TotalSpent);
    }

    [Fact]
    public async Task Budget_id_zero_compares_against_no_budget()
    {
        await using var t = Seed();
        await t.Db.SaveChangesAsync();

        var (_, table) = await BuildAsync(t, 3, 3, budgetId: 0);

        Assert.False(table.HasBudget);
        Assert.Equal(0m, table.TotalBudget);
        Assert.Equal(563.44m, table.TotalSpent);
    }
}
