#pragma warning disable CA2000 // test objects live for the test
using Microsoft.EntityFrameworkCore;
using OpenBudget.Connectors.FakeBank;
using OpenBudget.Connectors.Sync;
using OpenBudget.Models;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests.Connectors;

/// <summary>The Documents view: per-user unread state, visibility and the download settings.</summary>
public sealed class DocumentReadTests
{
    private static DocumentService Docs(BankWorld w, string user)
    {
        var access = new AccessScopeProvider(w.T.System, new FixedCurrentUser(user), w.T.Notifier);
        return new DocumentService(new ScopedDbContextFactory(w.T.System, access), w.T.System, access, w.Registry);
    }

    /// <summary>Alice's fake-bank login with statements on the giro account; project 1 holds the giro account and is shared with Bob.</summary>
    private static async Task<(BankWorld World, int LoginId, long GiroAccount)> SetupAsync()
    {
        var w = await BankWorld.CreateAsync();
        var id = await w.AddLoginAsync(BankWorld.Alice, "demo+statements");
        await w.SyncAsync(BankWorld.Alice, id, new SyncRequest(id, false, false));
        await w.LinkAllAsync(BankWorld.Alice, id);
        var giro = await w.RemoteAsync(BankWorld.Alice, id, FakeBankConnector.GiroId);
        await w.For(BankWorld.Alice).SetStatementsAsync(giro.Id, true, StatementsInterval.Daily);
        await w.SyncAsync(BankWorld.Alice, id);

        var account = await w.AccountAsync(giro.LinkedAccountId!.Value);
        w.T.Db.Projects.Add(new Project { Id = 1, Name = "Shared", SourceId = account.SourceId, OwnerUserId = BankWorld.Alice, CreatedAt = DateTime.UtcNow });
        w.T.Db.ProjectAccountAssignments.Add(new ProjectAccountAssignment { ProjectId = 1, AccountId = account.Id, Scope = AccountScope.Exclusive });
        w.T.Db.ProjectShares.Add(new ProjectShare { ProjectId = 1, UserId = BankWorld.Bob, Role = ProjectRole.Viewer });
        await w.T.Db.SaveChangesAsync();
        return (w, id, account.Id);
    }

    [Fact]
    public async Task Two_users_have_separate_unread_state()
    {
        var (w, _, giro) = await SetupAsync();
        await using var _w = w;
        var alice = Docs(w, BankWorld.Alice);
        var bob = Docs(w, BankWorld.Bob);

        Assert.Equal(11, await alice.GetUnreadCountAsync(1)); // ten statements and the tax certificate
        Assert.Equal(10, await bob.GetUnreadCountAsync(1));

        var first = (await alice.GetDocumentsAsync(1)).First(d => d.AccountId == giro);
        await alice.MarkReadAsync([first.Id]);
        Assert.Equal(10, await alice.GetUnreadCountAsync(1));
        Assert.False((await alice.GetDocumentsAsync(1)).Single(d => d.Id == first.Id).Unread);
        Assert.Equal(10, await bob.GetUnreadCountAsync(1));
        Assert.True((await bob.GetDocumentsAsync(1)).Single(d => d.Id == first.Id).Unread);

        await bob.MarkReadAsync((await bob.GetDocumentsAsync(1)).Select(d => d.Id).ToList());
        Assert.Equal(0, await bob.GetUnreadCountAsync(1));
        Assert.Equal(10, await alice.GetUnreadCountAsync(1));
    }

    [Fact]
    public async Task A_shared_viewer_sees_the_accounts_statements_but_never_the_owners_account_less_documents()
    {
        var (w, _, giro) = await SetupAsync();
        await using var _w = w;
        var alice = Docs(w, BankWorld.Alice);
        var bob = Docs(w, BankWorld.Bob);

        var alices = await alice.GetDocumentsAsync(1);
        var accountLess = alices.Single(d => d.AccountId == null);
        Assert.Equal("Fake", accountLess.AccountName); // the bank login's name

        var bobs = await bob.GetDocumentsAsync(1);
        Assert.Equal(10, bobs.Count);
        Assert.All(bobs, d => Assert.Equal(giro, d.AccountId));
        Assert.DoesNotContain(bobs, d => d.Id == accountLess.Id);

        // Marking something he cannot see does nothing (and Carol, without a share, sees nothing).
        await bob.MarkReadAsync([accountLess.Id]);
        Assert.Empty(await w.T.Db.StatementReads.Where(r => r.UserId == BankWorld.Bob).ToListAsync());
        Assert.Empty(await Docs(w, BankWorld.Carol).GetDocumentsAsync(1));
        Assert.Equal(0, await Docs(w, BankWorld.Carol).GetUnreadCountAsync(1));
    }

