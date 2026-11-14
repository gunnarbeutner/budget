#pragma warning disable EF1002 // test-only SQL built from constants of this file
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests;

/// <summary>
/// The migration that turns accounts a MoneyMoney import brought into manual accounts: an unlinked one becomes manual,
/// a linked one its connection's, and other providers stay.
/// </summary>
public sealed class ImportedAccountsManualMigrationTests
{
    private const string Before = "20261113130231_BackgroundDefaults";
    private const string Owner = "u-owner";

    [Fact]
    public async Task Imported_accounts_become_manual_or_their_connections()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<BudgetDbContext>().UseSqlite(connection)
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)).Options;
        await using var db = new BudgetDbContext(options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(Before);

        await db.Database.ExecuteSqlRawAsync($"""
            INSERT INTO user_data_states (UserId, IdSpace, LastUploadAt, MoneyMoneyImportFinishedAt) VALUES ('{Owner}', 1, '2026-11-01 08:00:00', '2026-11-02 08:00:00');
            INSERT INTO accounts (Id, OwnerUserId, name, provider) VALUES
                (1, '{Owner}', 'Example Giro', 'moneymoney'),
                (2, '{Owner}', 'Example Savings', 'moneymoney'),
                (3, '{Owner}', 'Example Card', 'fake'),
                (4, '{Owner}', 'Example Portfolio', 'portfolioperformance');
            INSERT INTO bank_logins (Id, OwnerUserId, ConnectorType, DisplayName, RefreshInterval, CreatedAt, CredentialsInvalid)
                VALUES (1, '{Owner}', 'fake', 'Example Bank', 0, '2026-11-01', 0);
            INSERT INTO bank_login_accounts (Id, BankLoginId, RemoteId, Name, Type, CreatedByConnector, Closed, StatementsEnabled, StatementsInterval,
                                             LinkedAccountId, LastSuccessAt, PendingRefreshJson) VALUES
                (1, 1, 'R-GIRO', 'Giro', 0, 0, 0, 0, 0, 1, '2026-11-03 08:00:00', 'report'),
                (2, 1, 'R-CARD', 'Card', 0, 0, 0, 0, 0, 3, '2026-11-03 08:00:00', NULL);
            """);

        await migrator.MigrateAsync();

        var providers = await db.Accounts.AsNoTracking().OrderBy(a => a.Id).Select(a => a.Provider).ToListAsync();
        Assert.Equal(["fake", "manual", "fake", "portfolioperformance"], providers);
        var giroLink = await db.BankLoginAccounts.AsNoTracking().SingleAsync(l => l.Id == 1);
        Assert.NotNull(giroLink.SwitchedAt);
        Assert.Null(giroLink.LastSuccessAt);   // the first sync continues from the imported history
        var cardLink = await db.BankLoginAccounts.AsNoTracking().SingleAsync(l => l.Id == 2);
        Assert.NotNull(cardLink.LastSuccessAt);
        Assert.Null(cardLink.SwitchedAt);
        Assert.NotNull((await db.UserDataStates.AsNoTracking().SingleAsync()).LastUploadAt);
        Assert.Equal(0, await db.Database.SqlQueryRaw<long>(
            "SELECT COUNT(*) AS Value FROM pragma_table_info('user_data_states') WHERE name = 'MoneyMoneyImportFinishedAt'").SingleAsync());
        Assert.Equal(0, await db.Database.SqlQueryRaw<long>(
            "SELECT COUNT(*) AS Value FROM pragma_table_info('bank_login_accounts') WHERE name LIKE 'Pending%'").SingleAsync());
    }
}
