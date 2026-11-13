using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests;

/// <summary>
/// Structure editing from the sidebar (<see cref="StructureService"/>): only the source's owner, only after the takeover;
/// deletes cascade (bookings, statements, rules, project rows), group deletes move children up, moves renumber siblings.
/// </summary>
public sealed class StructureServiceTests
{
    private static async Task<TestDb> WorldAsync(bool takenOver = true)
    {
        var t = await AccessWorld.BuildAsync();
        // MoneyMoney uploaded to the source.
        var source = await t.Db.ImportSources.SingleAsync(s => s.Id == AccessWorld.SourceAlice);
        source.LastUploadAt = DateTime.UtcNow;
        if (takenOver) source.StructureTakenOverAt = DateTime.UtcNow;
        await t.Db.SaveChangesAsync();
        return t;
    }

    private static StructureService For(TestDb t, string user) =>
        new(t.ForUser(user).Factory, t.System, t.Notifier, new ChangeOrigin());

    private static long Id(int row) => AccessWorld.Id(AccessWorld.SourceAlice, row);

    [Fact]
    public async Task Only_the_owner_may_edit_also_a_source_whose_structure_was_never_marked_taken_over()
    {
        await using var t = await WorldAsync(takenOver: false);
        Assert.Null(await For(t, AccessWorld.Alice).GetBlockAsync(AccessWorld.SourceAlice));
        // Bob edits the shared project but does not own the source.
        var bob = For(t, AccessWorld.Bob);
        Assert.Equal(StructureBlock.NotOwner, await bob.GetBlockAsync(AccessWorld.SourceAlice));
        await Assert.ThrowsAsync<AccessDeniedException>(() => bob.RenameAsync(StructureItemKind.Account, AccessWorld.AccountWp, "Mine"));
        await Assert.ThrowsAsync<AccessDeniedException>(() => bob.DeleteCategoryAsync(AccessWorld.SharedLeaf));
        await Assert.ThrowsAsync<AccessDeniedException>(() => bob.DeleteAccountAsync(AccessWorld.AccountWp, "Giro Shared"));

        await For(t, AccessWorld.Alice).RenameAsync(StructureItemKind.Category, AccessWorld.SharedLeaf, "  Example Market ");
        await using var db = await t.System.CreateDbContextAsync();
        Assert.Equal("Example Market", (await db.Categories.SingleAsync(c => c.Id == AccessWorld.SharedLeaf)).Name);
        Assert.Equal("Giro Shared", (await db.Accounts.SingleAsync(a => a.Id == AccessWorld.AccountWp)).Name);
    }

    [Fact]
    public async Task Deleting_a_category_uncategorizes_its_bookings_and_removes_its_rules_and_budget_rows()
    {
        await using var t = await WorldAsync();
        t.TrackCategory(AccessWorld.Shared, AccessWorld.SharedLeaf);
        t.Db.CategoryRules.Add(new CategoryRule { ProjectId = AccessWorld.Shared, CategoryId = AccessWorld.SharedLeaf, Expression = "name:\"Example Market\"" });
        var other = Id(201);
        t.AddLeaf(other, AccessWorld.SharedGroup, "Other", sourceId: AccessWorld.SourceAlice);
        t.Db.CategoryRules.Add(new CategoryRule { ProjectId = AccessWorld.Shared, CategoryId = other, Expression = "Example Shop" });
        t.AddBudget(AccessWorld.Private, 2026, (AccessWorld.SharedLeaf, CategoryItemType.Category, 10m));
        await t.Db.SaveChangesAsync();

        await For(t, AccessWorld.Alice).DeleteCategoryAsync(AccessWorld.SharedLeaf);

        await using var db = await t.System.CreateDbContextAsync();
        Assert.False(await db.Categories.AnyAsync(c => c.Id == AccessWorld.SharedLeaf));
        Assert.Equal(3, await db.Transactions.CountAsync(x => x.SourceId == AccessWorld.SourceAlice && x.CategoryId == null));
        Assert.Equal(["Example Shop"], await db.CategoryRules.Select(r => r.Expression).ToListAsync());
        Assert.False(await db.ProjectTrackingItems.AnyAsync(i => i.CategoryId == AccessWorld.SharedLeaf));
        Assert.False(await db.MonthlyBudgetEntries.AnyAsync(e => e.CategoryId == AccessWorld.SharedLeaf && e.ItemType == CategoryItemType.Category));
        Assert.True(await db.RemovedImportItems.AnyAsync(r => r.ItemId == AccessWorld.SharedLeaf && r.Kind == RemovedImportItem.CategoryKind));
        // Bob's source is untouched.
        Assert.NotNull((await db.Transactions.SingleAsync(x => x.SourceId == AccessWorld.SourceBob)).CategoryId);
    }

