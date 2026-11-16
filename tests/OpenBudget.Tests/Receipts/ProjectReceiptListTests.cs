using Microsoft.EntityFrameworkCore;
using OpenBudget.Connectors.Receipts;
using OpenBudget.Models;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests.Receipts;

/// <summary>
/// A project's receipt list and its review badge (ReceiptService.ListProjectAsync, CountProjectReviewAsync): which receipts
/// each user sees in which project, and the status chips the rows fall under.
/// </summary>
public sealed class ProjectReceiptListTests
{
    private const string Card = "visa-2222";
    private static readonly long Booking = AccessWorld.Id(AccessWorld.SourceAlice, 40);
    private static readonly long PrivateBooking = AccessWorld.Id(AccessWorld.SourceAlice, 41);
    private static readonly DateTime Day = new(2026, 3, 12, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Bob's Amazon login with one card mapped to Alice's shared account; Bob's receipt matched onto Alice's booking there,
    /// and an older one of Bob's still unmatched.
    /// </summary>
    private static async Task<(TestDb T, int Login, long Matched, long Open)> WorldAsync()
    {
        var t = await AccessWorld.BuildAsync();
        await using var db = await t.System.CreateDbContextAsync();
        db.Transactions.Add(new Transaction
        {
            Id = Booking,
            OwnerUserId = AccessWorld.Alice,
            AccountId = AccessWorld.AccountWp,
            Amount = -10000,
            Timestamp = new DateTimeOffset(Day.AddDays(1)).ToUnixTimeSeconds(),
            Name = "AMAZON EU",
        });
        db.Transactions.Add(new Transaction
        {
            Id = PrivateBooking,
            OwnerUserId = AccessWorld.Alice,
            AccountId = AccessWorld.AccountPrivate,
            Amount = -2000,
            Timestamp = new DateTimeOffset(Day).ToUnixTimeSeconds(),
            Name = "Example shop",
        });
        var login = new BankLogin { OwnerUserId = AccessWorld.Bob, ConnectorType = "amazon", DisplayName = "Shop" };
        db.BankLogins.Add(login);
        await db.SaveChangesAsync();
        db.BankLoginAccounts.Add(new BankLoginAccount { BankLoginId = login.Id, RemoteId = Card, Name = "Visa •••• 2222", EnrichAccountId = AccessWorld.AccountWp });
        var matched = Receipt(AccessWorld.Bob, login.Id, "302-0000000-0000001", 10000, Day, ReceiptMatchState.Matched, Booking);
        var open = Receipt(AccessWorld.Bob, login.Id, "302-0000000-0000002", 4500, Day.AddDays(-30), ReceiptMatchState.Unmatched, null);
        db.ReceiptInvoices.AddRange(matched, open);
        await db.SaveChangesAsync();
        return (t, login.Id, matched.Id, open.Id);
    }

    private static ReceiptInvoice Receipt(string owner, int? login, string order, long cents, DateTime day, ReceiptMatchState state, long? booking, string? key = null) => new()
    {
        OwnerUserId = owner,
        Shop = ReceiptShops.Amazon,
        BankLoginId = login,
        SourceKey = key ?? order + "-" + cents,
        InvoiceNumber = key ?? order + "-" + cents,
        OrderNumber = order,
        PaymentMethodId = Card,
        TotalCents = cents,
        PaymentCents = cents,
        DeliveryDate = day,
        ReadAt = day,
        State = state,
        TransactionId = booking,
        ManualMatch = booking != null,
    };

    private static ReceiptService Receipts(TestDb t, string user)
    {
        var view = t.ForUser(user);
        return new ReceiptService(view.Factory, t.System, view.Access, null!, new ReceiptStore(), null!, t.Notifier, new ChangeOrigin(), TimeProvider.System, TestRegistry.Connectors);
    }

    [Fact]
    public async Task The_account_owner_sees_another_users_receipt_on_her_booking_and_his_open_receipts_read_only()
    {
        var (t, _, matched, open) = await WorldAsync();
        await using var _t = t;
        var rows = await Receipts(t, AccessWorld.Alice).ListProjectAsync(AccessWorld.Shared);
        Assert.Equal(new[] { matched, open }.Order(), rows.Select(r => r.Id).Order());
        var row = rows.Single(r => r.Id == matched);
        Assert.Equal((false, Booking), (row.IsOwn, row.TransactionId!.Value));
        // his card is mapped to her account: his open charge shows, but is not hers to assign
        Assert.False(rows.Single(r => r.Id == open).IsOwn);
        Assert.Equal(0, await Receipts(t, AccessWorld.Alice).CountProjectReviewAsync(AccessWorld.Shared));
    }

    [Fact]
    public async Task The_receipt_owner_sees_his_receipt_and_his_open_receipts_of_a_source_booking_into_the_project()
    {
        var (t, _, matched, open) = await WorldAsync();
        await using var _t = t;
        var rows = await Receipts(t, AccessWorld.Bob).ListProjectAsync(AccessWorld.Shared);
        Assert.Equal(new[] { matched, open }.Order(), rows.Select(r => r.Id).Order());
        Assert.All(rows, r => Assert.True(r.IsOwn));
        Assert.Equal(ReceiptReviewState.Unmatched, rows.Single(r => r.Id == open).State);
        Assert.Equal(1, await Receipts(t, AccessWorld.Bob).CountProjectReviewAsync(AccessWorld.Shared));

        // His own project gets no receipts: the source books into the shared account only.
        Assert.Empty(await Receipts(t, AccessWorld.Bob).ListProjectAsync(AccessWorld.BobProject));
    }

    [Fact]
    public async Task A_project_the_user_may_not_see_lists_nothing()
    {
        var (t, _, _, _) = await WorldAsync();
        await using var _t = t;
        await Assert.ThrowsAsync<AccessDeniedException>(() => Receipts(t, AccessWorld.Bob).ListProjectAsync(AccessWorld.Private));
        Assert.Equal(0, await Receipts(t, AccessWorld.Bob).CountProjectReviewAsync(AccessWorld.Private));
        Assert.Empty(await Receipts(t, AccessWorld.Alice).ListProjectAsync(AccessWorld.Private));
    }

    [Fact]
    public async Task Another_users_receipt_on_a_booking_he_cannot_see_shows_only_to_whoever_sees_the_booking()
    {
        var (t, login, _, _) = await WorldAsync();
        await using var _t = t;
        long id;
        await using (var db = await t.System.CreateDbContextAsync())
        {
            var r = Receipt(AccessWorld.Bob, login, "302-0000000-0000003", 2000, Day, ReceiptMatchState.Matched, PrivateBooking);
            db.ReceiptInvoices.Add(r);
            await db.SaveChangesAsync();
            id = r.Id;
        }
        Assert.Contains(await Receipts(t, AccessWorld.Alice).ListProjectAsync(AccessWorld.Private), r => r.Id == id && !r.IsOwn);
        Assert.DoesNotContain(await Receipts(t, AccessWorld.Alice).ListProjectAsync(AccessWorld.Shared), r => r.Id == id);
        Assert.DoesNotContain(await Receipts(t, AccessWorld.Bob).ListProjectAsync(AccessWorld.Shared), r => r.Id == id);
    }

    [Fact]
    public async Task Once_the_share_is_gone_the_project_lists_nothing_for_the_former_member()
    {
        var (t, _, _, _) = await WorldAsync();
        await using var _t = t;
        await using (var db = await t.System.CreateDbContextAsync())
        {
            db.ProjectShares.RemoveRange(db.ProjectShares);
            await db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<AccessDeniedException>(() => Receipts(t, AccessWorld.Bob).ListProjectAsync(AccessWorld.Shared));
    }

    [Fact]
    public async Task Receipts_without_a_booking_show_in_the_project_their_card_pays_into()
    {
        var (t, login, _, _) = await WorldAsync();
        await using var _t = t;
        long dismissed, skipped, free;
        await using (var db = await t.System.CreateDbContextAsync())
        {
            var d = Receipt(AccessWorld.Bob, login, "302-0000000-0000005", 1500, Day, ReceiptMatchState.Dismissed, null);
            var s = Receipt(AccessWorld.Bob, login, "302-0000000-0000006", 2500, Day, ReceiptMatchState.Skipped, null);
            var f = Receipt(AccessWorld.Bob, login, "302-0000000-0000007", 0, Day, ReceiptMatchState.Skipped, null);
            db.ReceiptInvoices.AddRange(d, s, f);
            await db.SaveChangesAsync();
            (dismissed, skipped, free) = (d.Id, s.Id, f.Id);
        }
        foreach (var user in new[] { AccessWorld.Bob, AccessWorld.Alice })
        {
            var rows = await Receipts(t, user).ListProjectAsync(AccessWorld.Shared);
            Assert.Equal(ReceiptReviewGroup.NoBooking, ReceiptService.GroupOf(rows.Single(r => r.Id == dismissed).State, 1500));
            Assert.Equal(ReceiptReviewGroup.NoBooking, ReceiptService.GroupOf(rows.Single(r => r.Id == skipped).State, 2500));
            // nothing paid: done
            Assert.Equal(ReceiptReviewGroup.Done, ReceiptService.GroupOf(rows.Single(r => r.Id == free).State, 0));
        }
        Assert.Empty(await Receipts(t, AccessWorld.Bob).ListProjectAsync(AccessWorld.BobProject));
        // they need no review: the badge still counts only the older unmatched receipt
        Assert.Equal(1, await Receipts(t, AccessWorld.Bob).CountProjectReviewAsync(AccessWorld.Shared));
    }

    [Theory]
    [InlineData(ReceiptReviewState.Matched, 1000, ReceiptReviewGroup.Done)]
    [InlineData(ReceiptReviewState.Split, 1000, ReceiptReviewGroup.Done)]
    [InlineData(ReceiptReviewState.Instalments, 9000, ReceiptReviewGroup.Done)]
    [InlineData(ReceiptReviewState.NoBooking, 0, ReceiptReviewGroup.Done)]
    [InlineData(ReceiptReviewState.Unmatched, 0, ReceiptReviewGroup.Done)]
    [InlineData(ReceiptReviewState.NoBooking, 1000, ReceiptReviewGroup.NoBooking)]
    [InlineData(ReceiptReviewState.Dismissed, 1000, ReceiptReviewGroup.NoBooking)]
    [InlineData(ReceiptReviewState.Waiting, 1000, ReceiptReviewGroup.Waiting)]
    [InlineData(ReceiptReviewState.InstalmentsPending, 9000, ReceiptReviewGroup.Waiting)]
    [InlineData(ReceiptReviewState.Ambiguous, 1000, ReceiptReviewGroup.Review)]
    [InlineData(ReceiptReviewState.Unmatched, 1000, ReceiptReviewGroup.Review)]
    public void A_receipt_falls_under_its_status_chip(ReceiptReviewState state, long cents, ReceiptReviewGroup group) =>
        Assert.Equal(group, ReceiptService.GroupOf(state, cents));

    [Fact]
    public async Task A_zero_payment_receipt_is_listed_as_done()
    {
        var (t, login, _, _) = await WorldAsync();
        await using var _t = t;
        await using (var db = await t.System.CreateDbContextAsync())
        {
            db.ReceiptInvoices.Add(Receipt(AccessWorld.Bob, login, "302-0000000-0000004", 0, Day, ReceiptMatchState.Unmatched, null));
            await db.SaveChangesAsync();
        }
        var row = (await Receipts(t, AccessWorld.Bob).ListAllAsync()).Single(r => r.PaymentCents == 0);
        Assert.Equal(ReceiptReviewGroup.Done, ReceiptService.GroupOf(row.State, row.PaymentCents));
    }

    [Fact]
    public async Task An_instalment_order_waits_until_all_its_instalments_are_charged_and_matched()
    {
        var (t, login, _, _) = await WorldAsync();
        await using var _t = t;
        const string order = "302-0000000-0000009";
        long full;
        await using (var db = await t.System.CreateDbContextAsync())
        {
            var f = Receipt(AccessWorld.Bob, login, order, 9000, Day, ReceiptMatchState.Instalments, null);
            db.ReceiptInvoices.Add(f);
            db.ReceiptInvoices.Add(Receipt(AccessWorld.Bob, login, order, 3000, Day, ReceiptMatchState.Matched, null, "i1"));
            db.ReceiptInvoices.Add(Receipt(AccessWorld.Bob, login, order, 3000, Day.AddMonths(1), ReceiptMatchState.Matched, null, "i2"));
            await db.SaveChangesAsync();
            full = f.Id;
        }
        var row = (await Receipts(t, AccessWorld.Bob).ListAllAsync()).Single(r => r.Id == full);
        Assert.Equal(ReceiptReviewState.InstalmentsPending, row.State);
        Assert.Equal(ReceiptReviewGroup.Waiting, ReceiptService.GroupOf(row.State, row.PaymentCents));

        await using (var db = await t.System.CreateDbContextAsync())
        {
            db.ReceiptInvoices.Add(Receipt(AccessWorld.Bob, login, order, 3000, Day.AddMonths(2), ReceiptMatchState.Matched, null, "i3"));
            await db.SaveChangesAsync();
        }
        row = (await Receipts(t, AccessWorld.Bob).ListAllAsync()).Single(r => r.Id == full);
        Assert.Equal(ReceiptReviewState.Instalments, row.State);
        Assert.Equal(ReceiptReviewGroup.Done, ReceiptService.GroupOf(row.State, row.PaymentCents));
    }
}
