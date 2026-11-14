using Microsoft.EntityFrameworkCore;
using OpenBudget.Tests.Connectors;
using OpenBudget.Connectors.FakeBank;
using OpenBudget.Connectors.Sync;
using OpenBudget.Models;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests;

/// <summary>
/// The per-user "default project for new accounts": setting/clearing, the unshared-only rule (and its
/// automatic clearing on sharing), automatic assignment of newly appearing accounts, and the one-time
/// "assign all accounts that are in no project yet" helper.
/// </summary>
public sealed class DefaultProjectTests
{
    private const string Alice = BankWorld.Alice;
    private const string Bob = BankWorld.Bob;
    private const string Carol = BankWorld.Carol;

    private static string Snapshot(BankWorld w, int accounts) =>
        w.BuildSnapshot(Enumerable.Range(1, accounts).Select(i => ($"Example Account {i}", (string?)null)), []);

    private static long Account(int row) => RowIds.Namespace(BankWorld.SourceAlice, row);

    private static async Task<int> NewProjectAsync(BankWorld w, string user, string name)
        => (await w.View(user).Projects.CreateProjectAsync(name)).Id;

    private static async Task<Dictionary<long, int>> AssignmentsAsync(BankWorld w)
    {
        await using var db = await w.T.System.CreateDbContextAsync();
        return await db.ProjectAccountAssignments.AsNoTracking().ToDictionaryAsync(a => a.AccountId, a => a.ProjectId);
    }

    private static async Task<Project> LoadAsync(BankWorld w, int projectId)
    {
        await using var db = await w.T.System.CreateDbContextAsync();
        return await db.Projects.AsNoTracking().SingleAsync(p => p.Id == projectId);
    }

    // ---- setting and clearing ----

    [Fact]
    public async Task Users_without_a_project_get_one_that_takes_their_accounts()
    {
        await using var w = await BankWorld.CreateAsync();
        await w.ImportMoneyMoneyAsync(Snapshot(w, 1));
        var bobsOwn = await NewProjectAsync(w, Bob, "Bob's");

        Assert.Equal(3, await UserDefaultProject.EnsureAllAsync(w.T.System));   // alice, carol and the default user; bob owns one
        Assert.Equal(0, await UserDefaultProject.EnsureAllAsync(w.T.System));

        await using (var db = await w.T.System.CreateDbContextAsync())
        {
            var alices = await db.Projects.SingleAsync(p => p.OwnerUserId == Alice);
            Assert.Equal(("alice", true), (alices.Name, alices.IsDefaultForNewAccounts));
            Assert.Equal(alices.Id, (await AssignmentsAsync(w))[Account(1)]);
            var carols = await db.Projects.SingleAsync(p => p.OwnerUserId == Carol);
            Assert.Equal("carol", carols.Name);
            Assert.Equal(bobsOwn, (await db.Projects.SingleAsync(p => p.OwnerUserId == Bob)).Id);
        }
    }

    [Fact]
    public async Task Default_can_be_set_and_cleared_and_only_one_per_owner_exists()
    {
        await using var w = await BankWorld.CreateAsync();
        var alice = w.View(Alice);
        var a = await NewProjectAsync(w, Alice, "Household");
        var b = await NewProjectAsync(w, Alice, "Holidays");

        await alice.Projects.SetDefaultForNewAccountsAsync(a, true);
        Assert.True((await LoadAsync(w, a)).IsDefaultForNewAccounts);

        await alice.Projects.SetDefaultForNewAccountsAsync(b, true);
        Assert.False((await LoadAsync(w, a)).IsDefaultForNewAccounts);
        Assert.True((await LoadAsync(w, b)).IsDefaultForNewAccounts);
        Assert.True((await alice.Projects.GetDefaultStateAsync(b))!.IsDefault);
        Assert.False((await alice.Projects.GetDefaultStateAsync(a))!.IsDefault);

        await alice.Projects.SetDefaultForNewAccountsAsync(b, false);
        Assert.False((await LoadAsync(w, b)).IsDefaultForNewAccounts);
    }

