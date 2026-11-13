#pragma warning disable CA2000 // test objects live for the test
using Microsoft.EntityFrameworkCore;
using OpenBudget.Connectors;
using OpenBudget.Connectors.Sync;
using OpenBudget.Models;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests.Connectors;

/// <summary>A connector depot's value history: one day of chart points per sync day, written by the sync runner.</summary>
public sealed class DepotHistoryTests
{
    private static readonly DateTime Day1 = new(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>A bank with one depot whose positions, balance and failure the test sets.</summary>
    private sealed class DepotBank
    {
        public const string Type = "depotbank";
        public const string DepotId = "DEPOT-1";

        public List<RemotePosition> Positions { get; set; } =
        [
            new("Example Equity Fund", "XX0000000011", null, null, 10m, 100m, "EUR", 1000m),
            new("Example Bond Fund", "XX0000000012", null, null, 5m, 40m, "EUR", 200m),
        ];

        public bool Fail { get; set; }

        public ConnectorRegistration Registration => new(Type, "Depot Bank", "Test depot.", _ => new Connector(this));

        private sealed class Connector(DepotBank bank) : IBankConnector
        {
            public string ConnectorType => Type;

            public Task<ConnectorStep<ConnectorSession?>> LoginAsync(ConnectorCredentials credentials, ConnectorSession? storedSession, ChallengeAnswer? answer, CancellationToken ct) =>
                Task.FromResult(ConnectorStep.Done<ConnectorSession?>(new ConnectorSession()));

            public Task<IReadOnlyList<RemoteAccount>> ListAccountsAsync(CancellationToken ct) =>
                Task.FromResult<IReadOnlyList<RemoteAccount>>([new RemoteAccount(DepotId, "Example Depot", RemoteAccountType.Depot, null, "EUR")]);

            public Task<ConnectorStep<AccountRefresh>> RefreshAsync(RemoteAccount account, DateOnly? since, ChallengeAnswer? answer, CancellationToken ct)
            {
                if (bank.Fail) throw new ConnectorException("Depot Bank is unavailable.");
                var positions = bank.Positions.ToList();
                return Task.FromResult(ConnectorStep.Done(new AccountRefresh(positions.Sum(p => p.Value ?? 0), [], positions)));
            }

            public Task<ConnectorStep<IReadOnlyList<RemoteStatement>>> FetchStatementsAsync(
                IReadOnlyList<RemoteAccount> accounts, IReadOnlySet<string> knownIdentifiers, ChallengeAnswer? answer, CancellationToken ct) =>
                Task.FromResult(ConnectorStep.Done<IReadOnlyList<RemoteStatement>>([]));

            public Task<ConnectorSession?> LogoutAsync(CancellationToken ct) => Task.FromResult<ConnectorSession?>(new ConnectorSession());
        }
    }

    private static async Task<(BankWorld World, DepotBank Bank, int LoginId)> SetupAsync()
    {
        var bank = new DepotBank();
        var world = await BankWorld.CreateAsync(extras: new BankWorldExtras { Registrations = [bank.Registration] });
        world.Clock.Set(Day1);
        var view = await world.For(BankWorld.Alice).CreateLoginAsync(DepotBank.Type, "Depot", "user", "secret");
        await world.SyncAsync(BankWorld.Alice, view.Id, new SyncRequest(view.Id, false, false));
        return (world, bank, view.Id);
    }

    private static Task<BankSyncStatus> SyncAsync(BankWorld w, int loginId) =>
        w.SyncAsync(BankWorld.Alice, loginId, new SyncRequest(loginId, true, false));

    private static async Task<List<PortfolioChartPoint>> PointsAsync(BankWorld w, long accountId)
    {
        await using var db = await w.T.System.CreateDbContextAsync();
        return await db.PortfolioChartPoints.AsNoTracking().Where(p => p.AccountId == accountId).OrderBy(p => p.Timestamp).ThenBy(p => p.Id).ToListAsync();
    }

    private static async Task<Dictionary<long, string>> HoldingNamesAsync(BankWorld w, long accountId)
    {
        await using var db = await w.T.System.CreateDbContextAsync();
        return await db.Holdings.AsNoTracking().Where(h => h.AccountId == accountId).ToDictionaryAsync(h => h.Id, h => h.Name!);
    }

    private static long Noon(DateTime day) => new DateTimeOffset(day.Date, TimeSpan.Zero).ToUnixTimeSeconds() + 43200;

    [Fact]
    public async Task First_sync_writes_the_total_and_a_point_per_security_for_today()
    {
        var (w, _, id) = await SetupAsync();
        await using var _w = w;
        var accountId = await w.For(BankWorld.Alice).CreateAccountAsync((await w.RemoteAsync(BankWorld.Alice, id, DepotBank.DepotId)).Id);

        await SyncAsync(w, id);

        var points = await PointsAsync(w, accountId);
        Assert.Equal(3, points.Count);
        Assert.All(points, p => Assert.Equal(Noon(Day1), p.Timestamp));
        Assert.All(points, p => Assert.Equal("EUR", p.Currency));
        Assert.Equal(1200, Assert.Single(points, p => p.SecurityId == null).Balance);
        var names = await HoldingNamesAsync(w, accountId);
        var bySecurity = points.Where(p => p.SecurityId != null).ToDictionary(p => names[p.SecurityId!.Value], p => p.Balance, StringComparer.Ordinal);
        Assert.Equal(1000, bySecurity["Example Equity Fund"]);
        Assert.Equal(200, bySecurity["Example Bond Fund"]);
    }

    [Fact]
    public async Task A_second_sync_the_same_day_replaces_the_days_points_and_the_next_day_adds_one()
    {
        var (w, bank, id) = await SetupAsync();
        await using var _w = w;
        var accountId = await w.For(BankWorld.Alice).CreateAccountAsync((await w.RemoteAsync(BankWorld.Alice, id, DepotBank.DepotId)).Id);
        await SyncAsync(w, id);

        bank.Positions = [new("Example Equity Fund", "XX0000000011", null, null, 10m, 110m, "EUR", 1100m)];
        w.Clock.Set(Day1.AddHours(3));
        await SyncAsync(w, id);
        var points = await PointsAsync(w, accountId);
        Assert.Equal(2, points.Count);
        Assert.Equal(1100, Assert.Single(points, p => p.SecurityId == null).Balance);
        Assert.Equal(1100, Assert.Single(points, p => p.SecurityId != null).Balance);

        bank.Positions = [new("Example Equity Fund", "XX0000000011", null, null, 10m, 120m, "EUR", 1200m)];
        w.Clock.Set(Day1.AddDays(1));
        await SyncAsync(w, id);
        var totals = (await PointsAsync(w, accountId)).Where(p => p.SecurityId == null).ToList();
        Assert.Equal([Noon(Day1), Noon(Day1.AddDays(1))], totals.Select(p => p.Timestamp));
        Assert.Equal([1100d, 1200d], totals.Select(p => p.Balance!.Value));
    }

    [Fact]
    public async Task A_failed_refresh_writes_no_points()
    {
        var (w, bank, id) = await SetupAsync();
        await using var _w = w;
        var accountId = await w.For(BankWorld.Alice).CreateAccountAsync((await w.RemoteAsync(BankWorld.Alice, id, DepotBank.DepotId)).Id);
        bank.Fail = true;

        await SyncAsync(w, id);

        Assert.Empty(await PointsAsync(w, accountId));
    }

    [Fact]
    public async Task Imported_history_of_an_earlier_day_stays_and_a_depot_awaiting_its_switch_gets_no_points()
    {
        var (w, _, id) = await SetupAsync();
        await using var _w = w;
        var snapshot = w.BuildSnapshot([("MM Depot", null)], [], depots: ["MM Depot"]);
        await w.ImportMoneyMoneyAsync(snapshot);
        var mmDepot = RowIds.Namespace(BankWorld.SourceAlice, 1);
        var earlier = Noon(Day1.AddDays(-30));
        await using (var db = await w.T.System.CreateDbContextAsync())
        {
            db.PortfolioChartPoints.Add(new PortfolioChartPoint
            {
                Id = RowIds.Namespace(BankWorld.SourceAlice, 1),
                OwnerUserId = BankWorld.Alice,
                AccountId = mmDepot,
                Currency = "EUR",
                Timestamp = earlier,
                Balance = 900,
            });
            await db.SaveChangesAsync();
        }
        var remote = await w.RemoteAsync(BankWorld.Alice, id, DepotBank.DepotId);
        await w.For(BankWorld.Alice).LinkAsync(remote.Id, mmDepot);

        await SyncAsync(w, id);
        Assert.Equal([earlier], (await PointsAsync(w, mmDepot)).Select(p => p.Timestamp));

        await w.SwitchAsync(BankWorld.Alice, remote.Id);
        await SyncAsync(w, id);

        var totals = (await PointsAsync(w, mmDepot)).Where(p => p.SecurityId == null).ToList();
        Assert.Equal([earlier, Noon(Day1)], totals.Select(p => p.Timestamp));
        Assert.Equal([900d, 1200d], totals.Select(p => p.Balance!.Value));
    }

    [Fact]
    public async Task Accounts_owned_by_portfolio_performance_or_moneymoney_get_no_points()
    {
        var (w, _, _) = await SetupAsync();
        await using var _w = w;
        await using var db = await w.T.System.CreateDbContextAsync();
        var refresh = new AccountRefresh(500m, [], [new RemotePosition("Example Equity Fund", null, null, null, 1m, 500m, "EUR", 500m)]);
        foreach (var (provider, n) in new[] { (AccountProviders.PortfolioPerformance, 50L), (AccountProviders.MoneyMoney, 51L) })
        {
            var account = new Account { Id = RowIds.Namespace(BankWorld.SourceAlice, n), OwnerUserId = BankWorld.Alice, Name = provider, Type = 7, Portfolio = 1, Currency = "EUR", Provider = provider };
            db.Accounts.Add(account);
            await db.SaveChangesAsync();
            await DepotHistoryStore.RecordAsync(db, account, refresh, DateOnly.FromDateTime(Day1), CancellationToken.None);
            Assert.False(await db.PortfolioChartPoints.AnyAsync(p => p.AccountId == account.Id));
        }
    }
}
