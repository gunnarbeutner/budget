using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OpenBudget.Models;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests;

/// <summary>
/// The transfer flag of categories and groups: inheritance, the migration that flags the imported transfer group, its
/// effect on subscription detection, and the picker offering transfers in every project. All data is made up.
/// </summary>
public sealed class TransferCategoryTests
{
    [Fact]
    public void A_category_is_a_transfer_when_it_or_an_ancestor_group_is_flagged()
    {
        var groups = new (long, long?, bool)[] { (1, null, false), (2, 1, true), (3, 2, false), (4, 1, false), (5, 6, false), (6, 5, false) };
        var categories = new (long, long?, bool)[] { (10, 3, false), (11, 2, false), (12, 4, false), (13, 4, true), (14, 5, false), (15, null, false) };

        var ids = CategoryHierarchyService.TransferCategoryIds(groups, categories);

        Assert.Equal([10, 11, 13], ids.Order());
    }

    [Fact]
    public async Task The_helper_reads_the_flags_of_one_owner()
    {
        await using var t = new TestDb();
        t.AddGroup(100).IsTransfer = true;
        t.AddLeaf(101, 100);
        t.AddGroup(200);
        t.AddLeaf(201, 200).IsTransfer = true;
        t.AddLeaf(202, 200);
        await t.Db.SaveChangesAsync();

        Assert.Equal([101, 201], (await t.Hierarchy.GetTransferCategoryIdsAsync(TestDb.DefaultOwner)).Order());
        Assert.Empty(await t.Hierarchy.GetTransferCategoryIdsAsync("u-other"));
    }

    [Fact]
    public async Task Subscription_detection_skips_bookings_in_transfer_categories()
    {
        await using var t = new TestDb();
        t.AddGroup(100, name: "Moves");
        t.AddLeaf(101, 100, "Savings");
        t.AddGroup(200);
        t.AddLeaf(201, 200, "Media");
        t.AddProject(1);
        t.AddAccount(10, "Giro");
        t.Db.ProjectAccountAssignments.Add(new ProjectAccountAssignment { ProjectId = 1, AccountId = 10, Scope = AccountScope.Exclusive });
        var today = new DateOnly(2026, 11, 15);
        var id = 0L;
        for (var m = 0; m < 6; m++)
        {
            var date = new DateOnly(2026, 11, 1).AddMonths(-m);
            foreach (var (cat, name, cents) in new[] { (101L, "Example Savings Plan", -20000L), (201L, "Streamio Media", -1299L) })
                t.Db.Transactions.Add(new Transaction
                {
                    Id = ++id,
                    OwnerUserId = TestDb.DefaultOwner,
                    AccountId = 10,
                    CategoryId = cat,
                    Amount = cents,
                    Name = name,
                    Booked = true,
                    Timestamp = new DateTimeOffset(date.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero).ToUnixTimeSeconds(),
                });
        }
        await t.Db.SaveChangesAsync();

        var before = await new RecurringPaymentService(t.Factory, t.Hierarchy, t.Projects).GetAsync(1, today);
        Assert.Equal(2, before.Count);

        await t.Db.CategoryGroups.Where(g => g.Id == 100).ExecuteUpdateAsync(u => u.SetProperty(g => g.IsTransfer, true));
        var after = await new RecurringPaymentService(t.Factory, t.Hierarchy, t.Projects).GetAsync(1, today);
        Assert.Equal(["Streamio Media"], after.Select(e => e.Series.Payee));
    }

