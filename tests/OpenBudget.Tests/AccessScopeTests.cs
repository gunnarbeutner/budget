using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests;

/// <summary>
/// The central access rules, exercised through the same user-scoped contexts the app uses: what a
/// user can read (filters) and what a user can write (the SaveChanges guard), for owner, editor and
/// viewer roles. Out-of-scope reads behave as "not found".
/// </summary>
public class AccessScopeTests
{
    private static string[] Names(IEnumerable<Project> projects) => projects.Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();

    // ---- Reads ----

    [Fact]
    public async Task Owner_sees_all_own_projects_and_none_of_the_others()
    {
        await using var t = await AccessWorld.BuildAsync();
        var alice = t.ForUser(AccessWorld.Alice);

        Assert.Equal(new[] { "Alice Private", "Property", "Shared" }, Names(await alice.Projects.GetProjectsAsync()));
        Assert.Null(await alice.Projects.GetProjectAsync(AccessWorld.BobProject));
    }

    [Fact]
    public async Task Editor_sees_only_the_shared_project_and_its_subproject()
    {
        await using var t = await AccessWorld.BuildAsync(ProjectRole.Editor);
        var bob = t.ForUser(AccessWorld.Bob);

        // Shared (shared), Property (inherits the share from its parent) and her own project.
        Assert.Equal(new[] { "Bob Private", "Property", "Shared" }, Names(await bob.Projects.GetProjectsAsync()));
        Assert.Null(await bob.Projects.GetProjectAsync(AccessWorld.Private));
    }

    [Fact]
    public async Task Subproject_inherits_the_parents_share_and_role()
    {
        await using var t = await AccessWorld.BuildAsync(ProjectRole.Viewer);
        var scope = await t.ForUser(AccessWorld.Bob).Access.GetAsync();

        Assert.Equal(ProjectRole.Viewer, scope.RoleFor(AccessWorld.Shared));
        Assert.Equal(ProjectRole.Viewer, scope.RoleFor(AccessWorld.Property));
        Assert.Equal(ProjectRole.None, scope.RoleFor(AccessWorld.Private));
        Assert.Equal(ProjectRole.Owner, scope.RoleFor(AccessWorld.BobProject));
    }

    [Fact]
    public async Task Unshared_world_hides_everything_of_the_other_user()
    {
        await using var t = await AccessWorld.BuildAsync(bobOnWp: null);
        var bob = t.ForUser(AccessWorld.Bob);

        Assert.Equal(new[] { "Bob Private" }, Names(await bob.Projects.GetProjectsAsync()));
        await using var db = await bob.Factory.CreateDbContextAsync();
        Assert.Equal(new[] { AccessWorld.AccountBob }, await db.Accounts.Select(a => a.Id).ToArrayAsync());
        Assert.Empty(await db.Categories.Where(c => c.SourceId == AccessWorld.SourceAlice).ToListAsync());
    }

    [Fact]
    public async Task Shared_user_sees_only_the_accounts_assigned_to_shared_projects()
    {
        await using var t = await AccessWorld.BuildAsync();
        await using var db = await t.ForUser(AccessWorld.Bob).Factory.CreateDbContextAsync();

        var accounts = (await db.Accounts.Select(a => a.Id).ToListAsync()).Order().ToArray();
        // Shared's account and the inherited sub-project's account, plus her own — never the private one.
        Assert.Equal(new[] { AccessWorld.AccountWp, AccessWorld.AccountProperty, AccessWorld.AccountBob }.Order().ToArray(), accounts);

        var transactionIds = (await db.Transactions.Select(x => x.Id).ToListAsync()).Order().ToArray();
        Assert.Equal(new long[] { 1, 3, 4 }, transactionIds);
    }

