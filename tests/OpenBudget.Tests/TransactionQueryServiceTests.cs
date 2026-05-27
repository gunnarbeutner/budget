using OpenBudget.Models;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests;

/// <summary>
/// Integration tests for the per-project transaction browser. The shared invariant across
/// these is that what the browser returns must tie out to the dashboard's aggregations — same
/// split-parent exclusion, same leaf-rollup semantics — so a user clicking from "€450 spent"
/// on the dashboard sees exactly the rows that add up to €450.
/// </summary>
public class TransactionQueryServiceTests
{
    [Fact]
    public async Task Split_parent_rows_are_excluded()
    {
        await using var t = new TestDb();
        t.AddGroup(100);
        t.AddLeaf(101, parent: 100);
        // Splitted=1 is the placeholder parent; Splitted=2 are the real children.
        t.AddTransaction(id: 1, categoryId: 101, amountCents: -10000, date: new DateTime(2026, 5, 10), splitted: 1, name: "Parent");
        t.AddTransaction(id: 2, categoryId: 101, amountCents: -4000, date: new DateTime(2026, 5, 10), splitted: 2, name: "ChildA");
        t.AddTransaction(id: 3, categoryId: 101, amountCents: -6000, date: new DateTime(2026, 5, 10), splitted: 2, name: "ChildB");
        await t.Db.SaveChangesAsync();

        var page = await t.TransactionQueries.QueryAsync(new TransactionQuery
        {
            SourceId = 1,
            CategoryLeafIds = new[] { 101L },
        });

        Assert.Equal(2, page.TotalCount);
        Assert.All(page.Rows, r => Assert.NotEqual(1L, r.Id));
        Assert.True(page.Rows.All(r => r.IsSplitChild), "Split children should be flagged.");
        Assert.Equal(-100m, page.TotalSum); // €-40 + €-60
    }

    [Fact]
    public async Task Category_leaf_filter_matches_group_rollup_sum()
    {
        // The leaf-id set the browser uses comes from the same primitive
        // (BuildItemToLeafCategoryIds) the dashboard's aggregations use, so summing the rows
        // returned for a group's leaves must equal the dashboard's spent number for that group.
        await using var t = new TestDb();
        t.AddGroup(100);
        t.AddLeaf(101, parent: 100);
        t.AddLeaf(102, parent: 100);
        t.AddLeaf(999, parent: 100); // sibling leaf in same group, but no transactions
        t.AddProject(1);
        t.TrackGroup(1, 100);
        t.AddTransaction(id: 1, categoryId: 101, amountCents: -3000, date: new DateTime(2026, 3, 15));
        t.AddTransaction(id: 2, categoryId: 102, amountCents: -2500, date: new DateTime(2026, 4, 1));
        // A transaction outside the group — must not be picked up.
        t.AddGroup(200);
        t.AddLeaf(201, parent: 200);
        t.AddTransaction(id: 3, categoryId: 201, amountCents: -9000, date: new DateTime(2026, 4, 2));
        await t.Db.SaveChangesAsync();

        var hierarchy = await t.Hierarchy.GetAllCategoriesWithChildCountsAsync(1);
        var leafMap = OpenBudget.Services.CategoryHierarchyService.BuildItemToLeafCategoryIds(
            new[] { (100L, CategoryItemType.Group) }, hierarchy);
        var leafIds = leafMap[(100L, CategoryItemType.Group)].ToList();

        var page = await t.TransactionQueries.QueryAsync(new TransactionQuery
        {
            SourceId = 1,
            CategoryLeafIds = leafIds,
        });
        Assert.Equal(2, page.TotalCount);
        Assert.Equal(-55m, page.TotalSum);

        // And it agrees with the dashboard for the same scope.
        var summary = await t.Spending.GetSpendingSummaryAsync(1, 2026, 1, 12);
        Assert.Equal(55m, summary.TotalSpent);
        Assert.Equal(-page.TotalSum, summary.TotalSpent);
    }

