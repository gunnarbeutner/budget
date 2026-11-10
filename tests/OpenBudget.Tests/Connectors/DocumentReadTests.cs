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
}
