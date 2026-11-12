using Microsoft.EntityFrameworkCore;
using OpenBudget.Connectors;
using OpenBudget.Connectors.Sync;
using OpenBudget.Models;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests;

/// <summary>
/// The bank's standing orders and scheduled transfers: storing them per account, who may see them, and how they
/// enter the cashflow projection without being counted twice. All data is made up.
/// </summary>
public sealed class ScheduledPaymentTests
{
    private const string LandlordIban = "DE00000000000000000099";

    private static ScheduledPayment Rent(long accountId, int sourceId = 1, long cents = -85000) => new()
    {
        SourceId = sourceId,
        AccountId = accountId,
        Kind = ScheduledPaymentKind.Standing,
        BankOrderId = "SO-1",
        NextDate = new DateOnly(2027, 1, 1),
        FirstDate = new DateOnly(2024, 1, 1),
        IntervalUnit = ScheduleUnit.Month,
        IntervalCount = 1,
        ExecutionDay = 1,
        Amount = cents,
        CounterpartyName = "Example Landlord",
        CounterpartyIban = LandlordIban,
        Purpose = "Miete",
        FetchedAt = new DateTime(2026, 11, 12, 0, 0, 0, DateTimeKind.Utc),
    };

    // ---------------------------------------------------------------- storage

    [Fact]
    public async Task A_sync_replaces_the_accounts_orders_and_one_without_a_list_keeps_them()
    {
        await using var t = new TestDb();
        var account = t.AddAccount(10, "Giro");
        var other = t.AddAccount(11, "Other");
        t.Db.ScheduledPayments.Add(Rent(10));
        t.Db.ScheduledPayments.Add(Rent(11));
        await t.Db.SaveChangesAsync();

        var refresh = new AccountRefresh(null, [], [], [
            new RemoteScheduledPayment(ScheduledPaymentKind.Scheduled, "T-1", new DateOnly(2026, 12, 24), -49.99m, "EUR",
                "Example Shop", "DE00 0000 0000 0000 0000 33", null, "Bestellung"),
        ]);
        await ScheduledPaymentStore.ApplyAsync(t.Db, account, refresh, DateTime.UtcNow, default);
        await t.Db.SaveChangesAsync();

        var rows = await t.Db.ScheduledPayments.AsNoTracking().OrderBy(p => p.AccountId).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(11, rows[1].AccountId); // the other account's order is untouched
        var stored = rows[0];
        Assert.Equal((ScheduledPaymentKind.Scheduled, -4999L, "DE00000000000000000033"), (stored.Kind, stored.Amount, stored.CounterpartyIban));
        Assert.Null(stored.IntervalUnit);

        await ScheduledPaymentStore.ApplyAsync(t.Db, account, new AccountRefresh(null, [], []), DateTime.UtcNow, default);
        Assert.Equal(2, await t.Db.ScheduledPayments.CountAsync());
        _ = other;
    }

    // ---------------------------------------------------------------- access

    [Fact]
    public async Task Orders_follow_the_visibility_of_their_account()
    {
        await using var t = await AccessWorld.BuildAsync(ProjectRole.Viewer);
        var stale = Rent(AccessWorld.AccountWp, AccessWorld.SourceAlice);
        stale.NextDate = new DateOnly(2026, 10, 1); // stored by a sync six weeks ago
        t.Db.ScheduledPayments.Add(stale);
        t.Db.ScheduledPayments.Add(Rent(AccessWorld.AccountPrivate, AccessWorld.SourceAlice, -1000));
        t.Db.ScheduledPayments.Add(Rent(AccessWorld.AccountBob, AccessWorld.SourceBob, -700));
        await t.Db.SaveChangesAsync();
        var today = new DateOnly(2026, 11, 12);

        var bob = t.ForUser(AccessWorld.Bob);
        var service = new ScheduledPaymentService(bob.Factory);
        var shared = await service.GetAsync(AccessWorld.Shared, today);
        var item = Assert.Single(shared.Items);
        Assert.Equal(AccessWorld.AccountWp, item.AccountId);
        Assert.Equal(new DateOnly(2026, 12, 1), item.NextDate); // the next execution after "today", not the stored one
        Assert.Empty((await service.GetAsync(AccessWorld.Private, today)).Items);
        await using (var db = await bob.Factory.CreateDbContextAsync())
        {
            Assert.Equal(
                new[] { AccessWorld.AccountWp, AccessWorld.AccountBob }.Order(),
                (await db.ScheduledPayments.Select(p => p.AccountId).ToListAsync()).Order());
        }

        var alice = new ScheduledPaymentService(t.ForUser(AccessWorld.Alice).Factory);
        Assert.Single((await alice.GetAsync(AccessWorld.Private, today)).Items);
        Assert.Empty((await alice.GetAsync(AccessWorld.BobProject, today)).Items);
    }