    [Fact]
    public async Task Deleting_an_account_needs_its_name_and_takes_bookings_statements_and_assignments()
    {
        await using var t = await WorldAsync();
        var login = new BankLogin { OwnerUserId = AccessWorld.Alice, ConnectorType = "fake", DisplayName = "Example Bank" };
        t.Db.BankLogins.Add(login);
        await t.Db.SaveChangesAsync();
        t.Db.BankLoginAccounts.Add(new BankLoginAccount { BankLoginId = login.Id, RemoteId = "R1", Name = "Giro", LinkedAccountId = AccessWorld.AccountWp, StatementsEnabled = true });
        t.Db.BankStatements.Add(new BankStatement { BankLoginId = login.Id, OwnerUserId = AccessWorld.Alice, AccountId = AccessWorld.AccountWp, SourceId = AccessWorld.SourceAlice, Identifier = "s1", Name = "Statement 1", FilePath = "x/s1.pdf" });
        t.Db.CategoryRules.Add(new CategoryRule { ProjectId = AccessWorld.Shared, CategoryId = AccessWorld.SharedLeaf, Expression = "market AND account_id=" + AccessWorld.AccountWp });
        await t.Db.SaveChangesAsync();
        var alice = For(t, AccessWorld.Alice);

        var info = await alice.GetAccountDeleteInfoAsync(AccessWorld.AccountWp);
        Assert.Equal(new AccountDeleteInfo(AccessWorld.AccountWp, "Giro Shared", 1, 1), info);

        await Assert.ThrowsAsync<ArgumentException>(() => alice.DeleteAccountAsync(AccessWorld.AccountWp, "giro shared"));
        await alice.DeleteAccountAsync(AccessWorld.AccountWp, "Giro Shared");

        await using var db = await t.System.CreateDbContextAsync();
        Assert.False(await db.Accounts.AnyAsync(a => a.Id == AccessWorld.AccountWp));
        Assert.False(await db.Transactions.AnyAsync(x => x.AccountId == AccessWorld.AccountWp));
        Assert.False(await db.BankStatements.AnyAsync());
        Assert.False(await db.ProjectAccountAssignments.AnyAsync(a => a.AccountId == AccessWorld.AccountWp));
        Assert.False(await db.CategoryRules.AnyAsync());
        var link = await db.BankLoginAccounts.SingleAsync();
        Assert.Null(link.LinkedAccountId);
        Assert.False(link.StatementsEnabled);
        Assert.Equal(2, await db.Transactions.CountAsync(x => x.SourceId == AccessWorld.SourceAlice));
    }

    private static async Task AccountGroupsAsync(TestDb t)
    {
        // Banks (10) > Savings box (11) > Giro Private; Giro Shared and Property Account directly in Banks.
        t.Db.AccountGroups.Add(new AccountGroup { Id = Id(10), SourceId = AccessWorld.SourceAlice, Name = "Banks", Sort = 0 });
        t.Db.AccountGroups.Add(new AccountGroup { Id = Id(11), SourceId = AccessWorld.SourceAlice, Name = "Savings box", ParentGroupId = Id(10), Sort = 1 });
        await t.Db.SaveChangesAsync();
        foreach (var (id, group, sort) in new[] { (AccessWorld.AccountWp, 10, 0L), (AccessWorld.AccountProperty, 10, 2L), (AccessWorld.AccountPrivate, 11, 0L) })
        {
            var a = await t.Db.Accounts.SingleAsync(x => x.Id == id);
            a.GroupId = Id(group);
            a.Sort = sort;
        }
        await t.Db.SaveChangesAsync();
    }

