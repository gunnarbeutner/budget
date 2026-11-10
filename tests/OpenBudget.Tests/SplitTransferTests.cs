#pragma warning disable CA2000 // test objects live for the test
using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests;

/// <summary>
/// Splits and transfers: validation, how they change budget / cashflow / sidebar sums and the list, transfer
/// suggestions, MoneyMoney rows and who may do what.
/// </summary>
public sealed class SplitTransferTests
{
    private const long Tx1 = 1; // -100.00 on the Shared account (connector-owned), SharedLeaf
    private const long Tx3 = 3; // property account, still MoneyMoney-owned

    private static readonly long UntrackedGroup = AccessWorld.Id(AccessWorld.SourceAlice, 150);
    private static readonly long UntrackedLeaf = AccessWorld.Id(AccessWorld.SourceAlice, 250);
    private static readonly long AccountSavings = AccessWorld.Id(AccessWorld.SourceAlice, 5);
    private static readonly long AccountMm = AccessWorld.Id(AccessWorld.SourceAlice, 6);
    private static readonly long AccountOther = AccessWorld.Id(AccessWorld.SourceAlice, 7);

    /// <summary>Category sums are source-wide: the private and property rows (500.00 + 25.00) are in the group too.</summary>
    private const decimal OtherRows = 525m;

    private static readonly DateTime March10 = new(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc);

    private static async Task<TestDb> WorldAsync(ProjectRole? bobRole = ProjectRole.Editor)
    {
        var t = await AccessWorld.BuildAsync(bobRole);
        await using var db = await t.System.CreateDbContextAsync();
        foreach (var id in new[] { AccessWorld.AccountWp, AccessWorld.AccountPrivate })
            (await db.Accounts.SingleAsync(a => a.Id == id)).Provider = "fake";
        db.CategoryGroups.Add(new CategoryGroup { Id = UntrackedGroup, SourceId = AccessWorld.SourceAlice, Name = "Untracked" });
        db.Categories.Add(new Category { Id = UntrackedLeaf, SourceId = AccessWorld.SourceAlice, Name = "Other leaf", GroupId = UntrackedGroup, Type = 1 });
        db.Accounts.Add(new Account { Id = AccountSavings, SourceId = AccessWorld.SourceAlice, Name = "Example Savings", Provider = "fake" });
        db.Accounts.Add(new Account { Id = AccountMm, SourceId = AccessWorld.SourceAlice, Name = "Old Giro" });
        db.Accounts.Add(new Account { Id = AccountOther, SourceId = AccessWorld.SourceAlice, Name = "Unassigned", Provider = "fake" });
        db.ProjectAccountAssignments.Add(new ProjectAccountAssignment { ProjectId = AccessWorld.Shared, AccountId = AccountSavings, Scope = AccountScope.Exclusive });
        db.ProjectAccountAssignments.Add(new ProjectAccountAssignment { ProjectId = AccessWorld.Shared, AccountId = AccountMm, Scope = AccountScope.Exclusive });
        // Transfer candidates for Tx1 (-100.00 on 10 March).
        db.Transactions.Add(Row(20, AccountSavings, 10000, March10.AddDays(2)));   // match
        db.Transactions.Add(Row(21, AccountSavings, 10000, March10.AddDays(7)));   // too late
        db.Transactions.Add(Row(22, AccountSavings, -10000, March10));             // same sign
        db.Transactions.Add(Row(23, AccountMm, 10000, March10));                   // MoneyMoney account
        db.Transactions.Add(Row(24, AccountOther, 10000, March10));                // account not in the project
        db.Transactions.Add(Row(25, AccountSavings, 10000, March10.AddDays(-1)));  // match, closer
        await db.SaveChangesAsync();
        return t;
    }

