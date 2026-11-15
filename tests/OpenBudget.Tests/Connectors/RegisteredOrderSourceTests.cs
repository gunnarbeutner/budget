#pragma warning disable CA2000 // test objects live for the test
using OpenBudget.Connectors;
using OpenBudget.Connectors.Receipts;
using OpenBudget.Models;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests.Connectors;

/// <summary>
/// An order source the app knows nothing about runs through the sync by its registration alone: the runner reads and
/// stores through the store the registration names.
/// </summary>
public sealed class RegisteredOrderSourceTests
{
    private static readonly DateTime Today = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);

    private const string Type = "madeupshop";

    /// <summary>Remembers what the runner asked for and what it stored.</summary>
    private sealed class MemoryStore : IOrderStore
    {
        public List<OrderSourceResult> Applied { get; } = [];

        public HashSet<string> Known { get; } = new(StringComparer.Ordinal) { "K-1" };

        public Task<DateOnly> SinceAsync(BudgetDbContext db, BankLogin login, DateOnly today, int historyYears, CancellationToken ct) =>
            Task.FromResult(today.AddDays(-30));

        public Task<HashSet<string>> KnownOrdersAsync(BudgetDbContext db, int loginId, CancellationToken ct) => Task.FromResult(Known);

        public Task<List<string>> UnreadOrdersAsync(BudgetDbContext db, int loginId, CancellationToken ct) => Task.FromResult(new List<string> { "U-1" });

        public Task ApplyAsync(BudgetDbContext db, BankLogin login, OrderSourceResult result, DateTime nowUtc, ReceiptStore receipts, CancellationToken ct, Action<string>? progress = null)
        {
            Applied.Add(result);
            return Task.CompletedTask;
        }
    }

    private sealed class Connector : IBankConnector, IOrderSource
    {
        public (DateOnly Since, IReadOnlySet<string> Known, IReadOnlyList<string> Unread)? Asked { get; private set; }

        public string ConnectorType => Type;

        public Task<ConnectorStep<ConnectorSession?>> LoginAsync(ConnectorCredentials credentials, ConnectorSession? storedSession, ChallengeAnswer? answer, CancellationToken ct) =>
            Task.FromResult(ConnectorStep.Done<ConnectorSession?>(new ConnectorSession()));

        public Task<IReadOnlyList<RemoteAccount>> ListAccountsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<RemoteAccount>>([]);

        public Task<ConnectorStep<AccountRefresh>> RefreshAsync(RemoteAccount account, DateOnly? since, ChallengeAnswer? answer, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<ConnectorStep<IReadOnlyList<RemoteStatement>>> FetchStatementsAsync(
            IReadOnlyList<RemoteAccount> accounts, IReadOnlySet<string> knownIdentifiers, ChallengeAnswer? answer, CancellationToken ct) =>
            Task.FromResult(ConnectorStep.Done<IReadOnlyList<RemoteStatement>>([]));

        public Task<ConnectorSession?> LogoutAsync(CancellationToken ct) => Task.FromResult<ConnectorSession?>(new ConnectorSession());

        public Task<ConnectorStep<OrderSourceResult>> FetchOrdersAsync(
            DateOnly since, IReadOnlySet<string> knownOrderIds, IReadOnlyList<string> unreadOrderIds, ChallengeAnswer? answer, CancellationToken ct)
        {
            Asked = (since, knownOrderIds, unreadOrderIds);
            var charge = new RemoteCharge("O-7", since.AddDays(3), -12.34m, "EUR", "card-1", "Example Card ••1234", null, null);
            return Task.FromResult(ConnectorStep.Done(new OrderSourceResult(
                [charge], [], [new RemoteAccount("card-1", "Example Card ••1234", RemoteAccountType.CreditCard, null, "EUR")], since)));
        }
    }

    [Fact]
    public async Task MadeUpOrderSourceSyncsThroughItsRegisteredStore()
    {
        var store = new MemoryStore();
        var connector = new Connector();
        var registration = new ConnectorRegistration(Type, "Made-up Shop", "Test shop.", _ => connector, Statements: false)
        {
            Orders = new OrderSourceCapabilities(store, name => name.Contains("voucher", StringComparison.Ordinal), id => new Uri("https://shop.example/orders/" + id)),
            ReceiptShop = Type,
        };
        Assert.Equal(SourceKind.Orders, registration.Kind);

        await using var world = await BankWorld.CreateAsync(extras: new BankWorldExtras { Registrations = [registration] });
        world.Clock.Set(Today);
        var service = world.For(BankWorld.Alice);
        var login = await service.CreateLoginAsync(Type, "Shop", "user", "secret");

        Assert.True((await world.SyncAsync(BankWorld.Alice, login.Id)).Succeeded);

        var asked = connector.Asked!.Value;
        Assert.Equal(new DateOnly(2026, 10, 11), asked.Since);
        Assert.Contains("K-1", asked.Known);
        Assert.Equal(["U-1"], asked.Unread);
        var applied = Assert.Single(store.Applied);
        Assert.Equal("O-7", Assert.Single(applied.Charges).OrderId);
        var view = (await service.GetLoginAsync(login.Id))!;
        Assert.Equal(SourceKind.Orders, view.Kind);
        Assert.Equal(Type, view.ReceiptSource);
        Assert.Equal("card-1", Assert.Single(await service.GetAccountsAsync(login.Id)).RemoteId);
    }
}