    [Fact]
    public async Task Owner_sees_every_account_and_transaction_of_the_own_source()
    {
        await using var t = await AccessWorld.BuildAsync();
        await using var db = await t.ForUser(AccessWorld.Alice).Factory.CreateDbContextAsync();

        Assert.Equal(3, await db.Accounts.CountAsync());
        Assert.Equal(new long[] { 1, 2, 3 }, (await db.Transactions.Select(x => x.Id).ToListAsync()).Order().ToArray());
    }

    [Fact]
    public async Task Spending_summary_for_a_shared_project_counts_only_visible_accounts()
    {
        await using var t = await AccessWorld.BuildAsync();

        var owner = await t.ForUser(AccessWorld.Alice).Spending.GetSpendingSummaryAsync(AccessWorld.Shared, 2026, 3, 3);
        var viewer = await t.ForUser(AccessWorld.Bob).Spending.GetSpendingSummaryAsync(AccessWorld.Shared, 2026, 3, 3);

        Assert.Equal(625m, owner.TotalSpent);   // every account of the source, as before
        Assert.Equal(125m, viewer.TotalSpent);  // Shared + property accounts only; the private 500.00 stays hidden
    }

    [Fact]
    public async Task Browse_tree_for_a_shared_project_lists_only_visible_accounts()
    {
        await using var t = await AccessWorld.BuildAsync();

        var tree = await t.ForUser(AccessWorld.Bob).Browse.GetTreeAsync(AccessWorld.Shared, 2026, 3, 3);

        Assert.NotNull(tree);
        var names = tree.Accounts.Select(a => a.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(new[] { "Giro Shared", "Property Account" }, names);
        Assert.DoesNotContain(tree.Accounts, a => a.Id == AccessWorld.AccountPrivate);
    }

    [Fact]
    public async Task Transaction_queries_for_a_shared_project_never_return_hidden_rows()
    {
        await using var t = await AccessWorld.BuildAsync();
        var bob = t.ForUser(AccessWorld.Bob);

        var page = await bob.TransactionQueries.QueryAsync(new TransactionQuery
        {
            SourceId = AccessWorld.SourceAlice,
            Take = 50,
            Period = new TimeRangeFilter(2026, 1, 12),
        });

        Assert.DoesNotContain(page.Rows, r => r.Id == 2);
        Assert.Equal(2, page.TotalCount);
    }

    [Fact]
    public async Task Invisible_budget_reads_as_not_found()
    {
        await using var t = await AccessWorld.BuildAsync();
        int privateBudgetId;
        await using (var db = await t.System.CreateDbContextAsync())
            privateBudgetId = await db.Budgets.Where(b => b.ProjectId == AccessWorld.Private).Select(b => b.Id).SingleAsync();

        var bob = t.ForUser(AccessWorld.Bob);

        Assert.Null(await bob.Entries.GetBudgetAsync(privateBudgetId));
        Assert.DoesNotContain(await bob.Entries.GetBudgetsForProjectAsync(AccessWorld.Private), _ => true);
    }

    [Fact]
    public async Task Logged_out_user_sees_nothing()
    {
        await using var t = await AccessWorld.BuildAsync();
        var nobody = t.ForUser(null);

        Assert.Empty(await nobody.Projects.GetProjectsAsync());
        await using var db = await nobody.Factory.CreateDbContextAsync();
        Assert.Empty(await db.Accounts.ToListAsync());
        Assert.Empty(await db.Transactions.ToListAsync());
        Assert.Empty(await db.ImportSources.ToListAsync());
    }

    [Fact]
    public async Task Unknown_user_id_is_treated_as_logged_out()
    {
        await using var t = await AccessWorld.BuildAsync();
        var ghost = t.ForUser("u-removed");

        Assert.False((await ghost.Access.GetAsync()).IsAuthenticated);
        Assert.Empty(await ghost.Projects.GetProjectsAsync());
    }

    [Fact]
    public async Task Unowned_sources_and_projects_are_invisible_even_to_admins()
    {
        await using var t = await AccessWorld.BuildAsync(bobOnWp: null);
        t.Db.ImportSources.Add(new ImportSource { Id = 9, Name = "orphan" });
        t.AddProject(9, "Orphan", sourceId: 9);
        await t.Db.SaveChangesAsync();

        var admin = t.ForUser(AccessWorld.Alice);   // alice is admin

        Assert.DoesNotContain("Orphan", Names(await admin.Projects.GetProjectsAsync()));
        await using var db = await admin.Factory.CreateDbContextAsync();
        Assert.DoesNotContain(await db.ImportSources.ToListAsync(), s => s.Name == "orphan");
    }

    // ---- Writes ----

    [Fact]
    public async Task Editor_can_edit_the_shared_projects_budgets()
    {
        await using var t = await AccessWorld.BuildAsync(ProjectRole.Editor);
        var bob = t.ForUser(AccessWorld.Bob);

        var created = await bob.Entries.CreateBudgetAsync(AccessWorld.Shared, 2027, "Bob's budget");
        await bob.Entries.SaveBudgetEntriesAsync(created.Id, new[] { (AccessWorld.SharedGroup, CategoryItemType.Group, new decimal[12]) });
        await bob.Entries.SetActiveBudgetAsync(created.Id);
        await bob.Entries.DeleteBudgetAsync(created.Id);

        Assert.DoesNotContain(await bob.Entries.GetBudgetsForProjectAsync(AccessWorld.Shared), b => b.Year == 2027);
    }

    [Fact]
    public async Task Editor_can_change_category_assignments_and_tracking_of_the_shared_project()
    {
        await using var t = await AccessWorld.BuildAsync(ProjectRole.Editor);
        var bob = t.ForUser(AccessWorld.Bob);

        await bob.Projects.RemoveCategoryFromProjectAsync(AccessWorld.Shared, AccessWorld.SharedGroup);
        Assert.DoesNotContain((await bob.Projects.GetProjectAsync(AccessWorld.Shared))!.CategoryAssignments, _ => true);
        await bob.Projects.AssignCategoryToProjectAsync(AccessWorld.Shared, AccessWorld.SharedGroup);
        await bob.Projects.UpdateProjectTrackingItemsAsync(AccessWorld.Shared, new HashSet<long> { AccessWorld.SharedGroup }, new HashSet<long>());
        await bob.Spending.SetCategoryTypeAsync(AccessWorld.Shared, AccessWorld.SharedGroup, CategoryItemType.Group, isIncome: true);

        var shared = (await bob.Projects.GetProjectAsync(AccessWorld.Shared))!;
        Assert.Single(shared.CategoryAssignments);
        Assert.Contains((AccessWorld.SharedGroup, CategoryItemType.Group), await bob.Spending.GetIncomeItemsForProjectAsync(AccessWorld.Shared));
    }

    [Fact]
    public async Task Editor_cannot_change_account_assignments()
    {
        await using var t = await AccessWorld.BuildAsync(ProjectRole.Editor);
        var bob = t.ForUser(AccessWorld.Bob);

        await Assert.ThrowsAsync<AccessDeniedException>(() => bob.Spending.AssignAccountToProjectAsync(AccessWorld.Shared, AccessWorld.AccountPrivate, AccountScope.Shared));
        await Assert.ThrowsAsync<AccessDeniedException>(() => bob.Spending.RemoveAccountFromProjectAsync(AccessWorld.Shared, AccessWorld.AccountWp));
        await Assert.ThrowsAsync<AccessDeniedException>(() => bob.Spending.SetAccountScopeAsync(AccessWorld.Shared, AccessWorld.AccountWp, AccountScope.Informational));

        // Nothing changed: the private account is still hidden from her.
        await using var db = await bob.Factory.CreateDbContextAsync();
        Assert.DoesNotContain(await db.Accounts.ToListAsync(), a => a.Id == AccessWorld.AccountPrivate);
    }

    [Fact]
    public async Task Editor_cannot_change_shares_parent_order_or_delete_the_project()
    {
        await using var t = await AccessWorld.BuildAsync(ProjectRole.Editor);
        var bob = t.ForUser(AccessWorld.Bob);

        await Assert.ThrowsAsync<AccessDeniedException>(() => bob.Sharing.SetShareAsync(AccessWorld.Shared, AccessWorld.Alice, ProjectRole.Viewer));
        await Assert.ThrowsAsync<AccessDeniedException>(() => bob.Sharing.RevokeShareAsync(AccessWorld.Shared, AccessWorld.Bob));
        await Assert.ThrowsAsync<AccessDeniedException>(() => bob.Projects.SetProjectParentAsync(AccessWorld.Property, null));
        await Assert.ThrowsAsync<AccessDeniedException>(() => bob.Projects.DeleteProjectAsync(AccessWorld.Shared));
        Assert.DoesNotContain(await bob.Sharing.GetSharesAsync(AccessWorld.Shared), _ => true);   // she can't even list them
    }

    [Fact]
    public async Task Editor_cannot_pull_a_foreign_project_under_her_own_as_parent()
    {
        await using var t = await AccessWorld.BuildAsync(ProjectRole.Editor);
        var bob = t.ForUser(AccessWorld.Bob);

        // Her own project can't adopt (or be adopted by) a project she only edits: inheriting shares
        // across owners would leak data.
        await Assert.ThrowsAnyAsync<Exception>(() => bob.Projects.SetProjectParentAsync(AccessWorld.BobProject, AccessWorld.Shared));
    }

    [Fact]
    public async Task Viewer_cannot_edit_budgets_server_side()
    {
        await using var t = await AccessWorld.BuildAsync(ProjectRole.Viewer);
        var bob = t.ForUser(AccessWorld.Bob);
        var sharedBudget = (await bob.Entries.GetBudgetsForProjectAsync(AccessWorld.Shared)).Single();
        var inactive = await t.ForUser(AccessWorld.Alice).Entries.CreateBudgetAsync(AccessWorld.Shared, 2026, "Draft", makeActive: false);

        await Assert.ThrowsAsync<AccessDeniedException>(() => bob.Entries.CreateBudgetAsync(AccessWorld.Shared, 2027));
        await Assert.ThrowsAsync<AccessDeniedException>(() => bob.Entries.DeleteBudgetAsync(sharedBudget.Id));
        await Assert.ThrowsAsync<AccessDeniedException>(() => bob.Entries.SetActiveBudgetAsync(inactive.Id));
        await Assert.ThrowsAsync<AccessDeniedException>(() => bob.Entries.SaveBudgetEntriesAsync(sharedBudget.Id, new[] { (AccessWorld.SharedGroup, CategoryItemType.Group, new decimal[12]) }));
        await Assert.ThrowsAsync<AccessDeniedException>(() => bob.Entries.SaveBudgetContributionSplitsAsync(sharedBudget.Id, new[] { (AccessWorld.SharedGroup, CategoryItemType.Group, 50m) }));
        await Assert.ThrowsAsync<AccessDeniedException>(() => bob.Projects.AssignCategoryToProjectAsync(AccessWorld.Shared, AccessWorld.SharedGroup + 1));
        await Assert.ThrowsAsync<AccessDeniedException>(() => bob.Projects.UpdateProjectTrackingItemsAsync(AccessWorld.Shared, new HashSet<long>(), new HashSet<long>()));
        await Assert.ThrowsAsync<AccessDeniedException>(() => bob.Spending.SetCategoryTypeAsync(AccessWorld.Shared, AccessWorld.SharedGroup, CategoryItemType.Group, true));

        // The stored budget is untouched.
        await using var db = await t.System.CreateDbContextAsync();
        Assert.Equal(2, await db.Budgets.CountAsync(b => b.ProjectId == AccessWorld.Shared));
        Assert.False((await db.Budgets.SingleAsync(b => b.Id == inactive.Id)).IsActive);
    }

    [Fact]
    public async Task A_refused_project_delete_does_not_touch_the_callers_own_share()
    {
        await using var t = await AccessWorld.BuildAsync(ProjectRole.Viewer);
        var bob = t.ForUser(AccessWorld.Bob);

        await Assert.ThrowsAsync<AccessDeniedException>(() => bob.Projects.DeleteProjectAsync(AccessWorld.Shared));

        await using var db = await t.System.CreateDbContextAsync();
        Assert.Equal(1, await db.ProjectShares.CountAsync(s => s.ProjectId == AccessWorld.Shared && s.UserId == AccessWorld.Bob));
        Assert.Equal(1, await db.Projects.CountAsync(p => p.Id == AccessWorld.Shared));
    }

    [Fact]
    public async Task Deleting_a_budget_removes_its_entries_and_splits()
    {
        await using var t = await AccessWorld.BuildAsync(ProjectRole.Editor);
        var alice = t.ForUser(AccessWorld.Alice);
        var budget = (await alice.Entries.GetBudgetsForProjectAsync(AccessWorld.Shared)).Single();
        await alice.Entries.SaveBudgetContributionSplitsAsync(budget.Id, new[] { (AccessWorld.SharedGroup, CategoryItemType.Group, 50m) });

        await t.ForUser(AccessWorld.Bob).Entries.DeleteBudgetAsync(budget.Id);   // an editor may delete budgets

        await using var db = await t.System.CreateDbContextAsync();
        Assert.False(await db.Budgets.AnyAsync(b => b.Id == budget.Id));
        Assert.False(await db.MonthlyBudgetEntries.AnyAsync(e => e.BudgetId == budget.Id));
        Assert.False(await db.BudgetContributionSplits.AnyAsync(s => s.BudgetId == budget.Id));
    }

    [Fact]
    public async Task Nobody_can_write_into_a_project_they_cannot_see()
    {
        await using var t = await AccessWorld.BuildAsync();
        var bob = t.ForUser(AccessWorld.Bob);

        await Assert.ThrowsAsync<AccessDeniedException>(() => bob.Entries.CreateBudgetAsync(AccessWorld.Private, 2027));
        await Assert.ThrowsAsync<AccessDeniedException>(() => bob.Projects.AssignCategoryToProjectAsync(AccessWorld.Private, AccessWorld.SharedGroup + 1));
        await Assert.ThrowsAsync<AccessDeniedException>(() => bob.Sharing.SetShareAsync(AccessWorld.Private, AccessWorld.Bob, ProjectRole.Editor));
        await Assert.ThrowsAsync<ArgumentException>(() => bob.Spending.GetSpendingSummaryAsync(AccessWorld.Private, 2026, 3, 3));
    }

    [Fact]
    public async Task Owner_can_share_change_and_revoke()
    {
        await using var t = await AccessWorld.BuildAsync(bobOnWp: null);
        var alice = t.ForUser(AccessWorld.Alice);
        var bob = t.ForUser(AccessWorld.Bob);
        Assert.DoesNotContain("Shared", Names(await bob.Projects.GetProjectsAsync()));

        await alice.Sharing.SetShareAsync(AccessWorld.Shared, AccessWorld.Bob, ProjectRole.Viewer);
        Assert.Equal(ProjectRole.Viewer, (await alice.Sharing.GetSharesAsync(AccessWorld.Shared)).Single().Role);
        await alice.Sharing.SetShareAsync(AccessWorld.Shared, AccessWorld.Bob, ProjectRole.Editor);
        Assert.Equal(ProjectRole.Editor, (await alice.Sharing.GetSharesAsync(AccessWorld.Shared)).Single().Role);

        // Bob's cached scope was dropped by the share notification.
        Assert.Contains("Shared", Names(await bob.Projects.GetProjectsAsync()));

        await alice.Sharing.RevokeShareAsync(AccessWorld.Shared, AccessWorld.Bob);
        Assert.DoesNotContain("Shared", Names(await bob.Projects.GetProjectsAsync()));
    }

    [Fact]
    public async Task Share_validation_rejects_owner_role_and_unknown_users()
    {
        await using var t = await AccessWorld.BuildAsync(bobOnWp: null);
        var alice = t.ForUser(AccessWorld.Alice);

        await Assert.ThrowsAsync<ArgumentException>(() => alice.Sharing.SetShareAsync(AccessWorld.Shared, AccessWorld.Bob, ProjectRole.Owner));
        await Assert.ThrowsAsync<InvalidOperationException>(() => alice.Sharing.SetShareAsync(AccessWorld.Shared, "nobody", ProjectRole.Viewer));
        await Assert.ThrowsAsync<InvalidOperationException>(() => alice.Sharing.SetShareAsync(AccessWorld.Shared, AccessWorld.Alice, ProjectRole.Viewer));
    }

    [Fact]
    public async Task Sharee_sees_only_their_own_share_row()
    {
        await using var t = await AccessWorld.BuildAsync();
        await using var db = await t.ForUser(AccessWorld.Bob).Factory.CreateDbContextAsync();

        var rows = await db.ProjectShares.ToListAsync();

        Assert.Single(rows);
        Assert.Equal(AccessWorld.Bob, rows[0].UserId);
    }

    [Fact]
    public async Task Creating_a_project_requires_owning_the_source_and_makes_the_creator_owner()
    {
        await using var t = await AccessWorld.BuildAsync();
        var bob = t.ForUser(AccessWorld.Bob);

        var mine = await bob.Projects.CreateProjectAsync("Holiday", AccessWorld.SourceBob);
        Assert.Equal(AccessWorld.Bob, mine.OwnerUserId);
        await Assert.ThrowsAsync<AccessDeniedException>(() => bob.Projects.CreateProjectAsync("Sneaky", AccessWorld.SourceAlice));
    }

    // ---- Sources (owner-only) ----

    [Fact]
    public async Task Source_pages_and_tokens_are_owner_only()
    {
        await using var t = await AccessWorld.BuildAsync();
        var bob = t.ForUser(AccessWorld.Bob);

        await Assert.ThrowsAsync<AccessDeniedException>(() => bob.Sources.RotateTokenAsync(AccessWorld.SourceAlice));
        await Assert.ThrowsAsync<AccessDeniedException>(() => bob.Sources.DeleteSourceAsync(AccessWorld.SourceAlice));
        await Assert.ThrowsAsync<AccessDeniedException>(() => bob.DepotMapping.GetAsync(AccessWorld.SourceAlice));
        await Assert.ThrowsAsync<AccessDeniedException>(() => bob.DepotMapping.SaveLinkAsync(AccessWorld.SourceAlice, "x", null));
        await Assert.ThrowsAsync<AccessDeniedException>(() => bob.InstrumentMapping.GetAsync(AccessWorld.SourceAlice));
        await Assert.ThrowsAsync<AccessDeniedException>(() => bob.SecurityTax.GetSettingsAsync(AccessWorld.SourceAlice));
        await Assert.ThrowsAsync<AccessDeniedException>(() => bob.SecurityTax.SetAsync(AccessWorld.SourceAlice, "uuid", 0, 0, null));
        await Assert.ThrowsAsync<AccessDeniedException>(() => bob.Accounts.SetProviderAsync(AccessWorld.AccountWp, "x"));

        var owned = await bob.Sources.GetOwnedSourcesAsync();
        Assert.Equal(new[] { "bob" }, owned.Select(s => s.Name).ToArray());
    }

    [Fact]
    public async Task Owner_can_create_a_source_with_a_token_and_rotate_it()
    {
        await using var t = await AccessWorld.BuildAsync();
        var bob = t.ForUser(AccessWorld.Bob);

        var created = await bob.Sources.CreateSourceAsync("holiday");
        Assert.Equal(AccessWorld.Bob, created.OwnerUserId);
        Assert.False(string.IsNullOrEmpty(created.UploadToken));

        var rotated = await bob.Sources.RotateTokenAsync(created.Id);
        Assert.NotEqual(created.UploadToken, rotated);
        await Assert.ThrowsAsync<ArgumentException>(() => bob.Sources.CreateSourceAsync("Not A Slug"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => bob.Sources.CreateSourceAsync("alice"));   // taken
    }

    [Fact]
    public async Task Deleting_an_owned_source_wipes_its_data()
    {
        await using var t = await AccessWorld.BuildAsync(bobOnWp: null);
        var bob = t.ForUser(AccessWorld.Bob);
        var created = await bob.Sources.CreateSourceAsync("temp");
        Assert.True(await bob.Sources.DeleteSourceAsync(created.Id));
        // A source that still has projects is refused.
        Assert.False(await bob.Sources.DeleteSourceAsync(AccessWorld.SourceBob));
    }

    // ---- Freshness / upload time ----

    [Fact]
    public async Task Viewer_sees_the_last_upload_time_but_only_visible_accounts_of_the_source()
    {
        await using var t = await AccessWorld.BuildAsync(ProjectRole.Viewer);
        var stamp = new DateTime(2026, 3, 11, 8, 0, 0, DateTimeKind.Utc);
        await using (var db = await t.System.CreateDbContextAsync())
        {
            (await db.ImportSources.SingleAsync(s => s.Id == AccessWorld.SourceAlice)).LastUploadAt = stamp;
            await db.SaveChangesAsync();
        }

        var freshness = await t.ForUser(AccessWorld.Bob).Freshness.GetDataFreshnessAsync();
        var source = freshness.Single(f => f.SourceId == AccessWorld.SourceAlice);

        Assert.Equal(stamp, source.DbUploadedUtc);
        Assert.DoesNotContain(source.Accounts, a => a.AccountId == AccessWorld.AccountPrivate);
        Assert.Equal(2, source.Accounts.Count);
    }

    // ---- Change notifications ----

    [Fact]
    public async Task Change_events_outside_the_scope_are_not_visible()
    {
        await using var t = await AccessWorld.BuildAsync();
        var scope = await t.ForUser(AccessWorld.Bob).Access.GetAsync();

        Assert.True(scope.IsChangeVisible(new DataChange(DataArea.Budgets, ProjectId: AccessWorld.Shared)));
        Assert.True(scope.IsChangeVisible(new DataChange(DataArea.ImportedData, SourceId: AccessWorld.SourceAlice)));
        Assert.False(scope.IsChangeVisible(new DataChange(DataArea.Budgets, ProjectId: AccessWorld.Private)));
        Assert.False(scope.IsChangeVisible(new DataChange(DataArea.ImportedData, SourceId: 99)));
        Assert.True(scope.IsChangeVisible(new DataChange(DataArea.Projects)));   // unscoped, id-less structural change
    }

    [Fact]
    public async Task Scope_cache_is_dropped_by_project_and_source_notifications()
    {
        await using var t = await AccessWorld.BuildAsync(bobOnWp: null);
        var bob = t.ForUser(AccessWorld.Bob);
        Assert.False((await bob.Access.GetAsync()).CanView(AccessWorld.Shared));

        await using (var db = await t.System.CreateDbContextAsync())
        {
            db.ProjectShares.Add(new ProjectShare { ProjectId = AccessWorld.Shared, UserId = AccessWorld.Bob, Role = ProjectRole.Viewer });
            await db.SaveChangesAsync();
        }
        Assert.False((await bob.Access.GetAsync()).CanView(AccessWorld.Shared));   // still cached

        t.Notifier.Publish(new DataChange(DataArea.Projects, ProjectId: AccessWorld.Shared));
        Assert.True((await bob.Access.GetAsync()).CanView(AccessWorld.Shared));
    }
}
