#pragma warning disable EF1002 // test-only SQL built from constants of this file
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests;

/// <summary>
/// The migration that gives every source's rows to the source's owner, on a database shaped like a real one: two
/// owners with data (one of them also owning an empty second source), plus an empty source nobody owns.
/// </summary>
public sealed class RemoveSourcesMigrationTests
{
    private const string Before = "20261112155916_StatementAliases";
    private const string First = "u-first";
    private const string Second = "u-second";

    private static long Id(int source, long row) => RowIds.Namespace(source, row);

    private static async Task<(SqliteConnection Connection, BudgetDbContext Db, IMigrator Migrator)> OpenAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<BudgetDbContext>().UseSqlite(connection)
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)).Options;
        var db = new BudgetDbContext(options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(Before);
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON");
        return (connection, db, migrator);
    }

    private static Task<long> ScalarAsync(BudgetDbContext db, string sql) =>
        db.Database.SqlQueryRaw<long>(sql).SingleAsync();

    private static async Task SeedAsync(BudgetDbContext db)
    {
        // Source 1 is the seeded one; the first owner has it plus an empty leftover (3), the second owner source 2,
        // and source 4 belongs to nobody and holds nothing.
        await db.Database.ExecuteSqlRawAsync($"""
            UPDATE import_sources SET OwnerUserId = '{First}', LastUploadAt = '2026-11-01 08:00:00', StructureTakenOverAt = '2026-11-02 08:00:00' WHERE Id = 1;
            INSERT INTO import_sources (Id, Name, CreatedAt, OwnerUserId, MoneyMoneyImportFinishedAt) VALUES
                (2, 'second', '2026-01-01', '{Second}', '2026-11-03 08:00:00'),
                (3, 'first-pp', '2026-01-01', '{First}', NULL),
                (4, 'leftover', '2026-01-01', NULL, NULL);
            """);
        foreach (var source in new[] { 1, 2 })
        {
            await db.Database.ExecuteSqlRawAsync($"""
                INSERT INTO account_groups (Id, SourceId, name) VALUES ({Id(source, 1)}, {source}, 'Banks');
                INSERT INTO accounts (Id, SourceId, name, group_key, provider) VALUES ({Id(source, 1)}, {source}, 'Example Giro', {Id(source, 1)}, 'moneymoney'),
                                                                                      ({Id(source, 2)}, {source}, 'Example Depot', NULL, 'moneymoney');
                INSERT INTO category_groups (Id, SourceId, name, group_key) VALUES ({Id(source, 1)}, {source}, '', NULL), ({Id(source, 2)}, {source}, 'Household', {Id(source, 1)});
                INSERT INTO categories (Id, SourceId, name, group_key) VALUES ({Id(source, 1)}, {source}, '', {Id(source, 1)}), ({Id(source, 2)}, {source}, 'Groceries', {Id(source, 2)});
                INSERT INTO transactions (Id, SourceId, amount, timestamp, category_key, local_account_key) VALUES
                    ({Id(source, 1)}, {source}, -1250, 1767312000, {Id(source, 2)}, {Id(source, 1)}),
                    ({Id(source, 2)}, {source}, 200000, 1767398400, {Id(source, 1)}, {Id(source, 1)});
                INSERT INTO holdings (Id, SourceId, account_key, quantity) VALUES ({Id(source, 1)}, {source}, {Id(source, 2)}, 3);
                INSERT INTO portfolio_chart (Id, SourceId, account_key, timestamp, balance) VALUES ({Id(source, 1)}, {source}, {Id(source, 2)}, 1767312000, 100);
                INSERT INTO pp_portfolio (SourceId, Uuid, Name) VALUES ({source}, 'pf-{source}', 'Example Portfolio');
                INSERT INTO pp_portfolio_settings (SourceId, PpPortfolioUuid, TakenOver, TakeoverDeclined) VALUES ({source}, 'pf-{source}', 0, 0);
                INSERT INTO removed_import_items (SourceId, Kind, ItemId, RemovedAt) VALUES ({source}, 'account', {Id(source, 9)}, '2026-01-01');
                INSERT INTO scheduled_payments (SourceId, AccountId, Kind, NextDate, Amount, FetchedAt) VALUES ({source}, {Id(source, 1)}, 0, '2026-12-01', -85000, '2026-11-01');
                INSERT INTO bank_statements (AccountId, CreatedAt, Date, FilePath, Identifier, Name, OwnerUserId, Sha256, SizeBytes, SourceId)
                    VALUES ({Id(source, 1)}, '2026-01-01', '2026-01-31', 'f{source}.pdf', 'mm:{source}', 'Statement', '{(source == 1 ? First : Second)}', 'h{source}', 1, {source});
                INSERT INTO projects (Id, CreatedAt, Name, SortOrder, SourceId, OwnerUserId) VALUES ({source}, '2026-01-01', 'Project {source}', {source}, {source}, '{(source == 1 ? First : Second)}');
                INSERT INTO project_account_assignments (ProjectId, AccountId, Scope, CreatedAt, InCashflow) VALUES ({source}, {Id(source, 1)}, 0, '2026-01-01', 1);
                INSERT INTO budgets (ProjectId, Year, Name, IsActive, CreatedAt, Version) VALUES ({source}, 2026, '2026', 1, '2026-01-01', 0);
                """);
        }
    }

    private static readonly string[] Tables =
    [
        "accounts", "account_groups", "categories", "category_groups", "transactions", "holdings", "portfolio_chart",
        "pp_portfolio", "pp_portfolio_settings", "removed_import_items", "scheduled_payments", "bank_statements", "projects",
        "project_account_assignments", "budgets",
    ];

    [Fact]
    public async Task Rows_keep_their_ids_and_belong_to_the_former_source_owner()
    {
        var (connection, db, migrator) = await OpenAsync();
        await using var _ = connection;
        await using var __ = db;
        await SeedAsync(db);
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var t in Tables) counts[t] = await ScalarAsync(db, $"SELECT COUNT(*) AS Value FROM \"{t}\"");

        await migrator.MigrateAsync();

        foreach (var t in Tables) Assert.Equal(counts[t], await ScalarAsync(db, $"SELECT COUNT(*) AS Value FROM \"{t}\""));
        foreach (var t in Tables.Where(t => t is not ("project_account_assignments" or "budgets")))
        {
            Assert.Equal(0, await ScalarAsync(db, $"SELECT COUNT(*) AS Value FROM pragma_table_info('{t}') WHERE name = 'SourceId'"));
            Assert.Equal(0, await ScalarAsync(db, $"SELECT COUNT(*) AS Value FROM \"{t}\" WHERE OwnerUserId IS NULL OR OwnerUserId = ''"));
        }
        foreach (var (source, owner) in new[] { (1, First), (2, Second) })
        {
            foreach (var t in new[] { "accounts", "account_groups", "categories", "category_groups", "transactions", "holdings", "portfolio_chart" })
                Assert.Equal(new[] { Id(source, 1) }.Concat(t is "accounts" or "categories" or "category_groups" or "transactions" ? [Id(source, 2)] : []),
                    await db.Database.SqlQueryRaw<long>($"SELECT Id AS Value FROM \"{t}\" WHERE OwnerUserId = '{owner}' ORDER BY Id").ToListAsync());
            Assert.Equal(owner, await db.Database.SqlQueryRaw<string>($"SELECT OwnerUserId AS Value FROM pp_portfolio WHERE Uuid = 'pf-{source}'").SingleAsync());
            Assert.Equal(owner, await db.Database.SqlQueryRaw<string>($"SELECT OwnerUserId AS Value FROM projects WHERE Id = {source}").SingleAsync());
        }

        Assert.Equal(0, await ScalarAsync(db, "SELECT COUNT(*) AS Value FROM sqlite_schema WHERE name = 'import_sources'"));
        Assert.Equal(0, await ScalarAsync(db, "SELECT COUNT(*) AS Value FROM sqlite_schema WHERE sql LIKE '%import_sources%'"));
        Assert.Equal(0, await ScalarAsync(db, "SELECT COUNT(*) AS Value FROM pragma_table_info('cloud_file_links') WHERE name = 'TargetSourceName'"));
        Assert.Equal(0, await ScalarAsync(db, "SELECT COUNT(*) AS Value FROM pragma_foreign_key_check"));
        Assert.Equal("ok", await db.Database.SqlQueryRaw<string>("SELECT integrity_check AS Value FROM pragma_integrity_check").FirstAsync());

        var states = await db.UserDataStates.AsNoTracking().OrderBy(s => s.IdSpace).ToListAsync();
        Assert.Equal([(First, 1), (Second, 2)], states.Select(s => (s.UserId, s.IdSpace)));
        Assert.Equal("1,3", string.Join(',', states[0].LegacyFileKeys!.Split(',').Order(StringComparer.Ordinal)));
        Assert.NotNull(states[0].LastUploadAt);
        Assert.NotNull(states[0].StructureTakenOverAt);

        // The model works on the migrated schema: a new project, and a new user's id space after every one in use.
        db.Projects.Add(new Models.Project { Name = "New", OwnerUserId = Second });
        await db.SaveChangesAsync();
        Assert.Equal(3, await RowIds.IdSpaceAsync(db, "u-third"));
    }

    [Fact]
    public async Task A_source_without_owner_that_holds_data_stops_the_migration()
    {
        var (connection, db, migrator) = await OpenAsync();
        await using var _ = connection;
        await using var __ = db;
        await SeedAsync(db);
        await db.Database.ExecuteSqlRawAsync($"INSERT INTO accounts (Id, SourceId, name, provider) VALUES ({Id(4, 1)}, 4, 'Example Orphan', 'moneymoney')");

        await Assert.ThrowsAsync<SqliteException>(() => migrator.MigrateAsync());
        Assert.Equal(1, await ScalarAsync(db, "SELECT COUNT(*) AS Value FROM sqlite_schema WHERE name = 'import_sources'"));
    }
}