    [Fact]
    public async Task Each_user_has_an_independent_default()
    {
        await using var w = await BankWorld.CreateAsync();
        var a = await NewProjectAsync(w, Alice, "Household");
        var b = await NewProjectAsync(w, Bob, "Budget");
        await w.View(Alice).Projects.SetDefaultForNewAccountsAsync(a, true);
        await w.View(Bob).Projects.SetDefaultForNewAccountsAsync(b, true);
        Assert.True((await LoadAsync(w, a)).IsDefaultForNewAccounts);
        Assert.True((await LoadAsync(w, b)).IsDefaultForNewAccounts);
    }

    [Fact]
    public async Task Another_user_cannot_set_clear_or_see_the_default()
    {
        await using var w = await BankWorld.CreateAsync();
        var a = await NewProjectAsync(w, Alice, "Household");
        await w.View(Alice).Projects.SetDefaultForNewAccountsAsync(a, true);
        var bob = w.View(Bob);

        await Assert.ThrowsAsync<AccessDeniedException>(() => bob.Projects.SetDefaultForNewAccountsAsync(a, true));
        await Assert.ThrowsAsync<AccessDeniedException>(() => bob.Projects.SetDefaultForNewAccountsAsync(a, false));
        Assert.Null(await bob.Projects.GetDefaultStateAsync(a));
        Assert.True((await LoadAsync(w, a)).IsDefaultForNewAccounts);
    }

    [Fact]
    public async Task A_sharee_cannot_set_the_default_either()
    {
        await using var w = await BankWorld.CreateAsync();
        var a = await NewProjectAsync(w, Alice, "Household");
        await w.View(Alice).Sharing.SetShareAsync(a, Bob, ProjectRole.Editor);
        var bob = w.View(Bob);
        await Assert.ThrowsAsync<AccessDeniedException>(() => bob.Projects.SetDefaultForNewAccountsAsync(a, true));
        Assert.Null(await bob.Projects.GetDefaultStateAsync(a));
    }

    // ---- shared projects ----

    [Fact]
    public async Task A_shared_project_is_refused_as_default()
    {
        await using var w = await BankWorld.CreateAsync();
        var alice = w.View(Alice);
        var a = await NewProjectAsync(w, Alice, "Household");
        await alice.Sharing.SetShareAsync(a, Bob, ProjectRole.Viewer);

        await Assert.ThrowsAsync<InvalidOperationException>(() => alice.Projects.SetDefaultForNewAccountsAsync(a, true));
        Assert.False((await LoadAsync(w, a)).IsDefaultForNewAccounts);
        Assert.True((await alice.Projects.GetDefaultStateAsync(a))!.IsShared);

        // Revoking the share makes it eligible again.
        await alice.Sharing.RevokeShareAsync(a, Bob);
        await alice.Projects.SetDefaultForNewAccountsAsync(a, true);
        Assert.True((await LoadAsync(w, a)).IsDefaultForNewAccounts);
    }

    [Fact]
    public async Task A_sub_project_of_a_shared_project_is_refused_as_default()
    {
        await using var w = await BankWorld.CreateAsync();
        var alice = w.View(Alice);
        var parent = await NewProjectAsync(w, Alice, "Household");
        var child = await NewProjectAsync(w, Alice, "Garden");
        await alice.Projects.SetProjectParentAsync(child, parent);
        await alice.Sharing.SetShareAsync(parent, Bob, ProjectRole.Viewer);

        await Assert.ThrowsAsync<InvalidOperationException>(() => alice.Projects.SetDefaultForNewAccountsAsync(child, true));
    }

    [Fact]
    public async Task Sharing_the_default_project_clears_it_and_leaves_a_notice_that_can_be_dismissed()
    {
        await using var w = await BankWorld.CreateAsync();
        var alice = w.View(Alice);
        var a = await NewProjectAsync(w, Alice, "Household");
        await alice.Projects.SetDefaultForNewAccountsAsync(a, true);

        await alice.Sharing.SetShareAsync(a, Bob, ProjectRole.Viewer);

        var state = (await alice.Projects.GetDefaultStateAsync(a))!;
        Assert.False(state.IsDefault);
        Assert.True(state.ClearedNotice);

        await alice.Projects.DismissDefaultClearedNoticeAsync(a);
        Assert.False((await alice.Projects.GetDefaultStateAsync(a))!.ClearedNotice);

        // And a new account no longer lands in it.
        await w.ImportMoneyMoneyAsync(Snapshot(w, 1));
        Assert.Empty(await AssignmentsAsync(w));
    }

