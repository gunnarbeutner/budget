using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests.Receipts;

/// <summary>The migration that moves the per-source receipt categories and the item memory into projects.</summary>
public sealed class ProjectReceiptCategoriesMigrationTests
{
    [Fact]
    public async Task Source_categories_become_a_project_list_narrowed_per_source_and_memory_follows_its_category()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<BudgetDbContext>().UseSqlite(connection)
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)).Options;
        await using var db = new BudgetDbContext(options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261114134208_ReceiptSourceNoDefault");

        // Project 1 holds group 100 (with subgroup 101); project 2 holds group 200 and, directly, category 13.
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO projects (Id, Name, OwnerUserId, CreatedAt, SortOrder, IsDefaultForNewAccounts, DefaultClearedNotice) VALUES
            (1, 'Work', 'u-a', '2026-01-01', 0, 0, 0), (2, 'Home', 'u-a', '2026-01-01', 1, 0, 0);
            INSERT INTO category_groups (Id, OwnerUserId, name, group_key) VALUES (100, 'u-a', 'W', NULL), (101, 'u-a', 'W2', 100), (200, 'u-a', 'H', NULL);
            INSERT INTO categories (Id, OwnerUserId, name, group_key) VALUES (11, 'u-a', 'Household', 101), (12, 'u-a', 'Shop A', 100), (13, 'u-a', 'Shop B', 100), (21, 'u-a', 'Hobby', 200);
            INSERT INTO project_category_assignments (ProjectId, CategoryId) VALUES (1, 100), (2, 200), (2, 13);
            INSERT INTO receipt_source_categories (OwnerUserId, Source, CategoryId, Description, Keywords) VALUES
            ('u-a', 'mail', 11, 'Cleaning', NULL), ('u-a', 'mail', 12, 'Shop A things', 'foo'),
            ('u-a', 'lidl', 11, 'Cleaning', NULL), ('u-a', 'lidl', 13, NULL, NULL);
            INSERT INTO item_category_memory (OwnerUserId, NormalizedName, CategoryId, UpdatedAt) VALUES
            ('u-a', 'soap', 11, '2026-01-01'), ('u-a', 'brush', 13, '2026-01-01'), ('u-a', 'gone', 99, '2026-01-01');
            """);

        await migrator.MigrateAsync();

        Assert.Equal(["1:11:Cleaning", "1:12:Shop A things", "1:13:", "2:13:"], await db.Database.SqlQueryRaw<string>(
            "SELECT ProjectId || ':' || CategoryId || ':' || COALESCE(Description, '') AS Value FROM project_receipt_categories ORDER BY ProjectId, CategoryId").ToListAsync());
        Assert.Equal(["1:amazon:", "1:lidl:11,13", "1:mail:11,12", "2:amazon:", "2:lidl:13", "2:mail:"], await db.Database.SqlQueryRaw<string>(
            "SELECT ProjectId || ':' || Source || ':' || CategoryIds AS Value FROM project_receipt_sources ORDER BY ProjectId, Source").ToListAsync());
        Assert.Equal(["brush:1", "brush:2", "soap:1"], await db.Database.SqlQueryRaw<string>(
            "SELECT NormalizedName || ':' || ProjectId AS Value FROM item_category_memory ORDER BY NormalizedName, ProjectId").ToListAsync());
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM sqlite_master WHERE name = 'receipt_source_categories'").SingleAsync());
    }
}