    [Fact]
    public async Task Only_the_owner_may_write_orders()
    {
        await using var t = await AccessWorld.BuildAsync(ProjectRole.Editor);
        await using var db = await t.ForUser(AccessWorld.Bob).Factory.CreateDbContextAsync();
        db.ScheduledPayments.Add(Rent(AccessWorld.AccountWp, AccessWorld.SourceAlice));
        await Assert.ThrowsAsync<AccessDeniedException>(() => db.SaveChangesAsync());
    }

    // ---------------------------------------------------------------- cashflow

    private const long Group = 100, RentLeaf = 101, Other = 102;

    private static async Task<TestDb> CashflowWorldAsync(AccountScope scope = AccountScope.Exclusive, bool trackRent = true)
    {
        var t = new TestDb();
        t.AddGroup(Group);
        t.AddLeaf(RentLeaf, Group, "Rent");
        t.AddLeaf(Other, Group, "Other");
        t.AddProject(1);
        if (trackRent) t.TrackCategory(1, RentLeaf);
        else t.TrackCategory(1, Other);
        t.AddAccount(10, "Giro");
        t.Db.ProjectAccountAssignments.Add(new ProjectAccountAssignment { ProjectId = 1, AccountId = 10, Scope = scope, InCashflow = true });
        // The order's earlier executions, booked and categorised as rent; a later booking of the same amount went elsewhere.
        t.Db.Transactions.Add(new Transaction
        {
            Id = 1,
            SourceId = 1,
            AccountId = 10,
            CategoryId = RentLeaf,
            Amount = -85000,
            Name = "Example Landlord",
            CounterpartyIban = "DE00 0000 0000 0000 0000 99",
            Timestamp = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(),
        });
        t.Db.Transactions.Add(new Transaction
        {
            Id = 2,
            SourceId = 1,
            AccountId = 10,
            CategoryId = Other,
            Amount = -85000,
            Name = "Somebody Else",
            Timestamp = new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(),
        });
        t.Db.ScheduledPayments.Add(Rent(10));
        t.Db.ScheduledPayments.Add(new ScheduledPayment
        {
            SourceId = 1,
            AccountId = 10,
            Kind = ScheduledPaymentKind.Scheduled,
            NextDate = new DateOnly(2027, 3, 10),
            Amount = -20000,
            CounterpartyName = "Example Shop",
            FetchedAt = DateTime.UtcNow,
        });
        await t.Db.SaveChangesAsync();
        return t;
    }

