#pragma warning disable CA2000 // test objects live for the test
using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests;

/// <summary>
/// Editing category / note / check mark: manual flag, bulk edits with one undo, read-only MoneyMoney rows, and who may do
/// what (owner, project editor, project viewer, strangers) - enforced by the service and by the SaveChanges write guard.
/// </summary>
public sealed class TransactionEditingTests
{
    private const long Tx1 = 1;   // alice's Shared account (connector-owned in these tests), expense on SharedLeaf
    private const long Tx2 = 2;   // alice's private account (connector-owned), not shared
    private const long Tx3 = 3;   // property account: still MoneyMoney-owned, visible to bob through the parent share
    private const long Tx4 = 4;   // bob's own account

    private static readonly long OtherLeaf = AccessWorld.Id(AccessWorld.SourceAlice, 201);
    private static readonly long BobLeaf = AccessWorld.Id(AccessWorld.SourceBob, 200);

    private static async Task<TestDb> WorldAsync(ProjectRole? bobRole = ProjectRole.Editor)
    {
        var t = await AccessWorld.BuildAsync(bobRole);
        await using (var db = await t.System.CreateDbContextAsync())
        {
            foreach (var id in new[] { AccessWorld.AccountWp, AccessWorld.AccountPrivate, AccessWorld.AccountBob })
                (await db.Accounts.SingleAsync(a => a.Id == id)).Provider = "fake";
            db.Categories.Add(new Category { Id = OtherLeaf, SourceId = AccessWorld.SourceAlice, Name = "Other leaf", GroupId = AccessWorld.SharedGroup, Type = 1 });
            db.Categories.Add(new Category { Id = AccessWorld.Id(AccessWorld.SourceAlice, 202), SourceId = AccessWorld.SourceAlice, Name = "", GroupId = AccessWorld.SharedGroup, Type = 1 });
            // Two more rows of the same payee on the shared account, one already by hand.
            db.Transactions.Add(Row(10, AccessWorld.AccountWp, -500, "Payee Shared"));
            db.Transactions.Add(Row(11, AccessWorld.AccountWp, -600, "Payee Shared"));
            await db.SaveChangesAsync();
        }
        return t;
    }