    [Fact]
    public async Task Sharing_a_parent_clears_the_default_of_its_sub_project()
    {
        await using var w = await BankWorld.CreateAsync();
        var alice = w.View(Alice);
        var parent = await NewProjectAsync(w, Alice, "Household");
        var child = await NewProjectAsync(w, Alice, "Garden");
        await alice.Projects.SetProjectParentAsync(child, parent);
        await alice.Projects.SetDefaultForNewAccountsAsync(child, true);

        await alice.Sharing.SetShareAsync(parent, Bob, ProjectRole.Viewer);

        var loaded = await LoadAsync(w, child);
        Assert.False(loaded.IsDefaultForNewAccounts);
        Assert.True(loaded.DefaultClearedNotice);
    }

    [Fact]
    public async Task Moving_the_default_under_a_shared_parent_clears_it()
    {
        await using var w = await BankWorld.CreateAsync();
        var alice = w.View(Alice);
        var parent = await NewProjectAsync(w, Alice, "Household");
        var other = await NewProjectAsync(w, Alice, "Garden");
        await alice.Sharing.SetShareAsync(parent, Bob, ProjectRole.Viewer);
        await alice.Projects.SetDefaultForNewAccountsAsync(other, true);

        await alice.Projects.SetProjectParentAsync(other, parent);

        Assert.False((await LoadAsync(w, other)).IsDefaultForNewAccounts);
    }

    // ---- automatic assignment ----

    [Fact]
    public async Task Imported_accounts_go_to_the_default_project()
    {
        await using var w = await BankWorld.CreateAsync();
        var def = await NewProjectAsync(w, Alice, "Household");
        await w.View(Alice).Projects.SetDefaultForNewAccountsAsync(def, true);

        await w.ImportMoneyMoneyAsync(Snapshot(w, 2));

        var assignments = await AssignmentsAsync(w);
        Assert.Equal(2, assignments.Count);
        Assert.Equal(def, assignments[Account(1)]);
        Assert.Equal(def, assignments[Account(2)]);
    }

    [Fact]
    public async Task Without_a_default_project_nothing_is_assigned()
    {
        await using var w = await BankWorld.CreateAsync();
        await NewProjectAsync(w, Alice, "Household");
        await w.ImportMoneyMoneyAsync(Snapshot(w, 3));
        Assert.Empty(await AssignmentsAsync(w));
    }

    [Fact]
    public async Task Another_users_default_does_not_receive_the_accounts()
    {
        await using var w = await BankWorld.CreateAsync();
        var b = await NewProjectAsync(w, Bob, "Budget");
        await w.View(Bob).Projects.SetDefaultForNewAccountsAsync(b, true);
        await w.ImportMoneyMoneyAsync(Snapshot(w, 2));   // alice's source
        Assert.Empty(await AssignmentsAsync(w));
    }

    [Fact]
    public async Task A_connector_created_account_goes_to_the_default_project()
    {
        await using var w = await BankWorld.CreateAsync();
        var alice = w.View(Alice);
        var def = await NewProjectAsync(w, Alice, "Household");
        await alice.Projects.SetDefaultForNewAccountsAsync(def, true);
        var id = await w.AddLoginAsync(Alice, "demo");
        await w.SyncAsync(Alice, id, new SyncRequest(id, false, false));
        var savings = await w.RemoteAsync(Alice, id, FakeBankConnector.SavingsId);

        var created = await w.For(Alice).CreateAccountAsync(savings.Id);

        Assert.Equal(def, (await AssignmentsAsync(w))[created]);
    }

    [Fact]
    public async Task A_connector_created_account_belongs_to_its_user_and_goes_into_the_default_project()
    {
        await using var w = await BankWorld.CreateAsync();
        await NewProjectAsync(w, Alice, "First");
        var def = await NewProjectAsync(w, Alice, "Household");
        await w.View(Alice).Projects.SetDefaultForNewAccountsAsync(def, true);
        var id = await w.AddLoginAsync(Alice, "demo");
        await w.SyncAsync(Alice, id, new SyncRequest(id, false, false));
        var depot = await w.RemoteAsync(Alice, id, FakeBankConnector.DepotId);

        var created = await w.For(Alice).CreateAccountAsync(depot.Id);

        var account = await w.AccountAsync(created);
        Assert.Equal(Alice, account.OwnerUserId);
        Assert.Equal(FakeBankConnector.TypeName, account.Provider);
        Assert.Equal(def, (await AssignmentsAsync(w))[created]);
    }