    [Fact]
    public async Task Documents_downloaded_before_the_user_existed_count_as_read()
    {
        var (w, _, _) = await SetupAsync();
        await using var _w = w;
        var bobUser = await w.T.Db.Users.SingleAsync(u => u.Id == BankWorld.Bob);
        bobUser.CreatedAt = DateTime.UtcNow.AddMinutes(1);
        await w.T.Db.SaveChangesAsync();

        Assert.Equal(0, await Docs(w, BankWorld.Bob).GetUnreadCountAsync(1));
        Assert.All(await Docs(w, BankWorld.Bob).GetDocumentsAsync(1), d => Assert.False(d.Unread));
    }

    [Fact]
    public async Task Marking_unread_brings_documents_back_even_old_ones_and_only_for_that_user()
    {
        var (w, _, _) = await SetupAsync();
        await using var _w = w;
        var alice = Docs(w, BankWorld.Alice);
        var bob = Docs(w, BankWorld.Bob);
        var all = (await alice.GetDocumentsAsync(1)).Select(d => d.Id).ToList();
        await alice.MarkReadAsync(all);
        Assert.Equal(0, await alice.GetUnreadCountAsync(1));

        var two = all.Take(2).ToList();
        await alice.MarkUnreadAsync(two);
        Assert.Equal(2, await alice.GetUnreadCountAsync(1));
        Assert.Equal(two.ToHashSet(), (await alice.GetDocumentsAsync(1)).Where(d => d.Unread).Select(d => d.Id).ToHashSet());
        Assert.Equal(10, await bob.GetUnreadCountAsync(1));

        await alice.MarkReadAsync([two[0]]);
        Assert.Equal(1, await alice.GetUnreadCountAsync(1));

        // A document older than the user counts as read, but an explicit "mark as unread" still applies.
        var bobUser = await w.T.Db.Users.SingleAsync(u => u.Id == BankWorld.Bob);
        bobUser.CreatedAt = DateTime.UtcNow.AddMinutes(1);
        await w.T.Db.SaveChangesAsync();
        Assert.Equal(0, await bob.GetUnreadCountAsync(1));
        var bobs = (await bob.GetDocumentsAsync(1)).Select(d => d.Id).ToList();
        await bob.MarkUnreadAsync([bobs[0], all.Single(id => !bobs.Contains(id))]); // the second is not his to see
        Assert.Equal(1, await bob.GetUnreadCountAsync(1));
        Assert.True((await bob.GetDocumentsAsync(1)).Single(d => d.Id == bobs[0]).Unread);
        Assert.Single(await w.T.Db.StatementReads.Where(r => r.UserId == BankWorld.Bob).ToListAsync());
    }

    [Fact]
    public async Task Settings_list_every_account_and_only_the_owner_may_change_them()
    {
        var (w, _, giro) = await SetupAsync();
        await using var _w = w;
        w.T.Db.Accounts.Add(new Account { Id = 9001, SourceId = BankWorld.SourceAlice, Name = "Cash box", Provider = AccountProviders.Manual });
        w.T.Db.Accounts.Add(new Account { Id = 9002, SourceId = BankWorld.SourceAlice, Name = "Imported", Provider = AccountProviders.MoneyMoney });
        w.T.Db.ProjectAccountAssignments.Add(new ProjectAccountAssignment { ProjectId = 1, AccountId = 9001 });
        w.T.Db.ProjectAccountAssignments.Add(new ProjectAccountAssignment { ProjectId = 1, AccountId = 9002 });
        await w.T.Db.SaveChangesAsync();

        var settings = await Docs(w, BankWorld.Alice).GetSettingsAsync(1);
        Assert.Equal(3, settings.Count);
        var g = settings.Single(s => s.AccountId == giro);
        Assert.Null(g.Block);
        Assert.True(g.Enabled);
        Assert.True(g.CanChange);
        Assert.Equal(StatementBlock.Manual, settings.Single(s => s.AccountId == 9001).Block);
        Assert.Equal(StatementBlock.MoneyMoney, settings.Single(s => s.AccountId == 9002).Block);

        var bobs = await Docs(w, BankWorld.Bob).GetSettingsAsync(1);
        var bg = bobs.Single(s => s.AccountId == giro);
        Assert.True(bg.Enabled);
        Assert.False(bg.CanChange);
        await Assert.ThrowsAsync<AccessDeniedException>(() => w.For(BankWorld.Bob).SetStatementsAsync(bg.LoginAccountId!.Value, false, StatementsInterval.Monthly));
    }

