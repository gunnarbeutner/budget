using Xunit;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.Tests;

/// <summary>
/// The portfolio boundary behind the value chart: the project's depots plus their settlement accounts, valued together,
/// with flows only where money crosses it.
/// </summary>
public class PortfolioFlowsTests
{
    private const int ProjectId = 1;
    private const long PpDepot = 1, PpLedger = 2, Giro = 3, BrokerCash = 4, BankDepot = 5, OtherBrokerCash = 6;
    private static readonly DateTime Sale = new(2026, 3, 10);
    private static readonly DateTime Today = Sale.AddDays(4);

    private static long Ts(DateTime d) => ((DateTimeOffset)DateTime.SpecifyKind(d.AddHours(12), DateTimeKind.Utc)).ToUnixTimeSeconds();

    /// <summary>
    /// A PortfolioPerformance depot is sold (its ledger books the sale), the proceeds land on a giro outside the
    /// portfolio, most of it moves on to a broker's cash account and buys securities in that broker's depot.
    /// </summary>
    private static async Task<TestDb> SeedAsync()
    {
        var t = new TestDb();
        t.AddProject(ProjectId);
        t.Db.Accounts.AddRange(
            new Account { Id = PpDepot, OwnerUserId = TestDb.DefaultOwner, Name = "PP depot", Type = (long)AccountType.Depot, Provider = AccountProviders.PortfolioPerformance },
            new Account { Id = PpLedger, OwnerUserId = TestDb.DefaultOwner, Name = "PP ledger", Type = 0, Provider = AccountProviders.PortfolioPerformance },
            new Account { Id = Giro, OwnerUserId = TestDb.DefaultOwner, Name = "Giro", Type = (long)AccountType.Giro },
            new Account { Id = BrokerCash, OwnerUserId = TestDb.DefaultOwner, Name = "Broker cash", Type = (long)AccountType.Cash, Role = AccountRole.BrokerCash, Currency = "EUR", Balances = "11:8.000000,3:EUR," },
            new Account { Id = BankDepot, OwnerUserId = TestDb.DefaultOwner, Name = "Broker depot", Type = (long)AccountType.Depot },
            new Account { Id = OtherBrokerCash, OwnerUserId = TestDb.DefaultOwner, Name = "Other broker cash", Type = (long)AccountType.Cash, Role = AccountRole.BrokerCash, Currency = "EUR" });
        t.Db.ProjectAccountAssignments.AddRange(
            new ProjectAccountAssignment { ProjectId = ProjectId, AccountId = PpDepot },
            new ProjectAccountAssignment { ProjectId = ProjectId, AccountId = BankDepot },
            new ProjectAccountAssignment { ProjectId = ProjectId, AccountId = Giro });
        t.Db.PpPortfolioSettings.Add(new PpPortfolioSetting { OwnerUserId = TestDb.DefaultOwner, PpPortfolioUuid = "p", DepotAccountId = PpDepot, LedgerAccountId = PpLedger });
        var login = new BankLogin { OwnerUserId = TestDb.DefaultOwner, ConnectorType = "broker", DisplayName = "Broker" };
        login.Accounts.Add(new BankLoginAccount { RemoteId = "cash", Name = "Cash", LinkedAccountId = BrokerCash });
        login.Accounts.Add(new BankLoginAccount { RemoteId = "depot", Name = "Depot", LinkedAccountId = BankDepot });
        login.Accounts.Add(new BankLoginAccount { RemoteId = "cash2", Name = "Cash 2", LinkedAccountId = OtherBrokerCash });
        t.Db.BankLogins.Add(login);

        Transaction Booking(long id, long account, DateTime day, long cents, long? peer = null, string? text = null, string? externalId = null) => new()
        {
            Id = id,
            OwnerUserId = TestDb.DefaultOwner,
            AccountId = account,
            Timestamp = Ts(day),
            Amount = cents,
            Currency = "EUR",
            TransferPeerId = peer,
            BookingText = text,
            ExternalId = externalId,
            Splitted = 0,
        };
        t.Db.Transactions.AddRange(
            Booking(10, PpLedger, Sale, 41850, text: "Verkauf", externalId: PortfolioAccountSync.ExternalIdPrefix + "sale"),
            Booking(11, Giro, Sale, 41850),
            Booking(12, Giro, Sale.AddDays(1), -38200, peer: 13),
            Booking(13, BrokerCash, Sale.AddDays(1), 38200, peer: 12),
            Booking(14, BrokerCash, Sale.AddDays(2), -38000),                 // buy
            Booking(15, BrokerCash, Sale.AddDays(3), 100),                    // dividend
            Booking(16, BrokerCash, Sale.AddDays(3), 500, peer: 17),          // from another settlement account: internal
            Booking(17, OtherBrokerCash, Sale.AddDays(3), -500, peer: 16));
        await t.Db.SaveChangesAsync();
        return t;
    }

