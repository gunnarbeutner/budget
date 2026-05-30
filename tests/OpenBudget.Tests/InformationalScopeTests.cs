using OpenBudget.Models;
using Xunit;

namespace OpenBudget.Tests;

/// <summary>
/// AccountScope.Informational links an account for Browse + balance totals but keeps it out of the
/// cashflow projection. These pin both halves of that contract.
/// </summary>
public class InformationalScopeTests
{
    // MoneyMoney's packed balance blob: first "<tag>:<number>" pair is the amount.
    private static string Balance(decimal euros) => $"11:{euros:0.000000},3:EUR,";

    private static ProjectAccountAssignment Assign(long accountId, AccountScope scope) => new()
    {
        ProjectId = 1,
        AccountId = accountId,
        Scope = scope
    };

    [Fact]
    public async Task BalanceSummary_splits_cashflow_from_total_and_uses_holdings_for_depots()
    {
        await using var t = new TestDb();
        t.AddProject(1);

        var giro = t.AddAccount(10, "Giro");
        giro.Balances = Balance(1000m);
        t.Db.ProjectAccountAssignments.Add(Assign(10, AccountScope.Shared));

        // Informational securities depot: its cash blob is deliberately wrong (99) to prove the
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
        t.Db.ProjectAccountAssignments.Add(Assign(20, AccountScope.Informational));
        await t.Db.SaveChangesAsync();

        var summary = (await t.Projects.GetAllProjectsBalancesAsync())[1];

        // Cashflow figure: only the Shared giro.
        Assert.Equal(1000m, summary.Balance);
        Assert.Equal(1, summary.AccountCount);
        // Total: giro + depot's holdings value (5000, not the 99 blob).
        Assert.Equal(6000m, summary.TotalBalance);
        Assert.Equal(2, summary.TotalAccountCount);
        Assert.True(summary.HasInformational);
    }

    [Fact]
    public async Task Cashflow_starting_balance_excludes_informational_accounts()
    {
        await using var t = new TestDb();
        t.AddProject(1);

        var giro = t.AddAccount(10, "Giro");
        giro.Balances = Balance(1000m);
        t.Db.ProjectAccountAssignments.Add(Assign(10, AccountScope.Shared));

        var depot = t.AddAccount(20, "Depot");
        depot.Balances = Balance(5000m);
        t.Db.ProjectAccountAssignments.Add(Assign(20, AccountScope.Informational));
        await t.Db.SaveChangesAsync();

        var result = await t.Cashflow.GetProjectActualDailyDeltaAsync(1, 2026);

        Assert.True(result.HasAccounts);
        // With no transactions the Jan-1 starting balance is just the cashflow accounts' balance —
        // the informational depot's 5000 must not be included (would make it 6000).
        Assert.Equal(1000m, result.StartingBalance);
    }

    [Fact]
    public async Task BalanceSummary_without_informational_reports_equal_totals()
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
        Assert.False(summary.HasInformational);
    }
}
