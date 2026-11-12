using OpenBudget.Models;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests;

/// <summary>
/// The project list, the project switcher and the Budget view's summary strip must show the same
/// "spent / budget" for a project and month. All three read <see cref="BudgetService.GetSpendingSummaryAsync"/>;
/// this pins that the period each one asks for resolves to the same window and that the shared sum
/// follows the Budget view's rules: only the project's tracked categories (whatever account the
/// booking is on), no transfers, split parts instead of their parent, income not counted as spending.
/// </summary>
public class ProjectBudgetSumConsistencyTests
{
    private const long Tracked = 101, TrackedIncome = 102, Untracked = 201;
    private const long SharedAccount = 11, ExclusiveAccount = 12;

    [Fact]
    public async Task Project_list_switcher_and_budget_view_agree_on_spent_and_budget()
    {
        await using var t = new TestDb();
        t.AddGroup(100);
        t.AddLeaf(Tracked, parent: 100);
        t.AddLeaf(TrackedIncome, parent: 100, type: 2);
        t.AddGroup(200);
        t.AddLeaf(Untracked, parent: 200);
        t.AddProject(1);
        t.TrackCategory(1, Tracked);
        t.TrackCategory(1, TrackedIncome);
        t.Db.CategoryTypeAssignments.Add(new CategoryTypeAssignment { ProjectId = 1, CategoryId = TrackedIncome, ItemType = CategoryItemType.Category, IsIncome = true });
        t.AddAccount(SharedAccount);
        t.AddAccount(ExclusiveAccount);
        t.Db.ProjectAccountAssignments.Add(new ProjectAccountAssignment { ProjectId = 1, AccountId = SharedAccount, Scope = AccountScope.Shared });
        t.Db.ProjectAccountAssignments.Add(new ProjectAccountAssignment { ProjectId = 1, AccountId = ExclusiveAccount, Scope = AccountScope.Exclusive });
        t.AddBudget(1, 2026, (Tracked, CategoryItemType.Category, 500m));

        var nov = new DateTime(2026, 11, 5);
        t.AddTransaction(1, Tracked, -10000, nov, accountId: ExclusiveAccount);                // counts: 100
        t.AddTransaction(2, Tracked, -2000, nov, accountId: SharedAccount);                    // counts: 20
        t.AddTransaction(3, Untracked, -900000, nov, accountId: SharedAccount);                // other project's category
        t.AddTransaction(4, Untracked, -50000, nov, accountId: ExclusiveAccount);              // not a tracked category
        t.AddTransaction(5, Tracked, -30000, nov, accountId: ExclusiveAccount, splitted: SplitState.Parent); // parent: replaced by parts
        t.AddTransaction(6, Tracked, -12000, nov, accountId: ExclusiveAccount, splitted: 2);   // part: 120
        t.AddTransaction(7, Untracked, -18000, nov, accountId: ExclusiveAccount, splitted: 2); // part elsewhere
        t.AddTransaction(8, Tracked, -700000, nov, accountId: SharedAccount);                  // transfer out
        t.AddTransaction(9, Tracked, 700000, nov, accountId: ExclusiveAccount);                // transfer in
        t.AddTransaction(10, TrackedIncome, 250000, nov, accountId: ExclusiveAccount);         // income
        t.AddTransaction(11, Tracked, -99900, new DateTime(2026, 10, 30), accountId: ExclusiveAccount); // other month
        await t.Db.SaveChangesAsync();
        foreach (var id in new long[] { 8, 9 })
        {
            var tx = await t.Db.Transactions.FindAsync(id);
            tx!.IsTransfer = true;
            tx.TransferPeerId = id == 8 ? 9 : 8;
        }
        await t.Db.SaveChangesAsync();

        // Switcher: the current month.
        var switcher = await t.Spending.GetSpendingSummaryAsync(1, 2026, 11, 11);
        // Project list: anchor + period.
        var (from, to) = Anchoring.Range(11, "month");
        var list = await t.Spending.GetSpendingSummaryAsync(1, 2026, from, to);
        // Budget view: its table over the same summary.
        var table = BudgetTableModel.Build(
            await t.Spending.GetSpendingSummaryAsync(1, 2026, 11, 11),
            await t.Hierarchy.GetAllCategoriesWithChildCountsAsync(1));

        Assert.Equal(240m, switcher.TotalSpent);
        Assert.Equal(500m, switcher.TotalBudget);
        Assert.Equal(2500m, switcher.MonthIncome);
        Assert.Equal((switcher.TotalSpent, switcher.TotalBudget), (list.TotalSpent, list.TotalBudget));
        Assert.Equal((switcher.TotalSpent, switcher.TotalBudget), (table.TotalSpent, table.TotalBudget));
        Assert.Equal(ProjectStatus.OverallPercent(switcher), ProjectStatus.OverallPercent(list));
    }
}
