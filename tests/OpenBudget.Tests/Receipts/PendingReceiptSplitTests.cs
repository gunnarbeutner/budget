using Microsoft.EntityFrameworkCore;
using OpenBudget.Connectors;
using OpenBudget.Connectors.Receipts;
using OpenBudget.Connectors.Sync;
using OpenBudget.Models;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests.Receipts;

/// <summary>Receipts splitting pending bookings, across syncs that replace the pending rows.</summary>
public sealed class PendingReceiptSplitTests
{
    private const decimal Payment = -14.28m;
    private static readonly DateOnly Day = DateOnly.FromDateTime(ReceiptWorld.Delivered.AddDays(2));

    private static async Task<TestDb> WorldAsync()
    {
        var t = new TestDb();
        await ReceiptWorld.SeedAsync(t);
        await ReceiptWorld.AllowAsync(t, ReceiptWorld.Drugstore);
        return t;
    }

    private static RemoteTransaction Rewe(decimal amount, bool booked, string? id = null) =>
        new(amount, "EUR", Day, Day, "REWE Markt GmbH", "48466003261102300881", booked, BankTransactionId: id) { IdIsUnique = id != null };

    /// <summary>One sync: the bank's report is stored, then receipts are matched.</summary>
    private static async Task SyncAsync(TestDb t, ReceiptStore store, params RemoteTransaction[] report)
    {
        await using var db = await t.System.CreateDbContextAsync();
        await new BankTransactionStore().ApplyAsync(db, ReceiptWorld.Account, new AccountRefresh(null, report, []), null, DateTime.UtcNow, default);
        await store.MatchAsync(db, ReceiptWorld.Alice, default);
    }

    private static async Task AddReceiptAsync(TestDb t, ReceiptStore store)
    {
        await using var db = await t.System.CreateDbContextAsync();
        await ReceiptStore.AddAsync(db, ReceiptWorld.Alice, ReceiptShops.Rewe, ReceiptWorld.Invoice(), "mail-1", DateTime.UtcNow, default);
        await store.MatchAsync(db, ReceiptWorld.Alice, default);
    }

    private static async Task<(Transaction Parent, List<Transaction> Parts, ReceiptInvoice Invoice, int Rows)> StateAsync(TestDb t)
    {
        await using var db = await t.System.CreateDbContextAsync();
        var rows = await db.Transactions.AsNoTracking().Where(x => x.AccountId == ReceiptWorld.Account).ToListAsync();
        var parent = rows.Single(x => x.Splitted != SplitState.Child);
        var parts = rows.Where(x => x.Splitted == SplitState.Child).OrderBy(x => x.Id).ToList();
        Assert.All(parts, p => Assert.Equal(parent.Id, p.SplitParentId));
        var invoice = await db.ReceiptInvoices.AsNoTracking().SingleAsync();
        return (parent, parts, invoice, rows.Count);
    }

    private static void AssertReceiptSplit((Transaction Parent, List<Transaction> Parts, ReceiptInvoice Invoice, int Rows) s, long cents)
    {
        Assert.Equal(SplitState.Parent, s.Parent.Splitted);
        Assert.Equal(2, s.Parts.Count);
        Assert.Equal(cents, s.Parts.Sum(p => p.Amount));
        Assert.Equal(cents, s.Parent.SplitOriginalAmount);
        Assert.Contains(s.Parts, p => p.CategoryId == ReceiptWorld.Drugstore);
        Assert.False(s.Parent.SplitCheck);
        Assert.All(s.Parts, p => Assert.False(p.SplitCheck));
        Assert.Equal((ReceiptMatchState.Matched, s.Parent.Id), (s.Invoice.State, s.Invoice.TransactionId));
        Assert.NotNull(s.Invoice.SplitSignature);
        Assert.Equal(3, s.Rows);
    }

    [Fact]
    public async Task A_pending_booking_is_split_by_its_receipt_and_keeps_the_split_across_syncs()
    {
        await using var t = await WorldAsync();
        var store = new ReceiptStore();
        await SyncAsync(t, store, Rewe(Payment, booked: false));
        await AddReceiptAsync(t, store);
        var first = await StateAsync(t);
        AssertReceiptSplit(first, -1428);
        Assert.False(first.Parent.Booked);

        // The next sync replaces the pending row: one split, on the new row, still the receipt's.
        await SyncAsync(t, store, Rewe(Payment, booked: false));
        var second = await StateAsync(t);
        AssertReceiptSplit(second, -1428);
        Assert.Equal(first.Invoice.SplitSignature, second.Invoice.SplitSignature);
        Assert.False(second.Invoice.ManualMatch);
    }

    [Fact]
    public async Task A_receipt_split_follows_the_booking_when_it_is_booked()
    {
        await using var t = await WorldAsync();
        var store = new ReceiptStore();
        await SyncAsync(t, store, Rewe(Payment, booked: false));
        await AddReceiptAsync(t, store);

        await SyncAsync(t, store, Rewe(Payment, booked: true, id: "b-1"));
        var s = await StateAsync(t);
        AssertReceiptSplit(s, -1428);
        Assert.True(s.Parent.Booked);
        Assert.All(s.Parts, p => Assert.True(p.Booked));

        // And stays as it is.
        await SyncAsync(t, store, Rewe(Payment, booked: true, id: "b-1"));
        Assert.Equal(s.Parts.Select(p => p.Id), (await StateAsync(t)).Parts.Select(p => p.Id));
    }

