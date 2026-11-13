using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests;

/// <summary>The migration that turns the old field rules into equivalent search expressions.</summary>
public sealed class RuleExpressionsMigrationTests
{
    [Fact]
    public async Task Old_rules_become_equivalent_expressions_that_the_editor_can_take_apart()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<BudgetDbContext>().UseSqlite(connection)
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)).Options;
        await using var db = new BudgetDbContext(options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261110155619_AddRemovedImportItems");

        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF");
        await db.Database.ExecuteSqlRawAsync("UPDATE import_sources SET OwnerUserId = 'u-owner' WHERE Id = 1");
        await db.Database.ExecuteSqlRawAsync("INSERT INTO accounts (Id, SourceId, Iban, provider) VALUES (5, 1, 'DE00 1111 2222 3333 4444 00', 'fake'), (6, 1, NULL, 'fake')");
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO category_rules (Id, ProjectId, SortOrder, Enabled, PayeeText, PurposeText, UseRegex, AccountId, AmountSign, MinAmountCents, MaxAmountCents, CategoryId, CreatedAt) VALUES
            (1, 1, 0, 1, 'Example "Market"', NULL, 0, NULL, 0, NULL, NULL, 10, '2026-01-01'),
            (2, 1, 1, 1, '^sample/x', 'fee', 1, 5, 1, 500, 5000, 10, '2026-01-01'),
            (3, 1, 2, 1, NULL, 'rent', 0, 6, 2, 100000, NULL, 10, '2026-01-01'),
            (4, 1, 3, 1, 'bakery', NULL, 0, NULL, 0, 250, 1999, 10, '2026-01-01')
            """);
        await migrator.MigrateAsync();

        var rules = await db.CategoryRules.AsNoTracking().OrderBy(r => r.Id).Select(r => r.Expression).ToListAsync();
        Assert.Equal(
        [
            "name:\"Example Market\"",
            "name:/^sample\\/x/ AND (purpose:/fee/ OR booking_text:/fee/) AND local_account=\"DE00111122223333444400\" AND amount<0 AND amount<=-5.00 AND amount>=-50.00",
            "(purpose:\"rent\" OR booking_text:\"rent\") AND account_id=6 AND amount>0 AND amount>=1000.00",
            "name:\"bakery\" AND (amount>=2.50 AND amount<=19.99 OR amount<=-2.50 AND amount>=-19.99)",
        ], rules);

        RuleAccount[] accounts = [new(5, "DE00 1111 2222 3333 4444 00"), new(6, null)];
        Assert.All(rules, r => Assert.NotNull(RuleExpressionBuilder.TryDecompose(r, accounts)));
        var bakery = new RuleSubject(1, -1000, "Bakery", null, null);
        Assert.True(RuleExpression.Matches(rules[3], bakery));
        Assert.True(RuleExpression.Matches(rules[3], bakery with { Amount = 1000 }));
        Assert.False(RuleExpression.Matches(rules[3], bakery with { Amount = -2000 }));
        Assert.True(RuleExpression.Matches(rules[1], new RuleSubject(5, -1000, "Sample/x", "Fee 1", null, LocalAccount: "DE00111122223333444400")));
    }
}