    private static Transaction Row(long id, long account, long cents, string payee) => new()
    {
        Id = id,
        SourceId = AccessWorld.SourceAlice,
        AccountId = account,
        Amount = cents,
        Name = payee,
        Timestamp = new DateTimeOffset(2026, 3, 11, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(),
    };

    private static TransactionEditService EditFor(TestDb t, string? user) =>
        new(t.ForUser(user).Factory, t.Notifier, new ChangeOrigin());

    private static async Task<Transaction> LoadAsync(TestDb t, long id)
    {
        await using var db = await t.System.CreateDbContextAsync();
        return await db.Transactions.AsNoTracking().SingleAsync(x => x.Id == id);
    }

    // ---- the manual flag ----

    [Fact]
    public async Task Categorizing_by_hand_marks_the_row_manual_and_stamps_when()
    {
        await using var t = await WorldAsync();
        var result = await EditFor(t, AccessWorld.Alice).SetCategoryAsync([Tx1], OtherLeaf);

        Assert.Equal(1, result.Changed);
        var row = await LoadAsync(t, Tx1);
        Assert.Equal(OtherLeaf, row.CategoryId);
        Assert.Equal(TransactionCategorySource.Manual, row.CategorySetBy);
        Assert.Null(row.RuleId);
    }

    [Fact]
    public async Task Removing_a_category_is_a_manual_choice_too()
    {
        await using var t = await WorldAsync();
        await EditFor(t, AccessWorld.Alice).SetCategoryAsync([Tx1], null);

        var row = await LoadAsync(t, Tx1);
        Assert.Null(row.CategoryId);
        Assert.Equal(TransactionCategorySource.Manual, row.CategorySetBy);
    }

    [Fact]
    public async Task A_rule_set_row_becomes_manual_when_the_user_picks_a_category()
    {
        await using var t = await WorldAsync();
        await using (var db = await t.System.CreateDbContextAsync())
        {
            var row = await db.Transactions.SingleAsync(x => x.Id == Tx1);
            row.CategorySetBy = TransactionCategorySource.Rule;
            row.RuleId = 7;
            await db.SaveChangesAsync();
        }
        await EditFor(t, AccessWorld.Alice).SetCategoryAsync([Tx1], OtherLeaf);

        var after = await LoadAsync(t, Tx1);
        Assert.Equal(TransactionCategorySource.Manual, after.CategorySetBy);
        Assert.Null(after.RuleId);
    }

    [Fact]
    public async Task Categories_must_be_assignable_and_belong_to_the_same_source()
    {
        await using var t = await WorldAsync();
        var edit = EditFor(t, AccessWorld.Alice);
        await Assert.ThrowsAsync<InvalidOperationException>(() => edit.SetCategoryAsync([Tx1], BobLeaf));                                   // another source (and invisible)
        await Assert.ThrowsAsync<InvalidOperationException>(() => edit.SetCategoryAsync([Tx1], AccessWorld.Id(AccessWorld.SourceAlice, 202))); // unnamed sentinel
        await Assert.ThrowsAsync<InvalidOperationException>(() => edit.SetCategoryAsync([Tx1], 424242));                                    // unknown
        Assert.Equal(AccessWorld.SharedLeaf, (await LoadAsync(t, Tx1)).CategoryId);
    }

    // ---- read-only MoneyMoney rows ----

    [Fact]
    public async Task Moneymoney_owned_rows_are_refused_server_side_even_for_the_owner()
    {
        await using var t = await WorldAsync();
        var edit = EditFor(t, AccessWorld.Alice);

        await Assert.ThrowsAsync<AccessDeniedException>(() => edit.SetCategoryAsync([Tx3], OtherLeaf));
        await Assert.ThrowsAsync<AccessDeniedException>(() => edit.SetNoteAsync([Tx3], "x"));
        await Assert.ThrowsAsync<AccessDeniedException>(() => edit.SetCheckedAsync([Tx3], true));

        var row = await LoadAsync(t, Tx3);
        Assert.Equal(AccessWorld.SharedLeaf, row.CategoryId);
        Assert.Equal(TransactionCategorySource.Import, row.CategorySetBy);
        Assert.False(row.Checked);
        Assert.Null(row.Note);
    }

    [Fact]
    public async Task The_write_guard_itself_refuses_edits_of_moneymoney_rows()
    {
        await using var t = await WorldAsync();
        var factory = t.ForUser(AccessWorld.Alice).Factory;
        await using var db = await factory.CreateDbContextAsync();
        (await db.Transactions.SingleAsync(x => x.Id == Tx3)).CategoryId = OtherLeaf;
        await Assert.ThrowsAsync<AccessDeniedException>(() => db.SaveChangesAsync());

        // ... while the same change on a connector-owned row passes.
        await using var ok = await factory.CreateDbContextAsync();
        (await ok.Transactions.SingleAsync(x => x.Id == Tx1)).CategoryId = OtherLeaf;
        await ok.SaveChangesAsync();
    }

    [Fact]
    public async Task A_mixed_selection_edits_what_it_can_and_skips_the_moneymoney_rows()
    {
        await using var t = await WorldAsync();
        var result = await EditFor(t, AccessWorld.Alice).SetCategoryAsync([Tx1, Tx2, Tx3], OtherLeaf);

        Assert.Equal(2, result.Changed);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(OtherLeaf, (await LoadAsync(t, Tx1)).CategoryId);
        Assert.Equal(AccessWorld.SharedLeaf, (await LoadAsync(t, Tx3)).CategoryId);
    }

    [Fact]
    public async Task Bank_fields_can_never_be_changed_through_a_scoped_context()
    {
        await using var t = await WorldAsync();
        foreach (var user in new[] { AccessWorld.Alice, AccessWorld.Bob })
        {
            await using var db = await t.ForUser(user).Factory.CreateDbContextAsync();
            var row = await db.Transactions.SingleAsync(x => x.Id == Tx1);
            row.Amount = -1;
            row.Name = "Renamed";
            await Assert.ThrowsAsync<AccessDeniedException>(() => db.SaveChangesAsync());
        }
        Assert.Equal(AccessWorld.SharedCents, (await LoadAsync(t, Tx1)).Amount);
    }

    // ---- bulk edits and undo ----

    [Fact]
    public async Task A_bulk_category_edit_is_one_undo_step()
    {
        await using var t = await WorldAsync();
        var edit = EditFor(t, AccessWorld.Alice);
        // Row 11 already has a hand-picked category; undo must bring back exactly that.
        await edit.SetCategoryAsync([11], OtherLeaf);
        var before11 = await LoadAsync(t, 11);

        var result = await edit.SetCategoryAsync([Tx1, Tx2, 10, 11], AccessWorld.SharedLeaf);
        Assert.Equal(4, result.Changed);                                  // Tx1/Tx2/10 become manual, 11 changes category
        Assert.NotNull(result.Receipt);

        var undone = await edit.UndoAsync(result.Receipt);
        Assert.Equal(result.Changed, undone);
        var after11 = await LoadAsync(t, 11);
        Assert.Equal(before11.CategoryId, after11.CategoryId);
        Assert.Equal(before11.CategorySetBy, after11.CategorySetBy);
        var tx1 = await LoadAsync(t, Tx1);
        Assert.Equal(TransactionCategorySource.Import, tx1.CategorySetBy);
    }

    [Fact]
    public async Task Bulk_note_and_check_mark_edits_undo_as_one_step_each()
    {
        await using var t = await WorldAsync();
        var edit = EditFor(t, AccessWorld.Alice);

        var note = await edit.SetNoteAsync([Tx1, Tx2], "  reimbursed  ");
        Assert.Equal(2, note.Changed);
        Assert.Equal("reimbursed", (await LoadAsync(t, Tx1)).Note);
        await edit.UndoAsync(note.Receipt!);
        Assert.Null((await LoadAsync(t, Tx1)).Note);
        Assert.Null((await LoadAsync(t, Tx2)).Note);

        var check = await edit.SetCheckedAsync([Tx1, Tx2], true);
        Assert.True((await LoadAsync(t, Tx2)).Checked);
        await edit.UndoAsync(check.Receipt!);
        Assert.False((await LoadAsync(t, Tx1)).Checked);
        Assert.False((await LoadAsync(t, Tx2)).Checked);
    }

    [Fact]
    public async Task An_empty_note_removes_the_note_and_an_unchanged_edit_reports_nothing_changed()
    {
        await using var t = await WorldAsync();
        var edit = EditFor(t, AccessWorld.Alice);
        await edit.SetNoteAsync([Tx1], "x");
        await edit.SetNoteAsync([Tx1], "   ");
        Assert.Null((await LoadAsync(t, Tx1)).Note);

        var again = await edit.SetNoteAsync([Tx1], null);
        Assert.Equal(0, again.Changed);
        Assert.Null(again.Receipt);
    }

    [Fact]
    public async Task Toggling_the_check_mark_checks_everything_unless_everything_is_checked()
    {
        await using var t = await WorldAsync();
        var edit = EditFor(t, AccessWorld.Alice);
        await edit.SetCheckedAsync([Tx1], true);

        await edit.ToggleCheckedAsync([Tx1, Tx2]);                 // mixed -> all checked
        Assert.True((await LoadAsync(t, Tx1)).Checked);
        Assert.True((await LoadAsync(t, Tx2)).Checked);
        await edit.ToggleCheckedAsync([Tx1, Tx2]);                 // all checked -> all unchecked
        Assert.False((await LoadAsync(t, Tx1)).Checked);
        Assert.False((await LoadAsync(t, Tx2)).Checked);
    }

    [Fact]
    public async Task Mark_all_as_checked_covers_the_whole_filter_and_undoes_in_one_step()
    {
        await using var t = await WorldAsync();
        var edit = EditFor(t, AccessWorld.Alice);
        await edit.SetCheckedAsync([10], true);
        var query = new TransactionQuery { SourceId = AccessWorld.SourceAlice, AccountIds = [AccessWorld.AccountWp], Take = 1 };

        var result = await edit.MarkAllCheckedAsync(query);

        Assert.Equal(2, result.Changed);                           // Tx1 and 11; 10 was already checked
        Assert.True((await LoadAsync(t, Tx1)).Checked);
        Assert.True((await LoadAsync(t, 11)).Checked);
        Assert.False((await LoadAsync(t, Tx2)).Checked);           // another account: outside the filter
        await edit.UndoAsync(result.Receipt!);
        Assert.False((await LoadAsync(t, Tx1)).Checked);
        Assert.True((await LoadAsync(t, 10)).Checked);
    }

    // ---- who may edit ----

    [Fact]
    public async Task A_project_editor_may_edit_rows_of_the_accounts_in_the_shared_project()
    {
        await using var t = await WorldAsync(ProjectRole.Editor);
        var edit = EditFor(t, AccessWorld.Bob);

        Assert.Equal(1, (await edit.SetCategoryAsync([Tx1], OtherLeaf)).Changed);
        Assert.Equal(1, (await edit.SetNoteAsync([Tx1], "from bob")).Changed);
        Assert.Equal(1, (await edit.SetCheckedAsync([Tx1], true)).Changed);

        var row = await LoadAsync(t, Tx1);
        Assert.Equal(OtherLeaf, row.CategoryId);
        Assert.Equal("from bob", row.Note);
        Assert.True(row.Checked);
        Assert.Equal(TransactionCategorySource.Manual, row.CategorySetBy);
    }

    [Fact]
    public async Task A_project_editor_cannot_touch_accounts_outside_the_shared_project_or_moneymoney_rows()
    {
        await using var t = await WorldAsync(ProjectRole.Editor);
        var edit = EditFor(t, AccessWorld.Bob);

        await Assert.ThrowsAsync<AccessDeniedException>(() => edit.SetCategoryAsync([Tx2], OtherLeaf));   // alice's private account: invisible
        await Assert.ThrowsAsync<AccessDeniedException>(() => edit.SetNoteAsync([Tx3], "x"));             // visible, but MoneyMoney-owned
        await Assert.ThrowsAsync<InvalidOperationException>(() => edit.SetCategoryAsync([Tx4], OtherLeaf)); // bob's own row, but alice's category is not his
        Assert.Equal(AccessWorld.SharedLeaf, (await LoadAsync(t, Tx2)).CategoryId);
    }

    [Fact]
    public async Task A_project_viewer_is_read_only()
    {
        await using var t = await WorldAsync(ProjectRole.Viewer);
        var edit = EditFor(t, AccessWorld.Bob);

        await Assert.ThrowsAsync<AccessDeniedException>(() => edit.SetCategoryAsync([Tx1], OtherLeaf));
        await Assert.ThrowsAsync<AccessDeniedException>(() => edit.SetNoteAsync([Tx1], "x"));
        await Assert.ThrowsAsync<AccessDeniedException>(() => edit.SetCheckedAsync([Tx1], true));

        // The write guard agrees when someone bypasses the service.
        await using var db = await t.ForUser(AccessWorld.Bob).Factory.CreateDbContextAsync();
        (await db.Transactions.SingleAsync(x => x.Id == Tx1)).Note = "sneaky";
        await Assert.ThrowsAsync<AccessDeniedException>(() => db.SaveChangesAsync());
        Assert.Null((await LoadAsync(t, Tx1)).Note);
    }

    [Fact]
    public async Task Strangers_and_logged_out_users_cannot_edit_and_the_owner_can_edit_all_connector_rows_of_the_source()
    {
        await using var t = await WorldAsync(bobRole: null);
        await Assert.ThrowsAsync<AccessDeniedException>(() => EditFor(t, AccessWorld.Bob).SetNoteAsync([Tx1], "x"));
        await Assert.ThrowsAsync<AccessDeniedException>(() => EditFor(t, null).SetNoteAsync([Tx1], "x"));
        Assert.Equal(2, (await EditFor(t, AccessWorld.Alice).SetNoteAsync([Tx1, Tx2], "ok")).Changed);
    }

    [Fact]
    public async Task Undo_is_subject_to_the_same_permission_checks()
    {
        await using var t = await WorldAsync(ProjectRole.Editor);
        var result = await EditFor(t, AccessWorld.Alice).SetNoteAsync([Tx1], "x");

        // Bob may undo on the shared account (he could have made the edit) - a viewer could not.
        await using var viewerWorld = await WorldAsync(ProjectRole.Viewer);
        var receipt = await EditFor(viewerWorld, AccessWorld.Alice).SetNoteAsync([Tx1], "x");
        Assert.Equal(0, await EditFor(viewerWorld, AccessWorld.Bob).UndoAsync(receipt.Receipt!));
        Assert.Equal("x", (await LoadAsync(viewerWorld, Tx1)).Note);
        Assert.Equal(1, await EditFor(t, AccessWorld.Bob).UndoAsync(result.Receipt!));
    }

    // ---- the "new" marker ----

    [Fact]
    public async Task Viewing_clears_the_new_marker_and_so_does_any_edit()
    {
        await using var t = await WorldAsync();
        await using (var db = await t.System.CreateDbContextAsync())
        {
            foreach (var id in new long[] { Tx1, Tx2, 10 }) (await db.Transactions.SingleAsync(x => x.Id == id)).IsNew = true;
            await db.SaveChangesAsync();
        }
        var edit = EditFor(t, AccessWorld.Alice);

        await edit.MarkViewedAsync([Tx1]);
        Assert.False((await LoadAsync(t, Tx1)).IsNew);
        Assert.True((await LoadAsync(t, Tx2)).IsNew);

        var note = await edit.SetNoteAsync([Tx2], "seen");
        Assert.False((await LoadAsync(t, Tx2)).IsNew);
        await edit.UndoAsync(note.Receipt!);                           // undo restores the marker
        Assert.True((await LoadAsync(t, Tx2)).IsNew);

        await edit.SetCheckedAsync([10], true);
        Assert.False((await LoadAsync(t, 10)).IsNew);
    }

    [Fact]
    public async Task Viewing_by_someone_who_may_not_edit_changes_nothing()
    {
        await using var t = await WorldAsync(ProjectRole.Viewer);
        await using (var db = await t.System.CreateDbContextAsync())
        {
            (await db.Transactions.SingleAsync(x => x.Id == Tx1)).IsNew = true;
            await db.SaveChangesAsync();
        }
        await EditFor(t, AccessWorld.Bob).MarkViewedAsync([Tx1]);
        Assert.True((await LoadAsync(t, Tx1)).IsNew);
    }

    // ---- what the list shows about editability ----

    [Fact]
    public async Task Rows_report_why_they_are_not_editable()
    {
        await using var t = await WorldAsync(ProjectRole.Viewer);
        var page = await t.ForUser(AccessWorld.Bob).TransactionQueries.QueryAsync(new TransactionQuery { SourceId = AccessWorld.SourceAlice });
        Assert.Equal(EditBlock.NoPermission, page.Rows.First(r => r.Id == Tx1).EditBlock);
        Assert.Equal(EditBlock.MoneyMoney, page.Rows.First(r => r.Id == Tx3).EditBlock);

        var owner = await t.ForUser(AccessWorld.Alice).TransactionQueries.QueryAsync(new TransactionQuery { SourceId = AccessWorld.SourceAlice });
        Assert.Equal(EditBlock.None, owner.Rows.First(r => r.Id == Tx1).EditBlock);
        Assert.True(owner.Rows.First(r => r.Id == Tx1).CanEdit);
        Assert.Equal(EditBlock.MoneyMoney, owner.Rows.First(r => r.Id == Tx3).EditBlock);
    }
}
