using OpenBudget.Models;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests;

/// <summary>
/// Integration tests for the per-project browse tree. Hardest invariant to keep is that
/// per-node sums in the tree must match the dashboard's GetSpendingSummaryAsync at the same
/// scope — if they drift, a user clicking from "€55 spent" on the dashboard would see a
/// different number on the browser, which would destroy trust in the view.
/// </summary>
public class BrowseServiceTests
{
    [Fact]
    public async Task Tree_uses_source_sort_order_with_name_tiebreak()
    {
        // The source app stores a per-row sort column; the tree should respect it so the view
        // matches what the user sees inside the source app. Null sort values come last.
        await using var t = new TestDb();
        var lebensmittel = t.AddGroup(100, name: "Lebensmittel");
        lebensmittel.Sort = 10;
        var onlinedienste = t.AddGroup(200, name: "Onlinedienste");
        onlinedienste.Sort = 5; // smaller -> comes first despite the alphabetical inverse
        var sonstiges = t.AddGroup(300, name: "Sonstiges");
        sonstiges.Sort = null; // null sorts last
        t.AddLeaf(101, parent: 100, name: "FastFood");
        t.AddLeaf(201, parent: 200, name: "Claude");
        t.AddLeaf(301, parent: 300, name: "Misc");
        t.AddProject(1);
        t.TrackGroup(1, 100);
        t.TrackGroup(1, 200);
        t.TrackGroup(1, 300);
        await t.Db.SaveChangesAsync();

        var tree = await t.Browse.GetTreeAsync(1, 2026, 1, 12);

        Assert.NotNull(tree);
        Assert.Equal(3, tree!.Categories.Count);
        Assert.Equal(200L, tree.Categories[0].Id); // Onlinedienste, sort=5
        Assert.Equal(100L, tree.Categories[1].Id); // Lebensmittel, sort=10
        Assert.Equal(300L, tree.Categories[2].Id); // Sonstiges, sort=null -> last
    }

    [Fact]
    public async Task Children_under_a_group_use_source_sort_order()
    {
        await using var t = new TestDb();
        t.AddGroup(100, name: "Group");
        var amazon = t.AddLeaf(101, parent: 100, name: "Amazon");
        amazon.Sort = 20;
        var apple = t.AddLeaf(102, parent: 100, name: "Apple");
        apple.Sort = 10; // sorts before Amazon despite alphabetical inverse
        t.AddProject(1);
        t.TrackGroup(1, 100);
        await t.Db.SaveChangesAsync();

        var tree = await t.Browse.GetTreeAsync(1, 2026, 1, 12);

        var group = Assert.Single(tree!.Categories);
        Assert.Equal(2, group.Children.Count);
        Assert.Equal(102L, group.Children[0].Id); // Apple, sort=10
        Assert.Equal(101L, group.Children[1].Id); // Amazon, sort=20
    }

    [Fact]
    public async Task Tracked_group_includes_all_descendants_even_if_untracked()
    {
        // Screenshot reference: Onlinedienste is tracked → tree shows Amazon, Apple,
        // Bitwarden, etc. under it, each with its own spent number even though they're not
        // individually tracked.
        await using var t = new TestDb();
        t.AddGroup(100, name: "Onlinedienste");
        t.AddLeaf(101, parent: 100, name: "Amazon");
        t.AddLeaf(102, parent: 100, name: "Apple");
        t.AddProject(1);
        t.TrackGroup(1, 100);
        t.AddTransaction(id: 1, categoryId: 101, amountCents: -299, date: new DateTime(2026, 5, 1));
        t.AddTransaction(id: 2, categoryId: 102, amountCents: -698, date: new DateTime(2026, 5, 2));
        await t.Db.SaveChangesAsync();

        var tree = await t.Browse.GetTreeAsync(1, 2026, 5, 5);

        var onlinedienste = Assert.Single(tree!.Categories);
        Assert.Equal(2, onlinedienste.Children.Count);
        Assert.Contains(onlinedienste.Children, c => c.Name.Contains("Amazon") && c.Spent == 2.99m);
        Assert.Contains(onlinedienste.Children, c => c.Name.Contains("Apple") && c.Spent == 6.98m);
        Assert.Equal(2.99m + 6.98m, onlinedienste.Spent);
    }

