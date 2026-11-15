#pragma warning disable CA2000 // test objects live for the test
using Microsoft.EntityFrameworkCore;
using OpenBudget.Connectors;
using OpenBudget.Connectors.Sync;
using Xunit;

namespace OpenBudget.Tests.Connectors;

/// <summary>How far an order source's history read got: stored by the sync runner and shown on the login's card.</summary>
public sealed class OrderHistoryProgressTests
{
    private static readonly DateTime Today = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>A shop whose payment list read stops at <see cref="StopAt"/> (null: reaches the requested day).</summary>
    private sealed class Shop
    {
        public const string Type = "exampleshop";

        public DateOnly? StopAt { get; set; }

        public ConnectorRegistration Registration => new(Type, "Example Shop", "Test shop.", _ => new Connector(this)) { Orders = new OrderSourceCapabilities(OpenBudget.Connectors.Amazon.AmazonReceiptStore.Store, _ => false, id => new Uri("https://shop.example/order/" + id)), ReceiptShop = OpenBudget.Models.ReceiptShops.Amazon };

        private sealed class Connector(Shop shop) : IBankConnector, IOrderSource
        {
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
                DateOnly since, IReadOnlySet<string> knownOrderIds, IReadOnlyList<string> unreadOrderIds, ChallengeAnswer? answer, CancellationToken ct) =>
                Task.FromResult(ConnectorStep.Done(new OrderSourceResult([], [], [], shop.StopAt ?? since)));
        }
    }

    [Fact]
    public async Task IncompleteReadStoresHowFarItGotUntilTheHistoryIsComplete()
    {
        var shop = new Shop { StopAt = new DateOnly(2026, 8, 24) };
        await using var world = await BankWorld.CreateAsync(extras: new BankWorldExtras { Registrations = [shop.Registration] });
        world.Clock.Set(Today);
        var service = world.For(BankWorld.Alice);
        var login = await service.CreateLoginAsync(Shop.Type, "Shop", "user", "secret");
        Assert.False((await service.GetLoginAsync(login.Id))!.OrderHistoryIncomplete);

        Assert.True((await world.SyncAsync(BankWorld.Alice, login.Id)).Succeeded);
        var view = (await service.GetLoginAsync(login.Id))!;
        Assert.True(view.OrderHistoryIncomplete);
        Assert.Equal(new DateOnly(2026, 8, 24), view.OrderHistoryReadBackTo);

        shop.StopAt = null;
        Assert.True((await world.SyncAsync(BankWorld.Alice, login.Id)).Succeeded);
        view = (await service.GetLoginAsync(login.Id))!;
        Assert.False(view.OrderHistoryIncomplete);
        Assert.Null(view.OrderHistoryReadBackTo);
        await using var db = await world.T.System.CreateDbContextAsync();
        var stored = await db.BankLogins.AsNoTracking().SingleAsync(l => l.Id == login.Id);
        Assert.NotNull(stored.OrderHistoryCompleteAt);
        Assert.Null(stored.OrderHistoryReadBackTo);
    }
}
