using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OpenBudget.Models;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests;

/// <summary>
/// An assignment with InCashflow = false links an account for Browse + balance totals but keeps it
/// out of the cashflow projection. These pin both halves of that contract, the defaults for new
/// assignments and the migration of the former third scope value.
/// </summary>
public class InCashflowTests
{
    // the source's packed balance blob: first "<tag>:<number>" pair is the amount.
    private static string Balance(decimal euros) => $"11:{euros:0.000000},3:EUR,";

    private static ProjectAccountAssignment Assign(long accountId, AccountScope scope, bool inCashflow = true) => new()
    {
        ProjectId = 1,
        AccountId = accountId,
        Scope = scope,
        InCashflow = inCashflow,
    };

    [Fact]
    public async Task BalanceSummary_splits_cashflow_from_total_and_uses_holdings_for_depots()
    {
        await using var t = new TestDb();
        t.AddProject(1);

        var giro = t.AddAccount(10, "Giro");
        giro.Balances = Balance(1000m);
        t.Db.ProjectAccountAssignments.Add(Assign(10, AccountScope.Shared));

        // Securities depot outside the cashflow: its cash blob is deliberately wrong (99) to prove the
        // total uses holdings market value (5000), not the blob.
        var depot = t.AddAccount(20, "Depot");
        depot.Type = 7;
        depot.Balances = Balance(99m);
        t.Db.Holdings.Add(new Holding
        {
            Id = 1000,
            SourceId = 1,
            AccountId = 20,
            Quantity = 1,
            Amount = 5000,
            AmountCurrency = "EUR"
        });
        t.Db.ProjectAccountAssignments.Add(Assign(20, AccountScope.Exclusive, inCashflow: false));
        await t.Db.SaveChangesAsync();

        var summary = (await t.Projects.GetAllProjectsBalancesAsync())[1];

        // Cashflow figure: only the Shared giro.
        Assert.Equal(1000m, summary.Balance);
        Assert.Equal(1, summary.AccountCount);
        // Total: giro + depot's holdings value (5000, not the 99 blob).
        Assert.Equal(6000m, summary.TotalBalance);
        Assert.Equal(2, summary.TotalAccountCount);
        Assert.True(summary.HasNonCashflowAccounts);
    }

    [Fact]
    public async Task Cashflow_starting_balance_excludes_accounts_outside_the_cashflow()
    {
        await using var t = new TestDb();
        t.AddProject(1);

        var giro = t.AddAccount(10, "Giro");
        giro.Balances = Balance(1000m);
        t.Db.ProjectAccountAssignments.Add(Assign(10, AccountScope.Shared));

        var depot = t.AddAccount(20, "Depot");
        depot.Balances = Balance(5000m);
        t.Db.ProjectAccountAssignments.Add(Assign(20, AccountScope.Exclusive, inCashflow: false));
        await t.Db.SaveChangesAsync();

        var result = await t.Cashflow.GetProjectActualDailyDeltaAsync(1, 2026);

        Assert.True(result.HasAccounts);
        // With no transactions the Jan-1 starting balance is just the cashflow accounts' balance —
        // the depot outside the cashflow's 5000 must not be included (would make it 6000).
        Assert.Equal(1000m, result.StartingBalance);
    }

    [Fact]
    public async Task BalanceSummary_with_only_cashflow_accounts_reports_equal_totals()
    {
        await using var t = new TestDb();
        t.AddProject(1);
        var giro = t.AddAccount(10, "Giro");
        giro.Balances = Balance(1500m);
        t.Db.ProjectAccountAssignments.Add(Assign(10, AccountScope.Exclusive));
        await t.Db.SaveChangesAsync();

        var summary = (await t.Projects.GetAllProjectsBalancesAsync())[1];

        Assert.Equal(1500m, summary.Balance);
        Assert.Equal(1500m, summary.TotalBalance);
        Assert.False(summary.HasNonCashflowAccounts);
    }

    [Theory]
    [InlineData(1L, true)]   // giro
    [InlineData(2L, true)]   // savings
    [InlineData(3L, true)]   // credit card
    [InlineData(6L, true)]   // cash
    [InlineData(4L, false)]  // loan
    [InlineData(7L, false)]  // depot
    [InlineData(0L, false)]  // other
    [InlineData(5L, false)]  // manual
    [InlineData(null, false)]
    public void New_assignments_count_in_cashflow_only_for_cash_accounts(long? type, bool expected)
    {
        var account = new Account { Id = 1, Name = "Example", Type = type };
        Assert.Equal(expected, AccountAssignmentDefaults.InCashflow(account));
        var assignment = AccountAssignmentDefaults.For(3, account);
        Assert.Equal(expected, assignment.InCashflow);
        Assert.Equal(AccountScope.Shared, assignment.Scope);
    }

    [Fact]
    public void Portfolio_accounts_start_outside_the_cashflow()
    {
        Assert.False(AccountAssignmentDefaults.InCashflow(new Account { Id = 1, Name = "Ledger", Type = 1, Provider = AccountProviders.PortfolioPerformance }));
        Assert.False(AccountAssignmentDefaults.InCashflow(new Account { Id = 2, Name = "Depot", Type = 1, Portfolio = 1 }));
    }

    [Fact]
    public async Task Toggling_in_cashflow_changes_the_cashflow_starting_balance()
    {
        await using var t = new TestDb();
        t.AddProject(1);
        t.AddAccount(10, "Giro").Balances = Balance(1000m);
        t.AddAccount(20, "Savings").Balances = Balance(400m);
        t.Db.ProjectAccountAssignments.Add(Assign(10, AccountScope.Shared));
        t.Db.ProjectAccountAssignments.Add(Assign(20, AccountScope.Exclusive));
        await t.Db.SaveChangesAsync();

        Assert.Equal(1400m, (await t.Cashflow.GetProjectActualDailyDeltaAsync(1, 2026)).StartingBalance);
        await t.Spending.SetAccountInCashflowAsync(1, 20, false);
        Assert.Equal(1000m, (await t.Cashflow.GetProjectActualDailyDeltaAsync(1, 2026)).StartingBalance);
    }

    [Fact]
    public async Task Migration_moves_former_informational_rows_outside_the_cashflow()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<BudgetDbContext>().UseSqlite(connection)
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)).Options;
        await using var db = new BudgetDbContext(options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261111203658_InitialBalance");

        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF");
        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO project_account_assignments (Id, ProjectId, AccountId, Scope, CreatedAt) VALUES
            (1, 1, 10, 0, '2026-01-01'), (2, 1, 11, 1, '2026-01-01'), (3, 1, 12, 2, '2026-01-01')
            """);
        await migrator.MigrateAsync();

        var rows = await db.ProjectAccountAssignments.AsNoTracking().OrderBy(r => r.Id)
            .Select(r => new { r.Scope, r.InCashflow }).ToListAsync();
        Assert.Equal(new { Scope = AccountScope.Shared, InCashflow = true }, rows[0]);
        Assert.Equal(new { Scope = AccountScope.Exclusive, InCashflow = true }, rows[1]);
        Assert.Equal(new { Scope = AccountScope.Exclusive, InCashflow = false }, rows[2]);
    }
}