    [Fact]
    public async Task Tracked_descendant_nests_under_tracked_parent_not_at_top()
    {
        await using var t = new TestDb();
        t.AddGroup(100, name: "Onlinedienste");
        t.AddLeaf(101, parent: 100, name: "Claude");
        t.AddLeaf(102, parent: 100, name: "Apple");
        t.AddProject(1);
        t.TrackGroup(1, 100);
        t.TrackCategory(1, 101);
        await t.Db.SaveChangesAsync();

        var tree = await t.Browse.GetTreeAsync(1, 2026, 1, 12);

        // Only one top-level entry; tracked leaf Claude lives under its tracked parent.
        Assert.Single(tree!.Categories);
        Assert.Equal(100L, tree.Categories[0].Id);
        Assert.Contains(tree.Categories[0].Children, c => c.Id == 101L && c.IsTracked);
    }

    [Fact]
    public async Task Tree_spent_matches_dashboard_spent_for_same_scope()
    {
        // The promise: clicking a category row on the dashboard and seeing the matching
        // browse-tree node must produce the same number.
        await using var t = new TestDb();
        t.AddGroup(100);
        t.AddLeaf(101, parent: 100);
        t.AddLeaf(102, parent: 100);
        t.AddProject(1);
        t.TrackGroup(1, 100);
        t.AddBudget(1, 2026, (100, CategoryItemType.Group, 100m));
        t.AddTransaction(id: 1, categoryId: 101, amountCents: -3000, date: new DateTime(2026, 3, 15));
        t.AddTransaction(id: 2, categoryId: 102, amountCents: -2500, date: new DateTime(2026, 4, 1));
        await t.Db.SaveChangesAsync();

        var summary = await t.Spending.GetSpendingSummaryAsync(1, 2026, 1, 12);
        var tree = await t.Browse.GetTreeAsync(1, 2026, 1, 12);

        Assert.Equal(55m, summary.TotalSpent);
        var groupNode = Assert.Single(tree!.Categories);
        Assert.Equal(55m, groupNode.Spent);
        Assert.Equal(1200m, groupNode.Budget); // 100 * 12
        Assert.Equal(1200m - 55m, groupNode.Remaining);
    }

    [Fact]
    public async Task Untracked_descendants_carry_spent_but_no_budget()
    {
        // Plex / Microsoft / Bitwarden style: subscription leaves under a tracked group with
        // no per-leaf budget — they show Spent (which may be 0) but Budget/Remaining stay null.
        await using var t = new TestDb();
        t.AddGroup(100);
        t.AddLeaf(101, parent: 100, name: "Plex");
        t.AddProject(1);
        t.TrackGroup(1, 100);
        t.AddBudget(1, 2026, (100, CategoryItemType.Group, 50m));
        await t.Db.SaveChangesAsync();

        var tree = await t.Browse.GetTreeAsync(1, 2026, 1, 12);

        var group = Assert.Single(tree!.Categories);
        var leaf = Assert.Single(group.Children);
        Assert.Equal(0m, leaf.Spent);
        Assert.Null(leaf.Budget);
        Assert.Null(leaf.Remaining);

        // The group itself, by contrast, has a real budget.
        Assert.Equal(600m, group.Budget);
    }