    [Fact]
    public async Task Settlement_accounts_are_the_broker_cash_accounts_of_a_depots_login_or_the_project()
    {
        await using var t = await SeedAsync();

        Assert.Equal([BrokerCash, OtherBrokerCash], (await t.Portfolio.GetProjectSettlementAccountIdsAsync(ProjectId, [PpDepot, BankDepot])).Order());

        // A broker cash account of the project without a login counts; one of neither the project nor a depot's login does not.
        t.Db.Accounts.AddRange(
            new Account { Id = 7, OwnerUserId = TestDb.DefaultOwner, Name = "Assigned", Type = (long)AccountType.Cash, Role = AccountRole.BrokerCash },
            new Account { Id = 8, OwnerUserId = TestDb.DefaultOwner, Name = "Unrelated", Type = (long)AccountType.Cash, Role = AccountRole.BrokerCash });
        t.Db.ProjectAccountAssignments.Add(new ProjectAccountAssignment { ProjectId = ProjectId, AccountId = 7 });
        await t.Db.SaveChangesAsync();
        Assert.Equal([BrokerCash, OtherBrokerCash, 7L], (await t.Portfolio.GetProjectSettlementAccountIdsAsync(ProjectId, [PpDepot, BankDepot])).Order());
    }

    [Fact]
    public async Task Proceeds_moved_through_an_outside_account_into_another_depot_count_out_and_back_in()
    {
        await using var t = await SeedAsync();
        long[] depots = [PpDepot, BankDepot];
        var cash = await t.Portfolio.GetProjectSettlementAccountIdsAsync(ProjectId, depots);

        var flows = await t.TransactionQueries.GetPortfolioFlowsAsync(TestDb.DefaultOwner, depots, cash);

        Assert.Equal([(Sale, -418.5m), (Sale.AddDays(1), 382m)], flows.OrderBy(f => f.Key).Select(f => (DateTime.SpecifyKind(f.Key, DateTimeKind.Unspecified), f.Value)));

        var (cashHistory, cashNow) = await t.TransactionQueries.GetAccountBalanceHistoryAsync(TestDb.DefaultOwner, cash);
        Assert.Equal(3m, cashNow);
        Assert.Equal(
            [(Sale.AddDays(1), 382m), (Sale.AddDays(2), 2m), (Sale.AddDays(3), 8m)],
            cashHistory[BrokerCash]);
        Assert.Equal([(Sale.AddDays(3), -5m)], cashHistory[OtherBrokerCash]);

        var history = new Dictionary<long, List<(DateTime Day, decimal Value)>>
        {
            [PpDepot] = [(Sale.AddDays(-1), 420m), (Sale, 0m)],
            [BankDepot] = [(Sale.AddDays(2), 380m)],
        };
        foreach (var (id, points) in cashHistory) history[id] = points;

        var s = PortfolioValueHistory.Build(history, 370m + cashNow, Today, from: null, flows);

        Assert.Equal([420m, 0m, 382m, 382m, 383m, 373m], s.Points.Select(p => p.Value));
        Assert.Equal(-47m, s.Change);
        Assert.Equal(-36.5m, s.NetFlows);
        // The sale's 1.50 below the last value, the dividend and the depot's 10 loss.
        Assert.Equal(-10.5m, s.Gain);
    }

    [Fact]
    public async Task Without_settlement_accounts_only_the_ledger_trades_count()
    {
        await using var t = await SeedAsync();

        var flows = await t.TransactionQueries.GetPortfolioFlowsAsync(TestDb.DefaultOwner, [PpDepot, BankDepot]);

        Assert.Equal([(Sale, -418.5m)], flows.Select(f => (DateTime.SpecifyKind(f.Key, DateTimeKind.Unspecified), f.Value)));
    }
}
