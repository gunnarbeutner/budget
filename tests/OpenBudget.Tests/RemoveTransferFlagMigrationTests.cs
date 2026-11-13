using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests;

/// <summary>The migration that drops the transfer flag and the chart switch, on a database with categorised transactions.</summary>
public sealed class RemoveTransferFlagMigrationTests
{
    [Fact]
    public async Task Categorised_transactions_and_their_categories_survive_with_foreign_keys_on()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<BudgetDbContext>().UseSqlite(connection)
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)).Options;
        await using var db = new BudgetDbContext(options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261111233728_UserDisplayName");

        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON");
        await db.Database.ExecuteSqlRawAsync("UPDATE import_sources SET OwnerUserId = 'u-owner' WHERE Id = 1");
        await db.Database.ExecuteSqlRawAsync("INSERT INTO accounts (Id, SourceId, Iban, provider) VALUES (5, 1, NULL, 'fake')");
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO categories (Id, SourceId, name, show_in_charts) VALUES (10, 1, 'Food', 1), (11, 1, 'Transfers', 0)");
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO transactions (Id, SourceId, amount, timestamp, category_key, local_account_key, is_transfer, transfer_peer_id, split_parent_id) VALUES
            (1, 1, -1000, 0, 10, 5, 0, NULL, NULL),
            (2, 1, -500, 0, 10, 5, 0, NULL, 1),
            (3, 1, 2000, 0, 11, 5, 1, 4, NULL),
            (4, 1, -2000, 0, 11, 5, 1, 3, NULL),
            (5, 1, -300, 0, NULL, NULL, 0, NULL, NULL)
            """);

        await migrator.MigrateAsync();

        Assert.Equal(5, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM transactions").SingleAsync());
        Assert.Equal(2, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM categories").SingleAsync());
        Assert.Equal(2, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM transactions WHERE category_key = 11").SingleAsync());
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM pragma_foreign_key_check").SingleAsync());
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS Value FROM pragma_table_info('transactions') WHERE name IN ('is_transfer', 'transfer_peer_id')").SingleAsync());
        Assert.Equal(0, await db.Database.SqlQueryRaw<int>(
            "SELECT COUNT(*) AS Value FROM pragma_table_info('categories') WHERE name = 'show_in_charts'").SingleAsync());
    }
}