    private static Transaction Row(long id, long account, long cents, DateTime date) => new()
    {
        Id = id,
        SourceId = AccessWorld.SourceAlice,
        AccountId = account,
        Amount = cents,
        Name = "Example Transfer",
        Timestamp = new DateTimeOffset(date).ToUnixTimeSeconds(),
    };

    private static SplitTransferService For(TestDb t, string? user) => new(t.ForUser(user).Factory, t.Notifier, new ChangeOrigin());

    private static Task<decimal> SpentAsync(TestDb t) =>
        t.Spending.GetSpendingSummaryAsync(AccessWorld.Shared, 2026, 3, 3).ContinueWith(s => s.Result.TotalSpent - OtherRows, TaskScheduler.Default);

    private static async Task<decimal> CashflowExpensesAsync(TestDb t)
    {
        var batch = await t.Spending.GetMonthlyIncomeAndExpenseBatchAsync(
            [(AccessWorld.SharedGroup, CategoryItemType.Group)], 2026, AccessWorld.SourceAlice, AccessWorld.Shared);
        return batch[(AccessWorld.SharedGroup, CategoryItemType.Group)].Expenses[2] - OtherRows;
    }

    private static async Task<decimal> SidebarSpentAsync(TestDb t)
    {
        var tree = await t.Browse.GetTreeAsync(AccessWorld.Shared, 2026, 3, 3);
        return tree!.Categories.Single(c => c.Id == AccessWorld.SharedGroup).Spent - OtherRows;
    }

    private static readonly SplitPartInput[] SixtyForty =
    [
        new(-6000, AccessWorld.SharedLeaf, "Groceries"),
        new(-4000, UntrackedLeaf, null),
    ];

    // ---- pure logic ----

    [Theory]
    [InlineData("12,34", 1234)]
    [InlineData("12.34", 1234)]
    [InlineData("1.234,5", 123450)]
    [InlineData("1,234.50", 123450)]
    [InlineData("-5", -500)]
    [InlineData(" 7 € ", 700)]
    [InlineData("1.000", 100000)]
    public void Amounts_parse_in_german_and_english_notation(string text, long cents) =>
        Assert.Equal(cents, SplitTransferService.ParseCents(text));

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1,2,x")]
    public void Garbage_does_not_parse(string text) => Assert.Null(SplitTransferService.ParseCents(text));

    [Fact]
    public void Split_validation_needs_two_nonzero_parts_that_add_up()
    {
        Assert.Equal(SplitError.None, SplitTransferService.Validate(-10000, SixtyForty));
        Assert.Equal(SplitError.TooFewParts, SplitTransferService.Validate(-10000, [new(-10000, null, null)]));
        Assert.Equal(SplitError.ZeroPart, SplitTransferService.Validate(-10000, [new(-10000, null, null), new(0, null, null)]));
        Assert.Equal(SplitError.SumMismatch, SplitTransferService.Validate(-10000, [new(-6000, null, null), new(-3000, null, null)]));
        Assert.Equal(-1000, SplitTransferService.Remaining(-10000, [-6000, -3000]));
    }

    [Fact]
    public void A_transfer_match_needs_opposite_amount_close_date_and_another_account()
    {
        var row = Row(1, AccessWorld.AccountWp, -10000, March10);
        Assert.True(SplitTransferService.IsTransferMatch(row, Row(2, AccountSavings, 10000, March10.AddDays(5))));
        Assert.False(SplitTransferService.IsTransferMatch(row, Row(2, AccountSavings, 10000, March10.AddDays(6))));
        Assert.False(SplitTransferService.IsTransferMatch(row, Row(2, AccountSavings, 9999, March10)));
        Assert.False(SplitTransferService.IsTransferMatch(row, Row(2, AccessWorld.AccountWp, 10000, March10)));
        var pending = Row(2, AccountSavings, 10000, March10);
        pending.Booked = false;
        Assert.False(SplitTransferService.IsTransferMatch(row, pending));
        var already = Row(2, AccountSavings, 10000, March10);
        already.IsTransfer = true;
        Assert.False(SplitTransferService.IsTransferMatch(row, already));
    }