    private static async Task<List<(long Id, long? Group, long? Sort)>> AccountsAsync(TestDb t)
    {
        await using var db = await t.System.CreateDbContextAsync();
        return (await db.Accounts.Where(a => a.SourceId == AccessWorld.SourceAlice).OrderBy(a => a.Sort).Select(a => new { a.Id, a.GroupId, a.Sort }).ToListAsync())
            .Select(a => (a.Id, a.GroupId, a.Sort)).ToList();
    }

    [Fact]
    public async Task The_owner_sets_icon_and_colour_of_accounts_and_groups()
    {
        await using var t = await WorldAsync(takenOver: false);
        await AccountGroupsAsync(t);
        var alice = For(t, AccessWorld.Alice);

        await alice.SetAppearanceAsync(StructureItemKind.Account, AccessWorld.AccountWp, "bi-wallet2", "#2B5797");
        await alice.SetAppearanceAsync(StructureItemKind.AccountGroup, Id(10), "bi-safe", null);

        await using (var db = await t.System.CreateDbContextAsync())
        {
            var account = await db.Accounts.SingleAsync(a => a.Id == AccessWorld.AccountWp);
            Assert.Equal(("bi-wallet2", "#2b5797"), (account.Icon, account.Color));
            var group = await db.AccountGroups.SingleAsync(g => g.Id == Id(10));
            Assert.Equal("bi-safe", group.Icon);
            Assert.Null(group.Color);
            // Untouched rows keep the defaults.
            Assert.Null((await db.Accounts.SingleAsync(a => a.Id == AccessWorld.AccountPrivate)).Icon);
            Assert.Null((await db.AccountGroups.SingleAsync(g => g.Id == Id(11))).Icon);
        }

        // Back to the defaults.
        await alice.SetAppearanceAsync(StructureItemKind.Account, AccessWorld.AccountWp, null, null);
        await using (var db = await t.System.CreateDbContextAsync())
        {
            var account = await db.Accounts.SingleAsync(a => a.Id == AccessWorld.AccountWp);
            Assert.Null(account.Icon);
            Assert.Null(account.Color);
        }

        await Assert.ThrowsAsync<ArgumentException>(() => alice.SetAppearanceAsync(StructureItemKind.Account, AccessWorld.AccountWp, "bi-not-offered", null));
        await Assert.ThrowsAsync<ArgumentException>(() => alice.SetAppearanceAsync(StructureItemKind.Account, AccessWorld.AccountWp, null, "red"));
        // Bob edits the shared project but does not own the source.
        await Assert.ThrowsAsync<AccessDeniedException>(() =>
            For(t, AccessWorld.Bob).SetAppearanceAsync(StructureItemKind.Account, AccessWorld.AccountWp, "bi-wallet2", null));
    }

    [Fact]
    public void Accounts_without_a_picked_icon_show_their_type_icon_and_groups_the_folder()
    {
        Assert.Equal("bi-piggy-bank", AccountIcons.ForAccount(null, 2, false));
        Assert.Equal("bi-graph-up-arrow", AccountIcons.ForAccount(null, 1, true));
        Assert.Equal("bi-house", AccountIcons.ForAccount("bi-house", 2, false));
        Assert.Equal("bi-piggy-bank", AccountIcons.ForAccount("bi-unknown", 2, false));
        Assert.Equal("bi-folder", AccountIcons.ForGroup(null));
        Assert.Equal("bi-briefcase", AccountIcons.ForGroup("bi-briefcase"));
        var pill = CategoryColors.For(0, "#1D4ED8");
        Assert.Equal($"color:{pill.Text};--ob-tint-d:{pill.DarkText};", AccountIcons.TintStyle("#1D4ED8"));
        Assert.Null(AccountIcons.TintStyle(null));
    }

