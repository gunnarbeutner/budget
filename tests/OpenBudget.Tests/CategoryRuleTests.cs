#pragma warning disable CA2000 // test objects live for the test
using Microsoft.EntityFrameworkCore;
using OpenBudget.Connectors;
using OpenBudget.Connectors.Sync;
using OpenBudget.Models;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests;

/// <summary>
/// Categorisation rules (the category rules bar): matching, ordering (first match wins), never overwriting a manual category,
/// "apply to existing" with its preview, the bank-sync hook, and who may see and change rules.
/// </summary>
public sealed class CategoryRuleTests
{
    private static readonly long Groceries = AccessWorld.Id(AccessWorld.SourceAlice, 210);
    private static readonly long Dining = AccessWorld.Id(AccessWorld.SourceAlice, 211);

    // ---- matching ----

    private static CategoryRule Rule(string expression) => new() { Expression = expression, CategoryId = 1, Enabled = true };

    private static RuleSubject Subject(string? name, long amount = -1000, string? purpose = null, string? bookingText = null, long? account = 1) =>
        new(account, amount, name, purpose, bookingText);

    [Fact]
    public void Payee_text_is_a_case_insensitive_contains()
    {
        Assert.True(CategoryRuleMatcher.Matches(Rule("name:\"example market\""), Subject("EXAMPLE MARKET Berlin")));
        Assert.False(CategoryRuleMatcher.Matches(Rule("name:\"example market\""), Subject("Sample Bakery")));
        Assert.False(CategoryRuleMatcher.Matches(Rule("name:\"example market\""), Subject(null)));
    }

    [Fact]
    public void Invalid_or_empty_expressions_never_match()
    {
        Assert.False(CategoryRuleMatcher.Matches(Rule(""), Subject("Example Market")));
        Assert.False(CategoryRuleMatcher.Matches(Rule("name:/([/"), Subject("([")));
        Assert.Equal(RuleValidation.NoCondition, CategoryRuleService.Validate(new CategoryRule { CategoryId = 1 }));
        Assert.Equal(RuleValidation.InvalidExpression, CategoryRuleService.Validate(Rule("(\"unclosed")));
    }

    [Fact]
    public void First_enabled_match_wins()
    {
        var disabled = Rule("example"); disabled.Enabled = false; disabled.CategoryId = 1;
        var broad = Rule("example"); broad.CategoryId = 2;
        var narrow = Rule("\"example market\""); narrow.CategoryId = 3;
        Assert.Equal(2, CategoryRuleMatcher.FirstMatch([disabled, broad, narrow], Subject("Example Market"))!.CategoryId);
        Assert.Equal(3, CategoryRuleMatcher.FirstMatch([disabled, narrow, broad], Subject("Example Market"))!.CategoryId);
        Assert.Null(CategoryRuleMatcher.FirstMatch([disabled], Subject("Example Market")));
    }

    // ---- service ----

    private static async Task<TestDb> WorldAsync(ProjectRole? bobRole = ProjectRole.Editor)
    {
        var t = await AccessWorld.BuildAsync(bobRole);
        await using var db = await t.System.CreateDbContextAsync();
        foreach (var id in new[] { AccessWorld.AccountWp, AccessWorld.AccountPrivate, AccessWorld.AccountBob })
            (await db.Accounts.SingleAsync(a => a.Id == id)).Provider = "fake";
        db.Categories.Add(new Category { Id = Groceries, OwnerUserId = AccessWorld.Alice, Name = "Groceries", GroupId = AccessWorld.SharedGroup, Type = 1 });
        db.Categories.Add(new Category { Id = Dining, OwnerUserId = AccessWorld.Alice, Name = "Dining", GroupId = AccessWorld.SharedGroup, Type = 1 });
        db.Transactions.Add(Row(20, AccessWorld.AccountWp, "Example Market"));
        db.Transactions.Add(Row(21, AccessWorld.AccountWp, "Example Market"));
        // Set aside by hand: removed the category on purpose.
        var manual = Row(22, AccessWorld.AccountWp, "Example Market");
        manual.CategorySetBy = TransactionCategorySource.Manual;
        db.Transactions.Add(manual);
        // Already categorised (by hand).
        var categorized = Row(23, AccessWorld.AccountWp, "Example Market");
        categorized.CategoryId = Dining;
        categorized.CategorySetBy = TransactionCategorySource.Manual;
        db.Transactions.Add(categorized);
        // Another payee.
        db.Transactions.Add(Row(24, AccessWorld.AccountWp, "Sample Bakery"));
        // Property account is still MoneyMoney-owned: read-only.
        db.Transactions.Add(Row(25, AccessWorld.AccountProperty, "Example Market"));
        await db.SaveChangesAsync();
        return t;
    }