    [Fact]
    public async Task A_receipt_split_follows_a_booked_amount_that_differs()
    {
        await using var t = await WorldAsync();
        var store = new ReceiptStore();
        await SyncAsync(t, store, Rewe(Payment, booked: false));
        await AddReceiptAsync(t, store);

        await SyncAsync(t, store, Rewe(-15.28m, booked: true, id: "b-1"));
        var s = await StateAsync(t);
        AssertReceiptSplit(s, -1528);
        Assert.True(s.Parent.Booked);

        await SyncAsync(t, store, Rewe(-15.28m, booked: true, id: "b-1"));
        AssertReceiptSplit(await StateAsync(t), -1528);
    }

    [Fact]
    public async Task A_split_the_owner_changed_on_a_pending_booking_is_carried_over_as_theirs()
    {
        await using var t = await WorldAsync();
        var store = new ReceiptStore();
        await SyncAsync(t, store, Rewe(Payment, booked: false));
        await AddReceiptAsync(t, store);
        await using (var db = await t.System.CreateDbContextAsync())
        {
            var parts = await db.Transactions.Where(x => x.Splitted == SplitState.Child).OrderBy(x => x.Id).ToListAsync();
            parts[0].Amount += 100;
            parts[1].Amount -= 100;
            await db.SaveChangesAsync();
        }
        var edited = await StateAsync(t);

        await SyncAsync(t, store, Rewe(Payment, booked: false));
        var s = await StateAsync(t);
        Assert.Equal(edited.Parts.Select(p => (p.Amount, p.CategoryId)), s.Parts.Select(p => (p.Amount, p.CategoryId)));
        Assert.Null(s.Invoice.SplitSignature);
        Assert.Equal(s.Parent.Id, s.Invoice.TransactionId);

        // A changed amount scales and flags it, as for any split of the owner.
        await SyncAsync(t, store, Rewe(-15.28m, booked: true, id: "b-1"));
        s = await StateAsync(t);
        Assert.Equal(-1528, s.Parts.Sum(p => p.Amount));
        Assert.True(s.Parent.SplitCheck);
    }

    [Fact]
    public async Task A_pending_booking_categorized_by_hand_is_not_split_and_keeps_its_category()
    {
        await using var t = await WorldAsync();
        var store = new ReceiptStore();
        await SyncAsync(t, store, Rewe(Payment, booked: false));
        await using (var db = await t.System.CreateDbContextAsync())
        {
            var row = await db.Transactions.SingleAsync();
            row.CategoryId = ReceiptWorld.Drinks;
            row.CategorySetBy = TransactionCategorySource.Manual;
            await db.SaveChangesAsync();
        }
        await AddReceiptAsync(t, store);
        await SyncAsync(t, store, Rewe(Payment, booked: false));

        var s = await StateAsync(t);
        Assert.Equal(SplitState.Normal, s.Parent.Splitted ?? SplitState.Normal);
        Assert.Equal((ReceiptWorld.Drinks, TransactionCategorySource.Manual), (s.Parent.CategoryId, s.Parent.CategorySetBy));
        Assert.Equal(s.Parent.Id, s.Invoice.TransactionId);
        Assert.Equal(1, s.Rows);
    }

    [Fact]
    public async Task An_item_moved_on_a_pending_booking_stays_after_the_next_sync()
    {
        await using var t = await WorldAsync();
        var store = new ReceiptStore();
        await SyncAsync(t, store, Rewe(Payment, booked: false));
        await using (var db = await t.System.CreateDbContextAsync())
        {
            // Categorized by hand: only moving an item splits it.
            var row = await db.Transactions.SingleAsync();
            row.CategorySetBy = TransactionCategorySource.Manual;
            await db.SaveChangesAsync();
        }
        await AddReceiptAsync(t, store);
        Assert.Equal(1, (await StateAsync(t)).Rows);

        await using (var db = await t.System.CreateDbContextAsync())
        {
            var invoice = await db.ReceiptInvoices.Include(i => i.Items).SingleAsync();
            var item = invoice.Items.OrderBy(i => i.Position).First();
            item.CategoryId = ReceiptWorld.Drinks;
            item.CategorySource = ReceiptCategorySource.Manual;
            await db.SaveChangesAsync();
            Assert.True(await store.ApplyAsync(db, invoice, force: true, default));
        }
        var moved = await StateAsync(t);
        Assert.Contains(moved.Parts, p => p.CategoryId == ReceiptWorld.Drinks);

        await SyncAsync(t, store, Rewe(Payment, booked: false));
        var s = await StateAsync(t);
        Assert.Equal(moved.Parts.Select(p => (p.Amount, p.CategoryId)), s.Parts.Select(p => (p.Amount, p.CategoryId)));
        Assert.Equal(moved.Invoice.SplitSignature, s.Invoice.SplitSignature);
        Assert.Equal(s.Parent.Id, s.Invoice.TransactionId);
        Assert.False(s.Parent.SplitCheck);
    }
}