    [Fact]
    public void A_project_picker_offers_the_transfer_categories_in_their_own_section()
    {
        var groups = new List<CategoryGroup>
        {
            new() { Id = 1, Name = null },
            new() { Id = 2, Name = "Household", ParentGroupId = 1, Sort = 1 },
            new() { Id = 3, Name = "Moves", ParentGroupId = 1, Sort = 2 },
        };
        var categories = new List<Category>
        {
            new() { Id = 20, Name = "Food", GroupId = 2 },
            new() { Id = 21, Name = "Car", GroupId = 2 },
            new() { Id = 30, Name = "Own transfer", GroupId = 3 },
        };
        var all = CategoryPickerLogic.Build(groups, categories, 1);

        var data = CategoryPickerLogic.ForProject(all, new HashSet<long> { 20 }, [("Other", new HashSet<long> { 21 })],
            new HashSet<long> { 30 }, "Transfers");

        Assert.Equal([20, 30], data.Options.Select(o => o.Id));
        Assert.Null(data.OutsideOf(30));
        Assert.NotNull(data.OutsideOf(21));
        var section = data.Tree.SkipWhile(r => r.Id != CategoryPickerLogic.TransferSectionId).ToList();
        Assert.Equal([(true, 0, "Transfers"), (false, 1, "Moves › Own transfer")], section.Select(r => (r.IsGroup, r.Depth, r.Name)));
    }

    [Fact]
    public void A_project_tracking_the_transfer_category_keeps_it_in_place()
    {
        var all = CategoryPickerLogic.Build([new CategoryGroup { Id = 2, Name = "Moves" }], [new Category { Id = 30, Name = "Own transfer", GroupId = 2 }], 0);

        var data = CategoryPickerLogic.ForProject(all, new HashSet<long> { 30 }, [], new HashSet<long> { 30 }, "Transfers");

        Assert.DoesNotContain(data.Tree, r => r.Id == CategoryPickerLogic.TransferSectionId);
        Assert.Equal([30], data.Options.Select(o => o.Id));
    }

    [Fact]
    public async Task The_migration_flags_the_imported_transfer_group_and_its_categories()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<BudgetDbContext>().UseSqlite(connection)
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)).Options;
        await using var db = new BudgetDbContext(options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261115082937_RecurringDismissals");

        const long Space2 = 2L << 40;
        await db.Database.ExecuteSqlRawAsync("INSERT INTO user_data_states (UserId, IdSpace) VALUES ('u-a', 1), ('u-b', 2)");
        await db.Database.ExecuteSqlRawAsync($"""
            INSERT INTO category_groups (Id, OwnerUserId, name, group_key) VALUES
            (1, 'u-a', NULL, NULL),
            (5, 'u-a', 'Umbuchungen', 1),
            (6, 'u-a', 'Household', 1),
            (7, 'u-a', 'Umbuchungen', 6),
            (8, 'u-a', 'Moves', 5),
            ({Space2 | 1}, 'u-b', NULL, NULL),
            ({Space2 | 5}, 'u-b', 'Umbuchungen', {Space2 | 1}),
            ({Space2 | 6}, 'u-b', 'Umbuchungen', 1)
            """);
        await db.Database.ExecuteSqlRawAsync($"""
            INSERT INTO categories (Id, OwnerUserId, name, group_key) VALUES
            (50, 'u-a', 'Own transfer', 5),
            (51, 'u-a', 'Food', 6),
            (52, 'u-a', 'Nested', 7),
            (53, 'u-a', 'Deeper', 8),
            ({Space2 | 50}, 'u-b', 'Own transfer', {Space2 | 5}),
            ({Space2 | 51}, 'u-b', 'Not under the root', {Space2 | 6})
            """);

        await migrator.MigrateAsync();

        Assert.Equal([5, Space2 | 5], await db.Database.SqlQueryRaw<long>(
            "SELECT Id AS Value FROM category_groups WHERE is_transfer = 1 ORDER BY Id").ToListAsync());
        // A deeper category inherits the flag at runtime; only the group's own categories are flagged.
        Assert.Equal([50, Space2 | 50], await db.Database.SqlQueryRaw<long>(
            "SELECT Id AS Value FROM categories WHERE is_transfer = 1 ORDER BY Id").ToListAsync());
    }
}