    [Fact]
    public async Task Income_items_keep_natural_positive_sign()
    {
        await using var t = new TestDb();
        t.AddGroup(100);
        t.AddLeaf(101, parent: 100);
        t.AddProject(1);
        t.TrackCategory(1, 101);
        t.Db.CategoryTypeAssignments.Add(new CategoryTypeAssignment
        {
            ProjectId = 1,
            CategoryId = 101,
            ItemType = CategoryItemType.Category,
            IsIncome = true,
        });
        // Income in the source app is stored as positive cents.
        t.AddTransaction(id: 1, categoryId: 101, amountCents: 250000, date: new DateTime(2026, 5, 1));
        await t.Db.SaveChangesAsync();

        var tree = await t.Browse.GetTreeAsync(1, 2026, 5, 5);

        var node = Assert.Single(tree!.Categories);
        Assert.True(node.IsIncome);
        Assert.Equal(2500m, node.Spent); // displayed as the natural positive income amount
    }

    [Fact]
    public async Task Multi_level_nested_groups_render_with_correct_rollup()
    {
        // G1 contains G2 contains a leaf — the dashboard-side test has the same shape; the
        // browser tree must render that path and aggregate correctly at every level.
        await using var t = new TestDb();
        t.AddGroup(100, name: "Outer");
        t.AddGroup(200, parent: 100, name: "Inner");
        t.AddLeaf(201, parent: 200, name: "Leaf");
        t.AddProject(1);
        t.TrackGroup(1, 100);
        t.AddTransaction(id: 1, categoryId: 201, amountCents: -5000, date: new DateTime(2026, 6, 1));
        await t.Db.SaveChangesAsync();

        var tree = await t.Browse.GetTreeAsync(1, 2026, 1, 12);

        var outer = Assert.Single(tree!.Categories);
        Assert.Equal(50m, outer.Spent);
        var inner = Assert.Single(outer.Children);
        Assert.Equal(50m, inner.Spent);
        var leaf = Assert.Single(inner.Children);
        Assert.Equal(50m, leaf.Spent);
    }

    [Fact]
    public async Task Linked_accounts_appear_at_top_with_balances()
    {
        await using var t = new TestDb();
        t.AddProject(1);
        var giro = t.AddAccount(500, "Giro");
        t.AddAccount(501, "Savings");
        // Manual balance string: the source's packed format.
        giro.Balances = "11:1234.56,3:EUR,";
        t.Db.ProjectAccountAssignments.Add(new ProjectAccountAssignment
        {
            ProjectId = 1,
            AccountId = 500,
            Scope = AccountScope.Shared
        });
        await t.Db.SaveChangesAsync();

        var tree = await t.Browse.GetTreeAsync(1, 2026, 1, 12);

        var account = Assert.Single(tree!.Accounts);
        Assert.Equal(500L, account.Id);
        Assert.Equal("Giro", account.Name);
        Assert.Equal(1234.56m, account.Balance);
    }

    [Fact]
    public async Task Unassigned_accounts_of_the_owner_with_tracked_bookings_are_offered_including_portfolio_ones()
    {
        await using var t = new TestDb();
        t.AddProject(1);
        t.AddGroup(100);
        t.AddLeaf(101, parent: 100);
        t.TrackGroup(1, 100);
        t.AssignGroup(1, 100);
        var giro = t.AddAccount(500, "Giro");
        var ledger = t.AddAccount(RowIds.Namespace(1, OpenBudget.Connectors.Sync.BankTransactionStore.ConnectorRowBase + 1), "Example Portfolio (Verrechnung)");
        ledger.Provider = AccountProviders.PortfolioPerformance;
        t.Db.ProjectAccountAssignments.Add(new ProjectAccountAssignment { ProjectId = 1, AccountId = 500, Scope = AccountScope.Shared });
        t.AddTransaction(1, 101, -1000, new DateTime(2026, 3, 1), accountId: giro.Id);
        t.AddTransaction(2, 101, -2500, new DateTime(2026, 3, 2), accountId: ledger.Id);
        await t.Db.SaveChangesAsync();

        var tree = await t.Browse.GetTreeAsync(1, 2026, 1, 12);

        var offered = Assert.Single(tree!.Accounts, a => !a.IsAssigned);
        Assert.Equal(ledger.Id, offered.Id);
        Assert.Contains(tree.Accounts, a => a.Id == 500 && a.IsAssigned);
    }

