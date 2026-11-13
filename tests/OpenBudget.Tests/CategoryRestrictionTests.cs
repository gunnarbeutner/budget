#pragma warning disable CA2000 // test objects live for the test
using Microsoft.EntityFrameworkCore;
using OpenBudget.Connectors;
using OpenBudget.Connectors.Sync;
using OpenBudget.Models;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests;

/// <summary>
/// A category's "Restrict to": its rules only categorise bookings of the target account, or of accounts in the target
/// account group and its sub-groups - in "apply to existing", the preview, match counts and the bank-sync hook.
/// </summary>
public sealed class CategoryRestrictionTests
{
    private static readonly long Groceries = AccessWorld.Id(AccessWorld.SourceAlice, 210);
    private static readonly long TopGroup = AccessWorld.Id(AccessWorld.SourceAlice, 300);
    private static readonly long SubGroup = AccessWorld.Id(AccessWorld.SourceAlice, 301);
    private static readonly long OtherGroup = AccessWorld.Id(AccessWorld.SourceAlice, 302);

    // ---- pure ----

    [Fact]
    public void Group_membership_covers_nested_groups()
    {
        var index = new AccountGroupIndex(
            new Dictionary<long, long?> { [10] = 3, [11] = 1, [12] = null },
            new Dictionary<long, long?> { [1] = null, [2] = 1, [3] = 2, [4] = null });
        Assert.Equal([1, 2, 3], index.GroupsOf(10).Order());
        Assert.Equal([1], index.GroupsOf(11));
        Assert.Empty(index.GroupsOf(12));
        Assert.Empty(index.GroupsOf(99));
        Assert.True(CategoryRestrictions.Allows(null, 1, 10, index));
        Assert.True(CategoryRestrictions.Allows(null, 2, 10, index));
        Assert.False(CategoryRestrictions.Allows(null, 2, 11, index));
        Assert.False(CategoryRestrictions.Allows(null, 4, 10, index));
        Assert.True(CategoryRestrictions.Allows(10, null, 10, index));
        Assert.False(CategoryRestrictions.Allows(10, null, 11, index));
        Assert.True(CategoryRestrictions.Allows(null, null, 11, index));
    }

    [Fact]
    public void Group_cycles_do_not_loop()
    {
        var index = new AccountGroupIndex(new Dictionary<long, long?> { [10] = 1 }, new Dictionary<long, long?> { [1] = 2, [2] = 1 });
        Assert.Equal([1, 2], index.GroupsOf(10).Order());
    }

    [Fact]
    public void A_rule_of_a_restricted_category_is_skipped_for_other_accounts()
    {
        var restrictions = new CategoryRestrictions(new Dictionary<long, (long?, long?)> { [1] = (7, null) }, AccountGroupIndex.Empty);
        var restricted = new CategoryRule { Expression = "market", CategoryId = 1, Enabled = true };
        var fallback = new CategoryRule { Expression = "market", CategoryId = 2, Enabled = true };
        Assert.Equal(1, CategoryRuleMatcher.FirstMatch([restricted, fallback], new RuleSubject(7, -100, "Market", null, null), restrictions)!.CategoryId);
        Assert.Equal(2, CategoryRuleMatcher.FirstMatch([restricted, fallback], new RuleSubject(8, -100, "Market", null, null), restrictions)!.CategoryId);
    }

    // ---- service ----

    private static async Task<TestDb> WorldAsync()
    {
        var t = await AccessWorld.BuildAsync();
        await using var db = await t.System.CreateDbContextAsync();
        foreach (var id in new[] { AccessWorld.AccountWp, AccessWorld.AccountPrivate })
            (await db.Accounts.SingleAsync(a => a.Id == id)).Provider = "fake";
        db.AccountGroups.Add(new AccountGroup { Id = TopGroup, OwnerUserId = AccessWorld.Alice, Name = "Everyday" });
        db.AccountGroups.Add(new AccountGroup { Id = SubGroup, OwnerUserId = AccessWorld.Alice, Name = "Cards", ParentGroupId = TopGroup });
        db.AccountGroups.Add(new AccountGroup { Id = OtherGroup, OwnerUserId = AccessWorld.Alice, Name = "Savings" });
        (await db.Accounts.SingleAsync(a => a.Id == AccessWorld.AccountWp)).GroupId = SubGroup;
        (await db.Accounts.SingleAsync(a => a.Id == AccessWorld.AccountPrivate)).GroupId = OtherGroup;
        db.ProjectAccountAssignments.Add(new ProjectAccountAssignment { ProjectId = AccessWorld.Shared, AccountId = AccessWorld.AccountPrivate });
        db.Categories.Add(new Category { Id = Groceries, OwnerUserId = AccessWorld.Alice, Name = "Groceries", GroupId = AccessWorld.SharedGroup, Type = 1 });
        db.Transactions.Add(Row(20, AccessWorld.AccountWp));
        db.Transactions.Add(Row(21, AccessWorld.AccountPrivate));
        await db.SaveChangesAsync();
        return t;
    }