    [Fact]
    public async Task Documents_without_account_or_login_show_their_bank_and_their_owner_assigns_them_in_bulk()
    {
        var (w, _, giro) = await SetupAsync();
        await using var _w = w;
        var store = new BankStatementStore(new BankStorageOptions(Path.Combine(Path.GetTempPath(), $"ob-assign-{Guid.NewGuid():N}")));
        var ids = new List<long>();
        for (var i = 1; i <= 2; i++)
        {
            var row = await store.StoreAsync(w.T.Db, BankWorld.Alice, null, "import", null,
                new OpenBudget.Connectors.RemoteStatement($"mm:{i}", null, new DateOnly(2021, i, 28), $"Statement {i}", System.Text.Encoding.ASCII.GetBytes($"%PDF-1.4 {i}")), CancellationToken.None, "Example Bank");
            ids.Add(row.Id);
        }
        var alice = Docs(w, BankWorld.Alice);
        var bob = Docs(w, BankWorld.Bob);

        var labelled = (await alice.GetDocumentsAsync(1)).Where(d => ids.Contains(d.Id)).ToList();
        Assert.Equal(2, labelled.Count);
        Assert.All(labelled, d => Assert.Equal(("Example Bank", "Example Bank", null, true), (d.AccountName, d.Institution, d.AccountId, d.CanAssign)));
        Assert.DoesNotContain(await bob.GetDocumentsAsync(1), d => ids.Contains(d.Id));

        // A viewer may neither assign to the shared account nor touch the owner's documents.
        Assert.Empty(await bob.GetAssignableAccountsAsync(1));
        await Assert.ThrowsAsync<AccessDeniedException>(() => bob.AssignAsync(ids, giro));

        Assert.Equal([giro], (await alice.GetAssignableAccountsAsync(1)).Select(a => a.Id));
        Assert.Equal(2, await alice.AssignAsync(ids, giro));
        var assigned = await w.T.Db.BankStatements.AsNoTracking().Where(s => ids.Contains(s.Id)).ToListAsync();
        Assert.All(assigned, s => Assert.Equal(giro, s.AccountId));
        var bobs = (await bob.GetDocumentsAsync(1)).Where(d => ids.Contains(d.Id)).ToList();
        Assert.Equal(2, bobs.Count);
        Assert.All(bobs, d => Assert.False(d.CanAssign));
    }

    private static async Task<long> ImportAsync(BankWorld w, string owner, long? accountId, int n)
    {
        var store = new BankStatementStore(new BankStorageOptions(Path.Combine(Path.GetTempPath(), $"ob-assign-{Guid.NewGuid():N}")));
        var account = accountId is { } id ? await w.T.Db.Accounts.SingleAsync(a => a.Id == id) : null;
        var row = await store.StoreAsync(w.T.Db, owner, null, "import", account,
            new OpenBudget.Connectors.RemoteStatement($"mm:x{n}", null, new DateOnly(2022, 1, n), $"Statement {n}", System.Text.Encoding.ASCII.GetBytes($"%PDF-1.4 x{n}")), CancellationToken.None, "Example Bank");
        return row.Id;
    }

    /// <summary>
    /// Alice's imported documents: one without account, one on an account in no project, one on a closed account of
    /// project 1 and one on the giro account; project 2 (Alice's, not shared) holds a further open account.
    /// </summary>
    private static async Task<(BankWorld World, long Giro, long NoAccount, long Orphan, long OnClosed, long OnGiro)> SetupUnassignedAsync()
    {
        var (w, _, giro) = await SetupAsync();
        w.T.Db.Accounts.Add(new Account { Id = 9101, SourceId = BankWorld.SourceAlice, Name = "Depot", Provider = AccountProviders.Manual });
        w.T.Db.Accounts.Add(new Account { Id = 9102, SourceId = BankWorld.SourceAlice, Name = "Old savings", Provider = AccountProviders.Manual, Closed = true });
        w.T.Db.Accounts.Add(new Account { Id = 9103, SourceId = BankWorld.SourceAlice, Name = "Orphan", Provider = AccountProviders.Manual });
        w.T.Db.Projects.Add(new Project { Id = 2, Name = "Second", SourceId = BankWorld.SourceAlice, OwnerUserId = BankWorld.Alice, CreatedAt = DateTime.UtcNow });
        w.T.Db.ProjectAccountAssignments.Add(new ProjectAccountAssignment { ProjectId = 2, AccountId = 9101 });
        w.T.Db.ProjectAccountAssignments.Add(new ProjectAccountAssignment { ProjectId = 1, AccountId = 9102 });
        await w.T.Db.SaveChangesAsync();
        var noAccount = await ImportAsync(w, BankWorld.Alice, null, 1);
        var orphan = await ImportAsync(w, BankWorld.Alice, 9103, 2);
        var onClosed = await ImportAsync(w, BankWorld.Alice, 9102, 3);
        var onGiro = await ImportAsync(w, BankWorld.Alice, giro, 4);
        return (w, giro, noAccount, orphan, onClosed, onGiro);
    }