    // ---- splits ----

    [Fact]
    public async Task Splitting_counts_the_parts_instead_of_the_booking_everywhere()
    {
        await using var t = await WorldAsync();
        Assert.Equal(100m, await SpentAsync(t));
        Assert.Equal(100m, await CashflowExpensesAsync(t));
        Assert.Equal(100m, await SidebarSpentAsync(t));

        await For(t, AccessWorld.Alice).SplitAsync(Tx1, SixtyForty);

        // Only the 60.00 part stays in the tracked group; the 40.00 part moved to an untracked category.
        Assert.Equal(60m, await SpentAsync(t));
        Assert.Equal(60m, await CashflowExpensesAsync(t));
        Assert.Equal(60m, await SidebarSpentAsync(t));

        // The list shows the parts, not the booking; the account's total is unchanged.
        var page = await t.TransactionQueries.QueryAsync(new TransactionQuery
        {
            SourceId = AccessWorld.SourceAlice,
            AccountIds = [AccessWorld.AccountWp],
            Skip = 0,
            Take = 50,
        });
        Assert.DoesNotContain(page.Rows, r => r.Id == Tx1);
        var parts = page.Rows.Where(r => r.IsSplitChild).ToList();
        Assert.Equal(2, parts.Count);
        Assert.All(parts, p => Assert.Equal(Tx1, p.SplitParentId));
        Assert.Equal(-100m, page.TotalSum);
        Assert.Contains(parts, p => p.Note == "Groceries" && p.Amount == -60m);

        var info = await For(t, AccessWorld.Alice).GetSplitAsync(parts[0].Id);
        Assert.Equal(Tx1, info!.ParentId);
        Assert.Equal(-10000, info.TotalCents);
        Assert.Equal(2, info.Parts.Count);
    }

    [Fact]
    public async Task Resplitting_replaces_the_parts_and_removing_restores_the_booking()
    {
        await using var t = await WorldAsync();
        var svc = For(t, AccessWorld.Alice);
        await svc.SplitAsync(Tx1, SixtyForty);
        var firstPart = (await svc.GetSplitAsync(Tx1))!.Parts[0].Id;

        await svc.SplitAsync(firstPart, [new(-9000, AccessWorld.SharedLeaf, null), new(-500, null, null), new(-500, UntrackedLeaf, null)]);
        var info = await svc.GetSplitAsync(Tx1);
        Assert.Equal(3, info!.Parts.Count);
        Assert.Equal(90m, await SpentAsync(t));

        await svc.RemoveSplitAsync(Tx1);
        Assert.Empty((await svc.GetSplitAsync(Tx1))!.Parts);
        Assert.Equal(100m, await SpentAsync(t));
        await using var db = await t.System.CreateDbContextAsync();
        var parent = await db.Transactions.SingleAsync(x => x.Id == Tx1);
        Assert.Equal(SplitState.Normal, parent.Splitted);
        Assert.False(await db.Transactions.AnyAsync(x => x.SplitParentId == Tx1));
    }

    [Fact]
    public async Task Splits_that_dont_add_up_or_hit_moneymoney_or_pending_rows_are_refused()
    {
        await using var t = await WorldAsync();
        var svc = For(t, AccessWorld.Alice);
        await Assert.ThrowsAsync<SplitTransferException>(() => svc.SplitAsync(Tx1, [new(-6000, null, null), new(-3000, null, null)]));
        await Assert.ThrowsAsync<AccessDeniedException>(() => svc.SplitAsync(Tx3, [new(-1000, null, null), new(-1500, null, null)]));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.SplitAsync(Tx1, [new(-6000, AccessWorld.Id(AccessWorld.SourceBob, 200), null), new(-4000, null, null)]));