    private static Transaction Row(long id, long account) => new()
    {
        Id = id,
        OwnerUserId = AccessWorld.Alice,
        AccountId = account,
        Amount = -1250,
        Name = "Example Market",
        Timestamp = new DateTimeOffset(2026, 3, 12, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(),
    };

    private static CategoryRuleService Service(TestDb t) => new(t.ForUser(AccessWorld.Alice).Factory, t.Notifier, new ChangeOrigin());

    private static Task<int> AddRuleAsync(CategoryRuleService s, string expression = "name:\"example market\"") =>
        s.SaveAsync(new CategoryRule { ProjectId = AccessWorld.Shared, Expression = expression, CategoryId = Groceries, Enabled = true });

    private static async Task<long?> CategoryOfAsync(TestDb t, long id)
    {
        await using var db = await t.System.CreateDbContextAsync();
        return await db.Transactions.Where(x => x.Id == id).Select(x => x.CategoryId).SingleAsync();
    }

    [Fact]
    public async Task Restricted_to_an_account_only_that_accounts_bookings_are_categorized()
    {
        await using var t = await WorldAsync();
        var s = Service(t);
        await AddRuleAsync(s);
        await s.SetRestrictionAsync(Groceries, AccessWorld.AccountPrivate, null);

        var restriction = await s.GetRestrictionAsync(Groceries);
        Assert.Equal(("Giro Private", true), (restriction.Name, restriction.CanEdit));
        Assert.Equal([21L], await s.PreviewMatchIdsAsync(AccessWorld.Shared, new CategoryRule { CategoryId = Groceries, Expression = "market" }));
        Assert.Equal(1, Assert.Single(await s.GetCategoryRulesAsync(AccessWorld.Shared, Groceries)).MatchCount);
        Assert.Equal(1, await s.ApplyExistingAsync(AccessWorld.Shared));
        Assert.Null(await CategoryOfAsync(t, 20));
        Assert.Equal(Groceries, await CategoryOfAsync(t, 21));
    }

    [Fact]
    public async Task Restricted_to_a_group_bookings_of_accounts_in_its_sub_groups_are_categorized()
    {
        await using var t = await WorldAsync();
        var s = Service(t);
        await AddRuleAsync(s);
        await s.SetRestrictionAsync(Groceries, null, TopGroup);

        Assert.Equal("Everyday", (await s.GetRestrictionAsync(Groceries)).Name);
        Assert.Equal(1, await s.ApplyExistingAsync(AccessWorld.Shared));
        Assert.Equal(Groceries, await CategoryOfAsync(t, 20));
        Assert.Null(await CategoryOfAsync(t, 21));
    }

    [Fact]
    public async Task Clearing_the_restriction_applies_to_all_accounts_again()
    {
        await using var t = await WorldAsync();
        var s = Service(t);
        await AddRuleAsync(s);
        await s.SetRestrictionAsync(Groceries, null, OtherGroup);
        await s.SetRestrictionAsync(Groceries, null, null);
        Assert.Null((await s.GetRestrictionAsync(Groceries)).Name);
        Assert.Equal(2, await s.ApplyExistingAsync(AccessWorld.Shared));
    }

    [Fact]
    public async Task Restriction_targets_list_the_account_tree_and_reject_foreign_ids()
    {
        await using var t = await WorldAsync();
        var s = Service(t);
        var targets = await s.GetRestrictionTargetsAsync(Groceries);
        var cards = targets.FindIndex(x => x.IsGroup && x.Id == SubGroup);
        Assert.Equal((true, TopGroup, 0), (targets[cards - 1].IsGroup, targets[cards - 1].Id, targets[cards - 1].Depth));
        Assert.Equal((false, AccessWorld.AccountWp, 2), (targets[cards + 1].IsGroup, targets[cards + 1].Id, targets[cards + 1].Depth));
        await Assert.ThrowsAsync<RuleValidationException>(() => s.SetRestrictionAsync(Groceries, AccessWorld.AccountBob, null));
        await Assert.ThrowsAsync<ArgumentException>(() => s.SetRestrictionAsync(Groceries, AccessWorld.AccountWp, TopGroup));
    }

    [Fact]
    public async Task Only_the_owner_may_change_the_restriction()
    {
        await using var t = await WorldAsync();
        var bob = new CategoryRuleService(t.ForUser(AccessWorld.Bob).Factory, t.Notifier, new ChangeOrigin());
        Assert.False((await bob.GetRestrictionAsync(Groceries)).CanEdit);
        await Assert.ThrowsAsync<AccessDeniedException>(() => bob.SetRestrictionAsync(Groceries, AccessWorld.AccountWp, null));
    }

    [Fact]
    public async Task Sync_does_not_apply_a_rule_whose_category_is_restricted_to_another_account()
    {
        await using var t = await WorldAsync();
        var s = Service(t);
        await AddRuleAsync(s);
        await s.SetRestrictionAsync(Groceries, AccessWorld.AccountPrivate, null);

        var day = new DateOnly(2026, 4, 2);
        var refresh = new AccountRefresh(null, [new RemoteTransaction(-12.50m, "EUR", day, null, "Example Market", null, Booked: true, BankTransactionId: "b1")], []);
        await using (var db = await t.System.CreateDbContextAsync())
            await new BankTransactionStore().ApplyAsync(db, AccessWorld.AccountWp, refresh, null, DateTime.UtcNow, CancellationToken.None);
        await using (var db = await t.System.CreateDbContextAsync())
            await new BankTransactionStore().ApplyAsync(db, AccessWorld.AccountPrivate, refresh, null, DateTime.UtcNow, CancellationToken.None);

        await using var check = await t.System.CreateDbContextAsync();
        var rows = await check.Transactions.AsNoTracking().Where(x => x.ExternalId == "id:b1").ToListAsync();
        Assert.Null(rows.Single(r => r.AccountId == AccessWorld.AccountWp).CategoryId);
        Assert.Equal(Groceries, rows.Single(r => r.AccountId == AccessWorld.AccountPrivate).CategoryId);
    }

    [Fact]
    public async Task Account_group_terms_match_accounts_in_nested_groups()
    {
        await using var t = await WorldAsync();
        var s = Service(t);
        await AddRuleAsync(s, $"market AND account_group={TopGroup}");
        Assert.Equal(1, await s.ApplyExistingAsync(AccessWorld.Shared));
        Assert.Equal(Groceries, await CategoryOfAsync(t, 20));
    }
}