    [Fact]
    public async Task Account_filter_restricts_to_account()
    {
        await using var t = new TestDb();
        t.AddGroup(100);
        t.AddLeaf(101, parent: 100);
        t.AddAccount(500, "Giro");
        t.AddAccount(501, "Savings");
        t.AddTransaction(id: 1, categoryId: 101, amountCents: -1000, date: new DateTime(2026, 5, 1), accountId: 500);
        t.AddTransaction(id: 2, categoryId: 101, amountCents: -2000, date: new DateTime(2026, 5, 2), accountId: 501);
        t.AddTransaction(id: 3, categoryId: 101, amountCents: -3000, date: new DateTime(2026, 5, 3), accountId: 500);
        await t.Db.SaveChangesAsync();

        var page = await t.TransactionQueries.QueryAsync(new TransactionQuery
        {
            SourceId = 1,
            AccountIds = new[] { 500L },
        });

        Assert.Equal(2, page.TotalCount);
        Assert.All(page.Rows, r => Assert.Equal(500L, r.AccountId));
        Assert.Equal("Giro", page.Rows[0].AccountName);
    }

    [Fact]
    public async Task Category_and_account_filters_combine_with_AND()
    {
        await using var t = new TestDb();
        t.AddGroup(100);
        t.AddLeaf(101, parent: 100);
        t.AddLeaf(102, parent: 100);
        t.AddAccount(500, "Giro");
        t.AddAccount(501, "Savings");
        t.AddTransaction(id: 1, categoryId: 101, amountCents: -1000, date: new DateTime(2026, 5, 1), accountId: 500);
        t.AddTransaction(id: 2, categoryId: 102, amountCents: -2000, date: new DateTime(2026, 5, 2), accountId: 500);
        t.AddTransaction(id: 3, categoryId: 101, amountCents: -3000, date: new DateTime(2026, 5, 3), accountId: 501);
        await t.Db.SaveChangesAsync();

        var page = await t.TransactionQueries.QueryAsync(new TransactionQuery
        {
            SourceId = 1,
            CategoryLeafIds = new[] { 101L },
            AccountIds = new[] { 500L },
        });

        Assert.Single(page.Rows);
        Assert.Equal(1L, page.Rows[0].Id);
    }

    [Fact]
    public async Task Period_filter_uses_half_open_interval()
    {
        await using var t = new TestDb();
        t.AddGroup(100);
        t.AddLeaf(101, parent: 100);
        t.AddTransaction(id: 1, categoryId: 101, amountCents: -1000, date: new DateTime(2026, 2, 28, 23, 59, 59));
        t.AddTransaction(id: 2, categoryId: 101, amountCents: -2000, date: new DateTime(2026, 3, 1, 0, 0, 0));
        t.AddTransaction(id: 3, categoryId: 101, amountCents: -3000, date: new DateTime(2026, 3, 31, 23, 59, 59));
        t.AddTransaction(id: 4, categoryId: 101, amountCents: -4000, date: new DateTime(2026, 4, 1, 0, 0, 0));
        await t.Db.SaveChangesAsync();

        var page = await t.TransactionQueries.QueryAsync(new TransactionQuery
        {
            SourceId = 1,
            Period = new TimeRangeFilter(2026, 3, 3),
        });
        Assert.Equal(new[] { 3L, 2L }, page.Rows.Select(r => r.Id).ToArray());
    }

    [Fact]
    public async Task Pagination_returns_disjoint_pages_with_correct_total()
    {
        await using var t = new TestDb();
        t.AddGroup(100);
        t.AddLeaf(101, parent: 100);
        for (int i = 0; i < 12; i++)
        {
            t.AddTransaction(id: i + 1, categoryId: 101, amountCents: -((i + 1) * 100),
                date: new DateTime(2026, 6, i + 1));
        }
        await t.Db.SaveChangesAsync();

        var page0 = await t.TransactionQueries.QueryAsync(new TransactionQuery
        {
            SourceId = 1,
            Skip = 0,
            Take = 5,
        });
        var page1 = await t.TransactionQueries.QueryAsync(new TransactionQuery
        {
            SourceId = 1,
            Skip = 5,
            Take = 5,
        });

        Assert.Equal(12, page0.TotalCount);
        Assert.Equal(12, page1.TotalCount);
        Assert.Equal(5, page0.Rows.Count);
        Assert.Equal(5, page1.Rows.Count);
        Assert.Empty(page0.Rows.Select(r => r.Id).Intersect(page1.Rows.Select(r => r.Id)));
        // Newest first: page 0 starts with id 12 (2026-06-12), page 1 starts with id 7.
        Assert.Equal(12L, page0.Rows[0].Id);
        Assert.Equal(7L, page1.Rows[0].Id);
        // TotalSum is the sum across the entire filtered set, independent of paging.
        Assert.Equal(page0.TotalSum, page1.TotalSum);
    }