    [Fact]
    public async Task A_connector_created_account_of_a_new_user_starts_their_id_space()
    {
        await using var w = await BankWorld.CreateAsync();
        var id = await w.AddLoginAsync(Carol, "demo");
        await w.SyncAsync(Carol, id, new SyncRequest(id, false, false));
        var savings = await w.RemoteAsync(Carol, id, FakeBankConnector.SavingsId);

        var created = await w.For(Carol).CreateAccountAsync(savings.Id);

        var account = await w.AccountAsync(created);
        Assert.Equal(Carol, account.OwnerUserId);
        await using var db = await w.T.System.CreateDbContextAsync();
        var space = (await db.UserDataStates.SingleAsync(s => s.UserId == Carol)).IdSpace;
        Assert.Equal(RowIds.Namespace(space, BankTransactionStore.ConnectorRowBase + 1), account.Id);
    }

    [Fact]
    public async Task A_connector_created_account_is_not_assigned_without_a_default()
    {
        await using var w = await BankWorld.CreateAsync();
        await NewProjectAsync(w, Alice, "Household");
        var id = await w.AddLoginAsync(Alice, "demo");
        await w.SyncAsync(Alice, id, new SyncRequest(id, false, false));
        var savings = await w.RemoteAsync(Alice, id, FakeBankConnector.SavingsId);

        await w.For(Alice).CreateAccountAsync(savings.Id);

        Assert.Empty(await AssignmentsAsync(w));
    }

    // ---- adding several accounts at once ----

    [Fact]
    public async Task Several_accounts_are_added_at_once_skipping_already_assigned_ones_with_one_change()
    {
        await using var w = await BankWorld.CreateAsync();
        var alice = w.View(Alice);
        await w.ImportMoneyMoneyAsync(Snapshot(w, 4));
        var target = await NewProjectAsync(w, Alice, "Household");
        await alice.Spending.AssignAccountToProjectAsync(target, Account(1), AccountScope.Shared);

        var changes = 0;
        w.T.Notifier.Changed += (c, _) => { if (c.Area == DataArea.Projects && c.ProjectId == target) changes++; };

        var added = await alice.Spending.AssignAccountsToProjectAsync(
            target, new[] { Account(1), Account(2), Account(3), Account(3) }, AccountScope.Exclusive);

        Assert.Equal(2, added);
        Assert.Equal(1, changes);
        var rows = await alice.Spending.GetProjectAccountAssignmentsAsync(target);
        Assert.Equal(new[] { Account(1), Account(2), Account(3) }, rows.Select(r => r.AccountId).OrderBy(i => i));
        Assert.Equal(AccountScope.Shared, rows.Single(r => r.AccountId == Account(1)).Scope);   // existing one untouched
        Assert.All(rows.Where(r => r.AccountId != Account(1)), r => Assert.Equal(AccountScope.Exclusive, r.Scope));

        Assert.Equal(0, await alice.Spending.AssignAccountsToProjectAsync(target, new[] { Account(2) }, AccountScope.Shared));
        Assert.Equal(1, changes);   // nothing new, no change raised
    }

    [Fact]
    public async Task A_sharee_cannot_add_several_accounts()
    {
        await using var w = await BankWorld.CreateAsync();
        await w.ImportMoneyMoneyAsync(Snapshot(w, 2));
        var target = await NewProjectAsync(w, Alice, "Household");
        await w.View(Alice).Sharing.SetShareAsync(target, Bob, ProjectRole.Editor);
        await Assert.ThrowsAsync<AccessDeniedException>(() =>
            w.View(Bob).Spending.AssignAccountsToProjectAsync(target, new[] { Account(1), Account(2) }, AccountScope.Shared));
        Assert.Empty(await AssignmentsAsync(w));
    }
}