    [Fact]
    public void The_palette_has_clearly_distinct_hues_and_old_colours_mark_their_nearest_swatch()
    {
        var keys = CategoryColors.Palette.Select(p => p.Key).ToList();
        Assert.InRange(keys.Count, 10, 12);
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        // Colours stored with the earlier palette (or imported) select the swatch closest to them; the data stays as is.
        Assert.Equal("#2a7330", CategoryColors.NearestSwatch("#1f6b35"));
        Assert.Equal("#b91c1c", CategoryColors.NearestSwatch("#a3332a"));
        Assert.Equal("#1d4ed8", CategoryColors.NearestSwatch("#1d4ed8"));
        Assert.Null(CategoryColors.NearestSwatch("not a color"));
    }

    [Fact]
    public async Task Deleting_an_account_group_moves_its_accounts_up_into_its_place()
    {
        await using var t = await WorldAsync();
        await AccountGroupsAsync(t);

        await For(t, AccessWorld.Alice).DeleteAccountGroupAsync(Id(11));

        var accounts = await AccountsAsync(t);
        Assert.Equal([(AccessWorld.AccountWp, Id(10), 0L), (AccessWorld.AccountPrivate, Id(10), 1L), (AccessWorld.AccountProperty, Id(10), 2L)],
            accounts.Select(a => (a.Id, a.Group, a.Sort ?? -1)).ToList());
        await using var db = await t.System.CreateDbContextAsync();
        Assert.False(await db.AccountGroups.AnyAsync(g => g.Id == Id(11)));
    }

    [Fact]
    public async Task Moves_renumber_siblings_and_refuse_cycles()
    {
        await using var t = await WorldAsync();
        await AccountGroupsAsync(t);
        var alice = For(t, AccessWorld.Alice);

        // Drag Giro Private out of the box to the top of Banks.
        await alice.MoveAsync(StructureItemKind.Account, AccessWorld.AccountPrivate, Id(10), 0);
        var accounts = await AccountsAsync(t);
        Assert.Equal((Id(10), 0L), accounts.Where(a => a.Id == AccessWorld.AccountPrivate).Select(a => (a.Group!.Value, a.Sort!.Value)).Single());
        Assert.Equal(1L, accounts.Single(a => a.Id == AccessWorld.AccountWp).Sort);

        // Move down: Giro Private swaps with Giro Shared.
        Assert.True(await alice.MoveStepAsync(StructureItemKind.Account, AccessWorld.AccountPrivate, 1));
        Assert.Equal((1, 4), await alice.PositionAsync(StructureItemKind.Account, AccessWorld.AccountPrivate));
        Assert.False(await alice.MoveStepAsync(StructureItemKind.AccountGroup, Id(10), -1));

        await Assert.ThrowsAsync<ArgumentException>(() => alice.MoveAsync(StructureItemKind.AccountGroup, Id(10), Id(11), 0));
    }