    [Fact]
    public async Task Search_text_matches_name_or_comment_case_insensitively()
    {
        await using var t = new TestDb();
        t.AddGroup(100);
        t.AddLeaf(101, parent: 100);
        t.AddTransaction(id: 1, categoryId: 101, amountCents: -100, date: new DateTime(2026, 5, 1), name: "REWE Supermarkt");
        t.AddTransaction(id: 2, categoryId: 101, amountCents: -200, date: new DateTime(2026, 5, 2), name: "Aldi", comment: "wocheneinkauf rewe");
        t.AddTransaction(id: 3, categoryId: 101, amountCents: -300, date: new DateTime(2026, 5, 3), name: "Bäckerei");
        await t.Db.SaveChangesAsync();

        var page = await t.TransactionQueries.QueryAsync(new TransactionQuery
        {
            SourceId = 1,
            SearchText = "rewe",
        });
        Assert.Equal(2, page.TotalCount);
        Assert.Equal(new[] { 2L, 1L }, page.Rows.Select(r => r.Id).OrderByDescending(x => x).ToArray());
    }

    [Fact]
    public async Task Amount_filter_compares_on_absolute_value()
    {
        await using var t = new TestDb();
        t.AddGroup(100);
        t.AddLeaf(101, parent: 100);
        t.AddTransaction(id: 1, categoryId: 101, amountCents: -500, date: new DateTime(2026, 5, 1));    // €5
        t.AddTransaction(id: 2, categoryId: 101, amountCents: -5000, date: new DateTime(2026, 5, 2));   // €50
        t.AddTransaction(id: 3, categoryId: 101, amountCents: 12000, date: new DateTime(2026, 5, 3));   // €120 income
        t.AddTransaction(id: 4, categoryId: 101, amountCents: -25000, date: new DateTime(2026, 5, 4));  // €250
        await t.Db.SaveChangesAsync();

        var page = await t.TransactionQueries.QueryAsync(new TransactionQuery
        {
            SourceId = 1,
            MinAbsAmount = 50m,
            MaxAbsAmount = 200m,
        });

        // Expect rows with magnitude in [50, 200]: ids 2 (€50) and 3 (€120). 4 (€250) excluded; 1 (€5) excluded.
        Assert.Equal(new[] { 3L, 2L }, page.Rows.Select(r => r.Id).ToArray());
    }

    [Fact]
    public async Task Source_filter_excludes_other_sources()
    {
        await using var t = new TestDb();
        // Add a second source
        t.Db.ImportSources.Add(new ImportSource { Id = 2, Name = "second", CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) });
        t.AddGroup(100);
        t.AddLeaf(101, parent: 100);
        t.AddGroup(200, sourceId: 2);
        t.AddLeaf(201, parent: 200, sourceId: 2);
        t.AddTransaction(id: 1, categoryId: 101, amountCents: -100, date: new DateTime(2026, 5, 1), sourceId: 1);
        t.AddTransaction(id: 2, categoryId: 201, amountCents: -200, date: new DateTime(2026, 5, 2), sourceId: 2);
        await t.Db.SaveChangesAsync();

        var page = await t.TransactionQueries.QueryAsync(new TransactionQuery { SourceId = 1 });
        Assert.Single(page.Rows);
        Assert.Equal(1L, page.Rows[0].Id);
    }
}