    [Fact]
    public async Task Standing_orders_are_booked_on_their_days_and_taken_out_of_the_category_projection()
    {
        await using var t = await CashflowWorldAsync();
        var rentRow = (RentLeaf, CategoryItemType.Category);
        var scheduled = await t.Cashflow.GetScheduledCashflowAsync(1, 2027, new DateOnly(2026, 12, 15), [rentRow]);

        Assert.Equal(13, scheduled.Occurrences.Count); // twelve rents and the one-off transfer
        Assert.Equal(new DateOnly(2027, 1, 1), scheduled.Occurrences[0].Date);
        Assert.All(scheduled.CoveredByRow[rentRow], m => Assert.Equal(-850m, m)); // the one-off covers nothing

        // The budget projects 850 rent each month; the standing order pays exactly that.
        var expenses = new List<CategoryCashflow>
        {
            new() { CategoryId = RentLeaf, ItemType = CategoryItemType.Category, MonthlyAmounts = Enumerable.Repeat(850m, 12).ToArray(), IsAggregateRow = true },
        };
        var weights = new decimal[31];
        weights[14] = 1m; // history says the 15th
        var histograms = new Dictionary<(long, CategoryItemType), DayOfMonthDistribution> { [rentRow] = new() { Weights = weights } };
        var data = new ProjectDailyDeltaResult { HasAccounts = true, StartingBalance = 20000m, DailyDelta = new decimal[365], LastActualDayOfYear = 0 };

        var result = CashflowCalculator.ComputeBalance(data, 2027, 2026, 1, [], expenses, histograms, [], 0m, scheduled);

        Assert.Equal(20000m - 12 * 850m - 200m, result.BalanceDaily[^1]); // not 24 rents
        Assert.Equal(20000m - 850m, result.BalanceDaily[0]);              // on the 1st, the order's day
        Assert.Equal(20000m - 850m, result.BalanceDaily[14]);             // nothing more on the historical 15th
        Assert.Equal(20000m - 3 * 850m - 200m, result.BalanceDaily[new DateTime(2027, 3, 10).DayOfYear - 1]);

        var without = CashflowCalculator.ComputeBalance(data, 2027, 2026, 1, [], expenses, histograms, [], 0m);
        Assert.Equal(20000m - 12 * 850m, without.BalanceDaily[^1]);
    }

    [Fact]
    public async Task A_higher_budget_keeps_the_remainder_and_a_lower_one_never_turns_into_income()
    {
        await using var t = await CashflowWorldAsync();
        var rentRow = (RentLeaf, CategoryItemType.Category);
        var scheduled = await t.Cashflow.GetScheduledCashflowAsync(1, 2027, new DateOnly(2026, 12, 15), [rentRow]);
        var data = new ProjectDailyDeltaResult { HasAccounts = true, DailyDelta = new decimal[365] };
        var histograms = new Dictionary<(long, CategoryItemType), DayOfMonthDistribution> { [rentRow] = DayOfMonthDistribution.Day1Fallback() };

        static List<CategoryCashflow> Rows(decimal monthly) =>
            [new() { CategoryId = RentLeaf, ItemType = CategoryItemType.Category, MonthlyAmounts = Enumerable.Repeat(monthly, 12).ToArray(), IsAggregateRow = true }];

        var higher = CashflowCalculator.ComputeBalance(data, 2027, 2026, 1, [], Rows(1000m), histograms, [], 0m, scheduled);
        Assert.Equal(-12 * 1000m - 200m, higher.BalanceDaily[^1]);
        var lower = CashflowCalculator.ComputeBalance(data, 2027, 2026, 1, [], Rows(500m), histograms, [], 0m, scheduled);
        Assert.Equal(-12 * 850m - 200m, lower.BalanceDaily[^1]);
    }

    [Fact]
    public async Task On_a_shared_account_only_orders_in_the_projects_categories_count()
    {
        await using (var t = await CashflowWorldAsync(AccountScope.Shared))
        {
            var scheduled = await t.Cashflow.GetScheduledCashflowAsync(1, 2027, new DateOnly(2026, 12, 15), [(RentLeaf, CategoryItemType.Category)]);
            Assert.Equal(12, scheduled.Occurrences.Count); // the uncategorised one-off is not the project's
        }
        await using (var t = await CashflowWorldAsync(AccountScope.Shared, trackRent: false))
        {
            var scheduled = await t.Cashflow.GetScheduledCashflowAsync(1, 2027, new DateOnly(2026, 12, 15), [(Other, CategoryItemType.Category)]);
            Assert.Empty(scheduled.Occurrences);
        }
    }

    [Fact]
    public async Task Accounts_outside_the_cashflow_and_past_years_add_nothing()
    {
        await using var t = await CashflowWorldAsync();
        Assert.Empty((await t.Cashflow.GetScheduledCashflowAsync(1, 2025, new DateOnly(2026, 12, 15), [])).Occurrences);
        await t.Db.ProjectAccountAssignments.ExecuteUpdateAsync(s => s.SetProperty(a => a.InCashflow, false));
        Assert.Empty((await t.Cashflow.GetScheduledCashflowAsync(1, 2027, new DateOnly(2026, 12, 15), [])).Occurrences);
    }
}