    [Fact]
    public async Task Documents_to_assign_are_the_owners_documents_without_an_account_in_a_project()
    {
        var (w, giro, noAccount, orphan, onClosed, onGiro) = await SetupUnassignedAsync();
        await using var _w = w;
        var alice = Docs(w, BankWorld.Alice);

        var list = await alice.GetUnassignedAsync();
        var ids = list.Select(d => d.Id).ToHashSet();
        Assert.Contains(noAccount, ids);
        Assert.Contains(orphan, ids);
        Assert.DoesNotContain(onClosed, ids); // a closed account in a project still shows its documents
        Assert.DoesNotContain(onGiro, ids);
        Assert.DoesNotContain(list, d => d.AccountId == giro);
        Assert.Equal(list.Count, await alice.GetUnassignedCountAsync());
        Assert.Equal("Example Bank", list.Single(d => d.Id == noAccount).AccountName);
        Assert.Equal("Orphan", list.Single(d => d.Id == orphan).AccountName);
        Assert.All(list, d => Assert.True(d.CanAssign));
    }

    [Fact]
    public async Task Other_users_never_see_documents_to_assign_of_sources_they_do_not_own()
    {
        var (w, giro, noAccount, orphan, onClosed, _) = await SetupUnassignedAsync();
        await using var _w = w;
        var bob = Docs(w, BankWorld.Bob);

        Assert.Empty(await bob.GetUnassignedAsync());
        Assert.Equal(0, await bob.GetUnassignedCountAsync());
        Assert.Empty(await bob.GetAssignTargetsAsync(true)); // a viewer of project 1 may edit none of its accounts
        await Assert.ThrowsAsync<AccessDeniedException>(() => bob.AssignAsync([noAccount, orphan, onClosed], giro));
    }

    [Fact]
    public async Task Assign_targets_span_all_projects_and_assigning_moves_documents_into_another_project()
    {
        var (w, giro, noAccount, orphan, onClosed, _) = await SetupUnassignedAsync();
        await using var _w = w;
        var alice = Docs(w, BankWorld.Alice);

        var open = await alice.GetAssignTargetsAsync(false);
        Assert.Contains(open, t => t.ProjectId == 1 && t.AccountId == giro);
        Assert.Contains(open, t => t.ProjectId == 2 && t.AccountId == 9101 && t.ProjectName == "Second" && t.Icon.Length > 0);
        Assert.DoesNotContain(open, t => t.AccountId == 9102);
        Assert.DoesNotContain(open, t => t.AccountId == 9103); // in no project
        Assert.Contains(await alice.GetAssignTargetsAsync(true), t => t.AccountId == 9102 && t.Closed);

        Assert.DoesNotContain(orphan, (await alice.GetDocumentsAsync(2)).Select(d => d.Id));
        var before = await alice.GetUnassignedCountAsync();
        Assert.Equal(2, await alice.AssignAsync([noAccount, orphan], 9101));
        Assert.Equal(before - 2, await alice.GetUnassignedCountAsync());
        var second = (await alice.GetDocumentsAsync(2)).Where(d => d.AccountId == 9101).Select(d => d.Id).ToList();
        Assert.Contains(noAccount, second);
        Assert.Contains(orphan, second);

        // A closed account takes documents like an open one; they then show under it in its project.
        Assert.Equal(1, await alice.AssignAsync([orphan], 9102));
        Assert.Equal(before - 2, await alice.GetUnassignedCountAsync());
        var first = (await alice.GetDocumentsAsync(1)).Where(d => d.AccountId == 9102).Select(d => d.Id).ToList();
        Assert.Contains(orphan, first);
        Assert.Contains(onClosed, first);
    }
}