    [Fact]
    public async Task Categories_and_groups_are_created_in_place_and_only_empty_groups_are_deleted()
    {
        await using var t = await WorldAsync();
        var alice = For(t, AccessWorld.Alice);

        var market = await alice.CreateCategoryAsync(AccessWorld.SharedGroup, "Example Market");
        var first = await alice.CreateCategoryAsync(AccessWorld.SharedGroup, "Example Shop", afterId: AccessWorld.SharedLeaf);
        Assert.NotEqual(market, first);
        Assert.True(market >= SourceScope.Namespace(AccessWorld.SourceAlice, StructureService.OwnRowBase));
        await using (var db = await t.System.CreateDbContextAsync())
        {
            var order = await db.Categories.Where(c => c.GroupId == AccessWorld.SharedGroup).OrderBy(c => c.Sort).Select(c => c.Name).ToListAsync();
            Assert.Equal(["Leaf2", "Example Shop", "Example Market"], order);
        }

        var sub = await alice.CreateCategoryGroupAsync(AccessWorld.SharedGroup, "Household");
        Assert.False(await alice.IsCategoryGroupEmptyAsync(AccessWorld.SharedGroup));
        await Assert.ThrowsAsync<InvalidOperationException>(() => alice.DeleteCategoryGroupAsync(AccessWorld.SharedGroup));

        await alice.MoveAsync(StructureItemKind.Category, market, sub, 0);
        await Assert.ThrowsAsync<InvalidOperationException>(() => alice.DeleteCategoryGroupAsync(sub));
        await alice.MoveAsync(StructureItemKind.Category, market, AccessWorld.SharedGroup, 0);
        Assert.True(await alice.IsCategoryGroupEmptyAsync(sub));
        await alice.DeleteCategoryGroupAsync(sub);
        await Assert.ThrowsAsync<ArgumentException>(() => alice.MoveAsync(StructureItemKind.CategoryGroup, AccessWorld.SharedGroup, AccessWorld.SharedGroup, 0));

        await using var check = await t.System.CreateDbContextAsync();
        Assert.False(await check.CategoryGroups.AnyAsync(g => g.Id == sub));
        Assert.Equal(0L, (await check.Categories.SingleAsync(c => c.Id == market)).Sort);
    }

    [Fact]
    public async Task A_source_moneymoney_never_uploaded_to_is_editable_right_away()
    {
        await using var t = await AccessWorld.BuildAsync();
        Assert.Null(await For(t, AccessWorld.Alice).GetBlockAsync(AccessWorld.SourceAlice));
        var info = await For(t, AccessWorld.Bob).GetEditInfoAsync(AccessWorld.SourceAlice, [AccessWorld.AccountWp, AccessWorld.AccountBob]);
        Assert.Equal(StructureBlock.NotOwner, info.CategoryBlock);
        Assert.Equal(StructureBlock.NotOwner, info.AccountBlock(AccessWorld.AccountWp));
        Assert.Null(info.AccountBlock(AccessWorld.AccountBob));
    }

    [Fact]
    public async Task Drag_and_drop_moves_before_a_row_or_into_a_group()
    {
        await using var t = await WorldAsync();
        await AccountGroupsAsync(t);
        var alice = For(t, AccessWorld.Alice);

        // Property Account dropped on Giro Shared: right before it.
        await alice.MoveToAsync(StructureItemKind.Account, AccessWorld.AccountProperty, StructureItemKind.Account, AccessWorld.AccountWp, into: false);
        Assert.Equal((0, 3), await alice.PositionAsync(StructureItemKind.Account, AccessWorld.AccountProperty));
        // ... and onto the Savings box group: to its end.
        await alice.MoveToAsync(StructureItemKind.Account, AccessWorld.AccountProperty, StructureItemKind.AccountGroup, Id(11), into: true);
        var accounts = await AccountsAsync(t);
        Assert.Equal((Id(11), 1L), accounts.Where(a => a.Id == AccessWorld.AccountProperty).Select(a => (a.Group!.Value, a.Sort!.Value)).Single());
        Assert.Equal(["Savings box", "Banks"], (await alice.ListGroupsAsync(false, AccessWorld.SourceAlice)).Select(g => g.Name).Reverse());

        // A category dropped on an account is ignored.
        await alice.MoveToAsync(StructureItemKind.Category, AccessWorld.SharedLeaf, StructureItemKind.Account, AccessWorld.AccountWp, into: false);
    }

    [Fact]
    public async Task A_new_account_group_lands_next_to_its_sibling()
    {
        await using var t = await WorldAsync();
        await AccountGroupsAsync(t);
        var id = await For(t, AccessWorld.Alice).CreateAccountGroupAsync(Id(11), "Cards");
        await using var db = await t.System.CreateDbContextAsync();
        var g = await db.AccountGroups.SingleAsync(x => x.Id == id);
        Assert.Equal((Id(10), 2L), (g.ParentGroupId!.Value, g.Sort!.Value));
        Assert.Equal(3L, (await db.Accounts.SingleAsync(a => a.Id == AccessWorld.AccountProperty)).Sort);
    }
}
