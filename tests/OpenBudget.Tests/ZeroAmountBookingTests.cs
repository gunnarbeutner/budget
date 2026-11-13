#pragma warning disable CA2000 // test objects live for the test
using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests;

/// <summary>
/// Bookings of exactly 0.00: never uncategorized, and a zero booking next to its main booking carries the main's category.
/// </summary>
public sealed class ZeroAmountBookingTests
{
    private static readonly long Shopping = AccessWorld.Id(AccessWorld.SourceAlice, 220);
    private static readonly long Books = AccessWorld.Id(AccessWorld.SourceAlice, 221);
    private static readonly long Day = new DateTimeOffset(2026, 6, 10, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

    private static Transaction Row(long id, long cents, string payee, long dayOffset = 0, long? account = null, long? category = null) => new()
    {
        Id = id,
        OwnerUserId = AccessWorld.Alice,
        AccountId = account ?? AccessWorld.AccountWp,
        Amount = cents,
        Name = payee,
        CategoryId = category,
        CategorySetBy = category == null ? TransactionCategorySource.Import : TransactionCategorySource.Manual,
        Timestamp = Day + dayOffset * 86400,
    };

    private static async Task<TestDb> WorldAsync(params Transaction[] rows)
    {
        var t = await AccessWorld.BuildAsync();
        await using var db = await t.System.CreateDbContextAsync();
        (await db.Accounts.SingleAsync(a => a.Id == AccessWorld.AccountWp)).Provider = "fake";
        db.Categories.Add(new Category { Id = Shopping, OwnerUserId = AccessWorld.Alice, Name = "Shopping", GroupId = AccessWorld.SharedGroup, Type = 1 });
        db.Categories.Add(new Category { Id = Books, OwnerUserId = AccessWorld.Alice, Name = "Books", GroupId = AccessWorld.SharedGroup, Type = 1 });
        db.Transactions.AddRange(rows);
        await db.SaveChangesAsync();
        return t;
    }

    private static async Task<Transaction> LoadAsync(TestDb t, long id)
    {
        await using var db = await t.System.CreateDbContextAsync();
        return await db.Transactions.AsNoTracking().SingleAsync(x => x.Id == id);
    }

    private static TransactionEditService EditFor(TestDb t) => new(t.ForUser(AccessWorld.Alice).Factory, t.Notifier, new ChangeOrigin());

    // ---- finding the main booking ----

    [Fact]
    public void The_only_nearby_booking_is_the_main()
    {
        var zero = Row(1, 0, "Redeemed bonus points");
        var main = Row(2, -1060, "WWW.EXAMPLESHOP.DE", dayOffset: 1);
        Assert.Same(main, ZeroCompanions.FindMain(zero, [main, Row(3, -500, "Far away", dayOffset: 2), Row(4, -500, "Other account", account: AccessWorld.AccountPrivate)]));
    }

    [Fact]
    public void Among_several_the_one_sharing_a_merchant_word_wins()
    {
        var zero = Row(1, 0, "Eingeloeste Exampleshop Punkte");
        var main = Row(2, -1060, "WWW.EXAMPLESHOP.DE");
        Assert.Same(main, ZeroCompanions.FindMain(zero, [Row(3, -500, "Sample Bakery"), main]));
    }

    [Fact]
    public void Ambiguous_or_missing_mains_give_none()
    {
        var zero = Row(1, 0, "Eingeloeste Exampleshop Punkte");
        Assert.Null(ZeroCompanions.FindMain(zero, [Row(2, -100, "Exampleshop one"), Row(3, -200, "Exampleshop two")]));
        Assert.Null(ZeroCompanions.FindMain(zero, [Row(2, -100, "Sample Bakery"), Row(3, -200, "Sample Florist")]));
        Assert.Null(ZeroCompanions.FindMain(zero, [Row(4, 0, "Another zero")]));
    }

    // ---- uncategorized and suggestions ----

    [Fact]
    public async Task Zero_bookings_never_count_as_uncategorized()
    {
        await using var t = await WorldAsync(Row(1001, 0, "Zero deposit"), Row(1002, -300, "Sample Bakery", dayOffset: 10));
        await using var db = await t.System.CreateDbContextAsync();
        var ids = await db.Transactions.WhereUncategorized(db, AccessWorld.Alice).Select(x => x.Id).ToListAsync();
        Assert.Contains(1002, ids);
        Assert.DoesNotContain(1001, ids);
    }

    // ---- following the main ----

    [Fact]
    public async Task A_companion_follows_its_main_when_it_gets_changes_and_loses_a_category()
    {
        await using var t = await WorldAsync(Row(1001, -1060, "WWW.EXAMPLESHOP.DE"), Row(1002, 0, "Eingeloeste Exampleshop Punkte"));
        var edit = EditFor(t);

        await edit.SetCategoryAsync([1001], Shopping);
        var zero = await LoadAsync(t, 1002);
        Assert.Equal((Shopping, TransactionCategorySource.Follow), (zero.CategoryId, zero.CategorySetBy));

        await edit.SetCategoryAsync([1001], Books);
        Assert.Equal(Books, (await LoadAsync(t, 1002)).CategoryId);

        await edit.SetCategoryAsync([1001], null);
        Assert.Null((await LoadAsync(t, 1002)).CategoryId);
    }

    [Fact]
    public async Task A_companion_categorized_by_hand_keeps_its_category()
    {
        await using var t = await WorldAsync(Row(1001, -1060, "WWW.EXAMPLESHOP.DE"), Row(1002, 0, "Eingeloeste Exampleshop Punkte"));
        var edit = EditFor(t);
        await edit.SetCategoryAsync([1002], Books);

        await edit.SetCategoryAsync([1001], Shopping);

        var zero = await LoadAsync(t, 1002);
        Assert.Equal((Books, TransactionCategorySource.Manual), (zero.CategoryId, zero.CategorySetBy));
    }

    [Fact]
    public async Task An_arriving_companion_takes_the_mains_category()
    {
        await using var t = await WorldAsync(Row(1001, -1060, "WWW.EXAMPLESHOP.DE", category: Shopping));
        await using (var db = await t.System.CreateDbContextAsync())
        {
            db.Transactions.Add(Row(1002, 0, "Eingeloeste Exampleshop Punkte", dayOffset: 1));
            await db.SaveChangesAsync();
        }
        var zero = await LoadAsync(t, 1002);
        Assert.Equal((Shopping, TransactionCategorySource.Follow), (zero.CategoryId, zero.CategorySetBy));
    }

    [Fact]
    public async Task The_backfill_categorizes_uncategorized_companions_once()
    {
        await using var t = await WorldAsync(
            Row(1001, -1060, "WWW.EXAMPLESHOP.DE"), Row(1002, 0, "Eingeloeste Exampleshop Punkte"),
            Row(1003, -900, "Sample Bakery", dayOffset: 20), Row(1004, 0, "Bakery bonus", dayOffset: 20, category: Books));
        await using var db = await t.System.CreateDbContextAsync();
        // A category written past the change tracker, as data from before companions followed.
        await db.Transactions.Where(x => x.Id == 1001 || x.Id == 1003).ExecuteUpdateAsync(u => u.SetProperty(x => x.CategoryId, Shopping));

        Assert.Equal(1, await ZeroCompanions.FollowAsync(db, null, null, null, CancellationToken.None));
        await db.SaveChangesAsync();
        Assert.Equal(0, await ZeroCompanions.FollowAsync(db, null, null, null, CancellationToken.None));

        Assert.Equal(Shopping, (await LoadAsync(t, 1002)).CategoryId);
        Assert.Equal(Books, (await LoadAsync(t, 1004)).CategoryId);
    }
}