    private static Transaction Row(long id, long account, string payee) => new()
    {
        Id = id,
        OwnerUserId = AccessWorld.Alice,
        AccountId = account,
        Amount = -1250,
        Name = payee,
        Timestamp = new DateTimeOffset(2026, 3, 12, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(),
    };

    private static CategoryRuleService ServiceFor(TestDb t, string? user) => new(t.ForUser(user).Factory, t.Notifier, new ChangeOrigin());

    private static Task<int> AddRuleAsync(CategoryRuleService s, int project, string payee, long category, string? name = null) =>
        s.SaveAsync(new CategoryRule { ProjectId = project, Expression = "name:\"" + payee + "\"", CategoryId = category, Enabled = true, Name = name });

    private static async Task<Transaction> LoadAsync(TestDb t, long id)
    {
        await using var db = await t.System.CreateDbContextAsync();
        return await db.Transactions.AsNoTracking().SingleAsync(x => x.Id == id);
    }

    [Fact]
    public async Task Rules_are_appended_in_order_and_can_be_moved()
    {
        await using var t = await WorldAsync();
        var s = ServiceFor(t, AccessWorld.Alice);
        var a = await AddRuleAsync(s, AccessWorld.Shared, "Example", Groceries, "A");
        var b = await AddRuleAsync(s, AccessWorld.Shared, "Sample", Dining, "B");
        var c = await AddRuleAsync(s, AccessWorld.Shared, "Market", Dining, "C");

        Assert.Equal([a, b, c], (await s.GetRulesAsync(AccessWorld.Shared)).Select(v => v.Rule.Id));
        await s.MoveAsync(AccessWorld.Shared, c, -1);
        Assert.Equal([a, c, b], (await s.GetRulesAsync(AccessWorld.Shared)).Select(v => v.Rule.Id));
        await s.MoveAsync(AccessWorld.Shared, a, -1); // already first: no-op
        await s.MoveAsync(AccessWorld.Shared, a, 1);
        Assert.Equal([c, a, b], (await s.GetRulesAsync(AccessWorld.Shared)).Select(v => v.Rule.Id));
        await s.DeleteAsync(AccessWorld.Shared, a);
        Assert.Equal([c, b], (await s.GetRulesAsync(AccessWorld.Shared)).Select(v => v.Rule.Id));
    }

    [Fact]
    public async Task Moving_within_a_category_skips_the_rules_of_other_categories()
    {
        await using var t = await WorldAsync();
        var s = ServiceFor(t, AccessWorld.Alice);
        var a = await AddRuleAsync(s, AccessWorld.Shared, "Example", Groceries);
        var b = await AddRuleAsync(s, AccessWorld.Shared, "Sample", Dining);
        var c = await AddRuleAsync(s, AccessWorld.Shared, "Market", Groceries);

        await s.MoveWithinCategoryAsync(AccessWorld.Shared, c, -1);
        Assert.Equal([c, b, a], (await s.GetRulesAsync(AccessWorld.Shared)).Select(v => v.Rule.Id));
        Assert.Equal([c, a], (await s.GetCategoryRulesAsync(AccessWorld.Shared, Groceries)).Select(v => v.Rule.Id));
        await s.MoveWithinCategoryAsync(AccessWorld.Shared, b, 1); // only Dining rule: no-op
        Assert.Equal([c, b, a], (await s.GetRulesAsync(AccessWorld.Shared)).Select(v => v.Rule.Id));
    }

    [Fact]
    public async Task Draft_preview_lists_matching_rows_and_apply_can_be_limited_to_a_category()
    {
        await using var t = await WorldAsync();
        var s = ServiceFor(t, AccessWorld.Alice);
        var draft = new CategoryRule { ProjectId = AccessWorld.Shared, Expression = "name:\"example market\"" };
        Assert.Equal(4, (await s.PreviewMatchIdsAsync(AccessWorld.Shared, draft)).Count);
        Assert.Empty(await s.PreviewMatchIdsAsync(AccessWorld.Shared, new CategoryRule { ProjectId = AccessWorld.Shared }));

        await AddRuleAsync(s, AccessWorld.Shared, "Example Market", Groceries);
        Assert.Equal(0, await s.PreviewApplyExistingAsync(AccessWorld.Shared, Dining));
        Assert.Equal(2, await s.PreviewApplyExistingAsync(AccessWorld.Shared, Groceries));
        Assert.Equal(0, await s.ApplyExistingAsync(AccessWorld.Shared, Dining));
        Assert.Equal(2, await s.ApplyExistingAsync(AccessWorld.Shared, Groceries));
    }

    [Fact]
    public async Task Match_counts_cover_the_projects_transactions()
    {
        await using var t = await WorldAsync();
        var s = ServiceFor(t, AccessWorld.Alice);
        await AddRuleAsync(s, AccessWorld.Shared, "example market", Groceries);
        await AddRuleAsync(s, AccessWorld.Shared, "Sample Bakery", Dining);
        var views = await s.GetRulesAsync(AccessWorld.Shared);
        Assert.Equal(4, views[0].MatchCount); // rows 20-23 on the shared account; row 25 is another project's account
        Assert.Equal(1, views[1].MatchCount);
        Assert.Equal("Groceries", views[0].CategoryName);
    }

    [Fact]
    public async Task Apply_existing_previews_then_categorizes_without_overwriting_manual_choices()
    {
        await using var t = await WorldAsync();
        var s = ServiceFor(t, AccessWorld.Alice);
        var groceries = await AddRuleAsync(s, AccessWorld.Shared, "Example Market", Groceries);
        await AddRuleAsync(s, AccessWorld.Shared, "Example", Dining); // later rule: loses

        Assert.Equal(2, await s.PreviewApplyExistingAsync(AccessWorld.Shared));
        Assert.Equal(2, await s.ApplyExistingAsync(AccessWorld.Shared));

        foreach (var id in new long[] { 20, 21 })
        {
            var row = await LoadAsync(t, id);
            Assert.Equal(Groceries, row.CategoryId);
            Assert.Equal(TransactionCategorySource.Rule, row.CategorySetBy);
            Assert.Equal(groceries, row.RuleId);
        }
        Assert.Null((await LoadAsync(t, 22)).CategoryId);            // removed by hand
        Assert.Equal(Dining, (await LoadAsync(t, 23)).CategoryId);   // chosen by hand
        Assert.Null((await LoadAsync(t, 24)).CategoryId);            // no rule matches
        Assert.Equal(0, await s.PreviewApplyExistingAsync(AccessWorld.Shared));
    }

    [Fact]
    public async Task Expression_rules_apply_with_fields_and_operators()
    {
        await using var t = await WorldAsync();
        var s = ServiceFor(t, AccessWorld.Alice);
        await s.SaveAsync(new CategoryRule
        {
            ProjectId = AccessWorld.Shared,
            CategoryId = Groceries,
            Enabled = true,
            Expression = "(name:\"example market\" OR \"sample bakery\") AND amount<0 AND NOT comment:\"refund\"",
        });
        Assert.Equal(3, await s.ApplyExistingAsync(AccessWorld.Shared));
        Assert.Equal(Groceries, (await LoadAsync(t, 20)).CategoryId);
    }

    [Fact]
    public async Task Disabled_rules_do_not_apply()
    {
        await using var t = await WorldAsync();
        var s = ServiceFor(t, AccessWorld.Alice);
        var id = await AddRuleAsync(s, AccessWorld.Shared, "Example Market", Groceries);
        await s.SetEnabledAsync(AccessWorld.Shared, id, false);
        Assert.Equal(0, await s.PreviewApplyExistingAsync(AccessWorld.Shared));
    }

    [Fact]
    public async Task Moneymoney_owned_accounts_are_left_alone()
    {
        await using var t = await WorldAsync();
        var s = ServiceFor(t, AccessWorld.Alice);
        await AddRuleAsync(s, AccessWorld.Property, "Example Market", Groceries);
        Assert.Equal(0, await s.ApplyExistingAsync(AccessWorld.Property));
        Assert.Null((await LoadAsync(t, 25)).CategoryId);
    }

    [Fact]
    public async Task Validation_rejects_bad_rules()
    {
        await using var t = await WorldAsync();
        var s = ServiceFor(t, AccessWorld.Alice);
        var noCondition = await Assert.ThrowsAsync<RuleValidationException>(() =>
            s.SaveAsync(new CategoryRule { ProjectId = AccessWorld.Shared, CategoryId = Groceries }));
        Assert.Equal(RuleValidation.NoCondition, noCondition.Reason);
        var badRegex = await Assert.ThrowsAsync<RuleValidationException>(() =>
            s.SaveAsync(new CategoryRule { ProjectId = AccessWorld.Shared, CategoryId = Groceries, Expression = "name:/([/" }));
        Assert.Equal(RuleValidation.InvalidExpression, badRegex.Reason);
        var otherSource = await Assert.ThrowsAsync<RuleValidationException>(() =>
            AddRuleAsync(s, AccessWorld.Shared, "x", AccessWorld.Id(AccessWorld.SourceBob, 200)));
        Assert.Equal(RuleValidation.NoCategory, otherSource.Reason);
        var foreignAccount = await Assert.ThrowsAsync<RuleValidationException>(() =>
            s.SaveAsync(new CategoryRule { ProjectId = AccessWorld.Shared, CategoryId = Groceries, Expression = "x AND account_id=" + AccessWorld.AccountPrivate }));
        Assert.Equal(RuleValidation.UnknownAccount, foreignAccount.Reason);
    }

    // ---- access ----

    [Fact]
    public async Task An_editor_may_manage_and_apply_rules_of_a_shared_project()
    {
        await using var t = await WorldAsync(ProjectRole.Editor);
        var bob = ServiceFor(t, AccessWorld.Bob);
        await AddRuleAsync(bob, AccessWorld.Shared, "Example Market", Groceries);
        Assert.Equal(2, await bob.ApplyExistingAsync(AccessWorld.Shared));
    }

    [Fact]
    public async Task A_viewer_sees_rules_but_cannot_change_or_apply_them()
    {
        await using var t = await WorldAsync(ProjectRole.Viewer);
        var id = await AddRuleAsync(ServiceFor(t, AccessWorld.Alice), AccessWorld.Shared, "Example Market", Groceries);
        var bob = ServiceFor(t, AccessWorld.Bob);

        Assert.Single(await bob.GetRulesAsync(AccessWorld.Shared));
        await Assert.ThrowsAsync<AccessDeniedException>(() => AddRuleAsync(bob, AccessWorld.Shared, "Sample", Dining));
        await Assert.ThrowsAsync<AccessDeniedException>(() => bob.SetEnabledAsync(AccessWorld.Shared, id, false));
        await Assert.ThrowsAsync<AccessDeniedException>(() => bob.DeleteAsync(AccessWorld.Shared, id));
        Assert.Equal(0, await bob.PreviewApplyExistingAsync(AccessWorld.Shared));
        Assert.Equal(0, await bob.ApplyExistingAsync(AccessWorld.Shared));
        Assert.Null((await LoadAsync(t, 20)).CategoryId);
    }

    [Fact]
    public async Task Strangers_see_no_rules_and_cannot_add_any()
    {
        await using var t = await WorldAsync(bobRole: null);
        await AddRuleAsync(ServiceFor(t, AccessWorld.Alice), AccessWorld.Private, "Example Market", Groceries);
        var bob = ServiceFor(t, AccessWorld.Bob);
        Assert.Empty(await bob.GetRulesAsync(AccessWorld.Private));
        await Assert.ThrowsAsync<AccessDeniedException>(() => AddRuleAsync(bob, AccessWorld.Private, "Sample", Dining));
    }

    // ---- bank sync hook ----

    [Fact]
    public async Task Sync_categorizes_new_rows_and_leaves_known_ones_alone()
    {
        await using var t = await WorldAsync();
        var s = ServiceFor(t, AccessWorld.Alice);
        var rule = await AddRuleAsync(s, AccessWorld.Shared, "example market", Groceries);
        await AddRuleAsync(s, AccessWorld.Private, "example market", Dining); // another project's account: not used
        await s.SaveAsync(new CategoryRule { ProjectId = AccessWorld.Shared, CategoryId = Dining, Enabled = true, Expression = "bakery AND amount>=-5,00 AND amount<0" });

        var day = new DateOnly(2026, 4, 2);
        var refresh = new AccountRefresh(null,
        [
            new RemoteTransaction(-12.50m, "EUR", day, null, "EXAMPLE MARKET 042", "Card payment", Booked: true, BankTransactionId: "b1"),
            new RemoteTransaction(-4.00m, "EUR", day, null, "Sample Bakery", null, Booked: true, BankTransactionId: "b2"),
            new RemoteTransaction(-7.00m, "EUR", day, null, "Example Market", null, Booked: false),
        ], []);
        await using (var db = await t.System.CreateDbContextAsync())
            await new BankTransactionStore().ApplyAsync(db, AccessWorld.AccountWp, refresh, null, DateTime.UtcNow, CancellationToken.None);

        await using var check = await t.System.CreateDbContextAsync();
        var rows = await check.Transactions.AsNoTracking().Where(x => x.ExternalId != null || !x.Booked).ToListAsync();
        var market = rows.Single(r => r.ExternalId == "id:b1");
        Assert.Equal(Groceries, market.CategoryId);
        Assert.Equal(TransactionCategorySource.Rule, market.CategorySetBy);
        Assert.Equal(rule, market.RuleId);
        Assert.True(market.IsNew);
        Assert.Equal(Dining, rows.Single(r => r.ExternalId == "id:b2").CategoryId);
        Assert.Equal(Groceries, rows.Single(r => !r.Booked).CategoryId);
    }
}
