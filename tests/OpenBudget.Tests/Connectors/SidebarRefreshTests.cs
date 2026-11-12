#pragma warning disable CA2000 // test objects live for the test
using Microsoft.EntityFrameworkCore;
using OpenBudget.Connectors.FakeBank;
using OpenBudget.Connectors.Sync;
using OpenBudget.Models;
using OpenBudget.Services;
using OpenBudget.Shared;
using Xunit;

namespace OpenBudget.Tests.Connectors;

/// <summary>The sidebar's "Refresh account(s)": the group → logins resolution and the runner's account filter.</summary>
public sealed class SidebarRefreshTests
{
    private static BrowseAccountNode Acc(long id, long? group, int? login = null, int? link = null) =>
        new() { Id = id, Name = "a" + id, GroupId = group, SyncLoginId = login, SyncLoginAccountId = link };

    private static BrowseTree Tree() => new()
    {
        AccountGroups =
        [
            new AccountGroupInfo(1, null, "Banks", 1),
            new AccountGroupInfo(2, 1, "Sub", 1),
            new AccountGroupInfo(3, 2, "SubSub", 1),
            new AccountGroupInfo(4, null, "Cash", 2),
        ],
        Accounts =
        [
            Acc(10, 1, login: 7, link: 70),
            Acc(11, 2, login: 8, link: 80),
            Acc(12, 3, login: 7, link: 71),
            Acc(13, 3),                       // manual
            Acc(14, 4),
            Acc(15, 1, login: 9, link: null), // no longer listed by the bank
        ],
    };

    [Fact]
    public void A_group_resolves_to_one_target_per_login_over_its_subgroups()
    {
        var targets = SidebarBankMenu.Targets(new SidebarNode(SidebarNodeKind.AccountGroup, 1, "Banks"), Tree());
        Assert.Equal([7, 8], targets.Select(t => t.LoginId));
        Assert.Equal([70, 71], targets[0].LoginAccountIds);
        Assert.Equal([80], targets[1].LoginAccountIds);

        var sub = SidebarBankMenu.Targets(new SidebarNode(SidebarNodeKind.AccountGroup, 2, "Sub"), Tree());
        Assert.Equal([(8, 80), (7, 71)], sub.OrderByDescending(t => t.LoginId).Select(t => (t.LoginId, t.LoginAccountIds.Single())));
        Assert.Empty(SidebarBankMenu.Targets(new SidebarNode(SidebarNodeKind.AccountGroup, 4, "Cash"), Tree()));
    }

    [Fact]
    public void An_account_resolves_to_its_own_link()
    {
        var t = Assert.Single(SidebarBankMenu.Targets(new SidebarNode(SidebarNodeKind.Account, 11, "a11"), Tree()));
        Assert.Equal((8, 80), (t.LoginId, t.LoginAccountIds.Single()));
        Assert.Empty(SidebarBankMenu.Targets(new SidebarNode(SidebarNodeKind.Account, 13, "a13"), Tree()));
    }

    [Fact]
    public async Task Refreshing_one_account_only_refreshes_that_account()
    {
        await using var w = await BankWorld.CreateAsync();
        var id = await w.AddLoginAsync(BankWorld.Alice);
        await w.SyncAsync(BankWorld.Alice, id, new SyncRequest(id, false, false));
        await w.LinkAllAsync(BankWorld.Alice, id);
        var giro = await w.RemoteAsync(BankWorld.Alice, id, FakeBankConnector.GiroId);
        var savings = await w.RemoteAsync(BankWorld.Alice, id, FakeBankConnector.SavingsId);
        var lastSync = (await w.For(BankWorld.Alice).GetLoginAsync(id))!.LastSyncAt;

        Assert.Equal(StartResult.Started, await w.For(BankWorld.Alice).RefreshAccountsAsync(id, [giro.Id]));
        var s = await w.WaitAsync(BankWorld.Alice, id, []);
        Assert.True(s.Succeeded, s.Error);
        Assert.NotEmpty(await w.TransactionsAsync(giro.LinkedAccountId!.Value));
        Assert.Empty(await w.TransactionsAsync(savings.LinkedAccountId!.Value));
        // only part of the login was refreshed: its schedule is untouched
        Assert.Equal(lastSync, (await w.For(BankWorld.Alice).GetLoginAsync(id))!.LastSyncAt);

        // the full sync does refresh the other account (the fake bank has bookings for it)
        Assert.True((await w.SyncAsync(BankWorld.Alice, id, new SyncRequest(id, true, false))).Succeeded);
        Assert.NotEmpty(await w.TransactionsAsync(savings.LinkedAccountId!.Value));
    }

    [Fact]
    public async Task Only_the_owner_can_refresh_and_only_linked_accounts_of_that_login()
    {
        await using var w = await BankWorld.CreateAsync();
        var id = await w.AddLoginAsync(BankWorld.Alice);
        await w.SyncAsync(BankWorld.Alice, id, new SyncRequest(id, false, false));
        var giro = await w.RemoteAsync(BankWorld.Alice, id, FakeBankConnector.GiroId);

        Assert.Equal(StartResult.NotFound, await w.Coordinator.RefreshAccountsAsync(BankWorld.Alice, id, [giro.Id])); // not linked yet
        await w.LinkAllAsync(BankWorld.Alice, id);
        Assert.Equal(StartResult.NotFound, await w.Coordinator.RefreshAccountsAsync(BankWorld.Bob, id, [giro.Id]));
        Assert.Equal(StartResult.NothingToDo, await w.Coordinator.RefreshAccountsAsync(BankWorld.Alice, id, []));
        await using var db = await w.T.System.CreateDbContextAsync();
        Assert.Equal(0, await db.Transactions.CountAsync());
    }
}
