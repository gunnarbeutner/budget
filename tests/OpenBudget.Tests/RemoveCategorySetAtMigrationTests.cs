using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests;

/// <summary>The migration that drops the category timestamp, on a database with categorised transactions.</summary>
public sealed class RemoveCategorySetAtMigrationTests
{
    [Fact]
    public async Task Categorised_transactions_survive_with_foreign_keys_on()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<BudgetDbContext>().UseSqlite(connection)
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)).Options;
        await using var db = new BudgetDbContext(options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261112001816_AddScheduledPayments");

        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON");
        await db.Database.ExecuteSqlRawAsync("UPDATE import_sources SET OwnerUserId = 'u-owner' WHERE Id = 1");
        await db.Database.ExecuteSqlRawAsync("INSERT INTO accounts (Id, SourceId, Iban, provider) VALUES (5, 1, NULL, 'fake')");
        await db.Database.ExecuteSqlRawAsync("INSERT INTO categories (Id, SourceId, name) VALUES (10, 1, 'Food'), (11, 1, 'Rent')");
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO transactions (Id, SourceId, amount, timestamp, category_key, local_account_key, split_parent_id, category_set_at) VALUES
            (1, 1, -1000, 0, 10, 5, NULL, 1700000000),
            (2, 1, -500, 0, 10, 5, 1, 1700000000),
            (3, 1, -2000, 0, 11, 5, NULL, NULL),
            (4, 1, -300, 0, NULL, NULL, NULL, NULL)
            """);

        await migrator.MigrateAsync();

        Assert.Equal(4, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM transactions").SingleAsync());
        Assert.Equal(2, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM categories").SingleAsync());
        Assert.Equal(2, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM transactions WHERE category_key = 10").SingleAsync());
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM pragma_foreign_key_check").SingleAsync());
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS Value FROM pragma_table_info('transactions') WHERE name = 'category_set_at'").SingleAsync());
    }
}