    [Fact]
    public async Task Returns_null_for_unknown_project()
    {
        await using var t = new TestDb();
        var tree = await t.Browse.GetTreeAsync(999, 2026, 1, 12);
        Assert.Null(tree);
    }

    [Fact]
    public async Task Assigned_groups_become_roots_when_present()
    {
        // The "Gemeinsam for Shared" model: a project has CategoryAssignments pointing at one or
        // more groups, and those groups anchor the tree. Tracking items live underneath.
        await using var t = new TestDb();
        t.AddGroup(100, name: "Gemeinsam");
        t.AddGroup(200, parent: 100, name: "Wohnen");
        t.AddLeaf(201, parent: 200, name: "Strom");
        t.AddProject(1);
        t.AssignGroup(1, 100);          // root = Gemeinsam
        t.TrackCategory(1, 201);        // tracked leaf deep in the hierarchy
        await t.Db.SaveChangesAsync();

        var tree = await t.Browse.GetTreeAsync(1, 2026, 1, 12);

        var root = Assert.Single(tree!.Categories);
        Assert.Equal(100L, root.Id); // Gemeinsam, the assigned group
        Assert.False(root.IsTracked);
        // Intermediate parent group Wohnen must be in the path between the assigned root and
        // the tracked sub-category — that's the whole point of the user's request.
        var wohnen = Assert.Single(root.Children);
        Assert.Equal(200L, wohnen.Id);
        var strom = Assert.Single(wohnen.Children);
        Assert.Equal(201L, strom.Id);
        Assert.True(strom.IsTracked);
    }

    [Fact]
    public async Task Assigned_roots_dont_pull_in_their_ancestors()
    {
        // "Up to but not including the groups assigned" — the assigned group is the top of
        // the visible path. Its parent group must NOT appear as a root or anywhere else.
        await using var t = new TestDb();
        t.AddGroup(50, name: "Ausgaben");        // ancestor of the assigned group, must NOT show
        t.AddGroup(100, parent: 50, name: "Gemeinsam"); // assigned
        t.AddLeaf(101, parent: 100, name: "Strom");
        t.AddProject(1);
        t.AssignGroup(1, 100);
        t.TrackCategory(1, 101);
        await t.Db.SaveChangesAsync();

        var tree = await t.Browse.GetTreeAsync(1, 2026, 1, 12);

        var root = Assert.Single(tree!.Categories);
        Assert.Equal(100L, root.Id);
        Assert.DoesNotContain(tree.Categories, c => c.Id == 50L);
    }

    [Fact]
    public async Task Orphan_tracking_items_outside_assigned_groups_still_show()
    {
        // Defensive: if a tracking item lives outside any assigned group (project setup is
        // mid-migration or otherwise inconsistent), it should still surface as an extra root
        // so the user can see + reach it rather than have it silently disappear.
        await using var t = new TestDb();
        t.AddGroup(100, name: "Gemeinsam");
        t.AddLeaf(101, parent: 100, name: "Strom");
        t.AddGroup(900, name: "Sonstiges");
        t.AddLeaf(901, parent: 900, name: "Orphan");
        t.AddProject(1);
        t.AssignGroup(1, 100);
        t.TrackCategory(1, 101);
        t.TrackCategory(1, 901);   // tracked but not under an assigned group
        await t.Db.SaveChangesAsync();

        var tree = await t.Browse.GetTreeAsync(1, 2026, 1, 12);

        Assert.Equal(2, tree!.Categories.Count);
        Assert.Contains(tree.Categories, c => c.Id == 100L);
        Assert.Contains(tree.Categories, c => c.Id == 901L && c.IsTracked);
    }
}