        await using (var db = await t.System.CreateDbContextAsync())
        {
            (await db.Transactions.SingleAsync(x => x.Id == Tx1)).Booked = false;
            await db.SaveChangesAsync();
        }
        var ex = await Assert.ThrowsAsync<SplitTransferException>(() => svc.SplitAsync(Tx1, SixtyForty));
        Assert.Equal(SplitTransferProblem.Pending, ex.Problem);
        Assert.Equal(100m, await SpentAsync(t));
    }

    [Fact]
    public async Task Rules_still_skip_split_parents()
    {
        await using var t = await WorldAsync();
        await For(t, AccessWorld.Alice).SplitAsync(Tx1, SixtyForty);
        await using var db = await t.System.CreateDbContextAsync();
        var parent = await db.Transactions.SingleAsync(x => x.Id == Tx1);
        parent.CategoryId = null;
        Assert.False(CategoryRuleEngine.IsCandidate(parent));
    }

    // ---- transfers ----

    [Fact]
    public async Task Suggestions_are_opposite_bookings_within_five_days_on_editable_project_accounts()
    {
        await using var t = await WorldAsync();
        var candidates = await For(t, AccessWorld.Alice).GetTransferCandidatesAsync(Tx1, AccessWorld.Shared);
        Assert.Equal([25L, 20L], candidates.Select(c => c.Id).ToArray());
        Assert.All(candidates, c => Assert.Equal("Example Savings", c.AccountName));
    }

    [Fact]
    public async Task A_linked_transfer_counts_as_neither_spending_nor_income_until_unlinked()
    {
        await using var t = await WorldAsync();
        var svc = For(t, AccessWorld.Alice);
        await svc.LinkTransferAsync(Tx1, 20);

        Assert.Equal(0m, await SpentAsync(t));
        Assert.Equal(0m, await CashflowExpensesAsync(t));
        Assert.Equal(0m, await SidebarSpentAsync(t));
        var info = await svc.GetTransferAsync(20);
        Assert.Equal(Tx1, info!.PeerId);
        var page = await t.TransactionQueries.QueryAsync(new TransactionQuery
        {
            SourceId = AccessWorld.SourceAlice,
            AccountIds = [AccessWorld.AccountWp],
            Skip = 0,
            Take = 50,
        });
        var row = page.Rows.Single(r => r.Id == Tx1);
        Assert.True(row.IsTransfer);
        Assert.Equal("Example Savings", row.TransferPeerAccountName);
        // A booking that is already a transfer is no longer suggested, and can't be linked twice.
        Assert.DoesNotContain(await svc.GetTransferCandidatesAsync(25, AccessWorld.Shared), c => c.Id == Tx1);
        await Assert.ThrowsAsync<SplitTransferException>(() => svc.LinkTransferAsync(25, Tx1));

        await svc.UnlinkTransferAsync(20);
        Assert.Equal(100m, await SpentAsync(t));
        await using var db = await t.System.CreateDbContextAsync();
        Assert.False(await db.Transactions.AnyAsync(x => x.IsTransfer || x.TransferPeerId != null));
    }

    [Fact]
    public async Task A_one_sided_transfer_drops_out_of_totals_and_the_uncategorized_count_but_not_the_balance()
    {
        await using var t = await WorldAsync();
        await using (var db = await t.System.CreateDbContextAsync())
        {
            (await db.Transactions.SingleAsync(x => x.Id == 20)).CategoryId = null;
            await db.SaveChangesAsync();
        }
        var before = (await t.Browse.GetTreeAsync(AccessWorld.Shared, 2026, 3, 3))!;
        var svc = For(t, AccessWorld.Alice);
        await svc.MarkOneSidedTransferAsync(Tx1);
        await svc.MarkOneSidedTransferAsync(20);

        Assert.Equal(0m, await SpentAsync(t));
        var after = (await t.Browse.GetTreeAsync(AccessWorld.Shared, 2026, 3, 3))!;
        Assert.Equal(before.UncategorizedCount - 1, after.UncategorizedCount);
        // The account's period flow still includes the booking.
        Assert.Equal(before.Accounts.Single(a => a.Id == AccessWorld.AccountWp).PeriodSum, after.Accounts.Single(a => a.Id == AccessWorld.AccountWp).PeriodSum);
        Assert.Null((await svc.GetTransferAsync(Tx1))!.PeerId);
    }

    [Fact]
    public async Task Moneymoney_rows_cannot_be_transfer_counterparts_and_split_rows_cannot_be_transfers()
    {
        await using var t = await WorldAsync();
        var svc = For(t, AccessWorld.Alice);
        await Assert.ThrowsAsync<AccessDeniedException>(() => svc.LinkTransferAsync(Tx1, 23));
        await svc.SplitAsync(Tx1, SixtyForty);
        await Assert.ThrowsAsync<SplitTransferException>(() => svc.MarkOneSidedTransferAsync(Tx1));
        await svc.RemoveSplitAsync(Tx1);
        await svc.MarkOneSidedTransferAsync(Tx1);
        await Assert.ThrowsAsync<SplitTransferException>(() => svc.SplitAsync(Tx1, SixtyForty));
    }

    // ---- access ----

    [Fact]
    public async Task A_project_editor_may_split_and_mark_transfers_on_shared_accounts()
    {
        await using var t = await WorldAsync(ProjectRole.Editor);
        var svc = For(t, AccessWorld.Bob);
        await svc.SplitAsync(Tx1, SixtyForty);
        Assert.Equal(2, (await svc.GetSplitAsync(Tx1))!.Parts.Count);
        await svc.RemoveSplitAsync(Tx1);
        await svc.LinkTransferAsync(Tx1, 20);
        Assert.True((await svc.GetTransferAsync(Tx1))!.IsTransfer);
    }

    [Fact]
    public async Task A_viewer_or_stranger_may_not()
    {
        await using var t = await WorldAsync(ProjectRole.Viewer);
        var viewer = For(t, AccessWorld.Bob);
        await Assert.ThrowsAsync<AccessDeniedException>(() => viewer.SplitAsync(Tx1, SixtyForty));
        await Assert.ThrowsAsync<AccessDeniedException>(() => viewer.MarkOneSidedTransferAsync(Tx1));
        await Assert.ThrowsAsync<AccessDeniedException>(() => viewer.LinkTransferAsync(Tx1, 20));
        Assert.Empty(await viewer.GetTransferCandidatesAsync(Tx1, AccessWorld.Shared));

        await using var noShare = await WorldAsync(null);
        await Assert.ThrowsAsync<SplitTransferException>(() => For(noShare, AccessWorld.Bob).SplitAsync(Tx1, SixtyForty)); // not even visible
    }

    [Fact]
    public async Task The_write_guard_only_lets_editors_add_split_parts_not_other_rows()
    {
        await using var t = await WorldAsync(ProjectRole.Editor);
        await using var db = await t.ForUser(AccessWorld.Bob).Factory.CreateDbContextAsync();
        db.Transactions.Add(new Transaction
        {
            Id = 9001,
            SourceId = AccessWorld.SourceAlice,
            AccountId = AccessWorld.AccountWp,
            Amount = -1,
            Name = "Example Market",
        });
        await Assert.ThrowsAsync<AccessDeniedException>(() => db.SaveChangesAsync());

        await using var db2 = await t.ForUser(AccessWorld.Bob).Factory.CreateDbContextAsync();
        db2.Transactions.Add(new Transaction
        {
            Id = 9002,
            SourceId = AccessWorld.SourceAlice,
            AccountId = AccessWorld.AccountProperty,
            Amount = -1,
            Splitted = SplitState.Child,
            SplitParentId = 3,
        });
        await Assert.ThrowsAsync<AccessDeniedException>(() => db2.SaveChangesAsync()); // MoneyMoney-owned account
    }
}
