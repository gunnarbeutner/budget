using Microsoft.EntityFrameworkCore;
using OpenBudget.Connectors;
using OpenBudget.Connectors.FakeBank;
using OpenBudget.Connectors.Sync;
using OpenBudget.Models;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests.Connectors;

/// <summary>Edits of pending bookings survive a sync: matching, split scaling and the round trip through the store.</summary>
public sealed class PendingCarryOverTests
{
    private const long Day = 86400;
    private static readonly long T0 = BankTransactionStore.ToTimestamp(new DateOnly(2026, 6, 1));

    private static PendingEdit Edit(long cents, string? name, long ts, string? bankRef = null) =>
        new(cents, ts, name, "EUR", bankRef, null, 7, true, null, false, []);

    private static CarryCandidate Cand(long cents, string? name, long ts, string? bankRef = null) => new(cents, ts, name, bankRef, null);

    [Fact]
    public void An_exact_booking_matches()
    {
        var m = PendingCarryOver.Match([Edit(-2500, "Example Market", T0)], [Cand(-999, "Other Shop", T0), Cand(-2500, "EXAMPLE MARKET", T0)]);
        Assert.Equal(1, m[0]);
    }

    [Fact]
    public void A_changed_amount_within_tolerance_matches_and_beyond_it_does_not()
    {
        Assert.Equal(0, PendingCarryOver.Match([Edit(-5000, "Example Fuel", T0)], [Cand(-5700, "Example Fuel Station", T0)])[0]);
        Assert.Null(PendingCarryOver.Match([Edit(-5000, "Example Fuel", T0)], [Cand(-5800, "Example Fuel", T0)])[0]);
        // Small amounts: up to 1.00 apart.
        Assert.Equal(0, PendingCarryOver.Match([Edit(-300, "Example Cafe", T0)], [Cand(-400, "Example Cafe", T0)])[0]);
        Assert.Null(PendingCarryOver.Match([Edit(-300, "Example Cafe", T0)], [Cand(300, "Example Cafe", T0)])[0]);
    }

    [Fact]
    public void A_shifted_date_matches_within_the_window()
    {
        Assert.Equal(0, PendingCarryOver.Match([Edit(-2500, "Example Market", T0)], [Cand(-2500, "Example Market", T0 + (7 * Day))])[0]);
        Assert.Equal(0, PendingCarryOver.Match([Edit(-2500, "Example Market", T0)], [Cand(-2500, "Example Market", T0 - (3 * Day))])[0]);
        Assert.Null(PendingCarryOver.Match([Edit(-2500, "Example Market", T0)], [Cand(-2500, "Example Market", T0 + (8 * Day))])[0]);
        Assert.Null(PendingCarryOver.Match([Edit(-2500, "Example Market", T0)], [Cand(-2500, "Example Market", T0 - (4 * Day))])[0]);
    }

    [Fact]
    public void Two_similar_candidates_pair_up_closest_first_and_once_each()
    {
        var edits = new[] { Edit(-1000, "Example Kiosk", T0), Edit(-1200, "Example Kiosk", T0) };
        var cands = new[] { Cand(-1190, "Example Kiosk", T0 + Day), Cand(-1010, "Example Kiosk", T0 + Day), Cand(-1000, "Example Kiosk", T0 + (5 * Day)) };
        var m = PendingCarryOver.Match(edits, cands);
        Assert.Equal(2, m[0]); // exact amount wins over the closer date
        Assert.Equal(0, m[1]);
    }

    [Fact]
    public void A_bank_reference_wins_over_a_closer_amount()
    {
        var m = PendingCarryOver.Match([Edit(-1000, "Example Kiosk", T0, "REF-1")], [Cand(-1000, "Example Kiosk", T0), Cand(-1100, "Changed Name", T0, "REF-1")]);
        Assert.Equal(1, m[0]);
    }

    [Fact]
    public void A_cancelled_reservation_matches_nothing()
    {
        var m = PendingCarryOver.Match([Edit(-2500, "Corner Market", T0)], [Cand(-2500, "Sunrise Bakery", T0)]);
        Assert.Null(m[0]);
    }

    [Fact]
    public void Payees_are_alike_only_by_a_telling_word()
    {
        Assert.False(PendingCarryOver.SimilarPayee("Müller GmbH", "Schmidt GmbH"));
        Assert.False(PendingCarryOver.SimilarPayee("Corner Shop 4711", "Sunrise Shop 4711"));
        Assert.True(PendingCarryOver.SimilarPayee("REWE Markt 123", "REWE SAGT DANKE"));
        Assert.True(PendingCarryOver.SimilarPayee("Example Fuel", "EXAMPLE FUEL"));
    }

    [Fact]
    public void Split_parts_scale_with_the_remainder_on_the_largest_part()
    {
        Assert.Equal(new long[] { -2000, -1000 }, PendingCarryOver.Scale([-2000, -1000], -3000));
        Assert.Equal(new long[] { -2200, -1100 }, PendingCarryOver.Scale([-2000, -1000], -3300));
        // 1000/3 does not divide: the cent goes to the largest part.
        Assert.Equal(new long[] { -334, -333, -333 }, PendingCarryOver.Scale([-300, -300, -300], -1000));
        var scaled = PendingCarryOver.Scale([-777, -123, -100], -1234);
        Assert.Equal(-1234, scaled.Sum());
    }

    [Fact]
    public async Task Edits_of_a_pending_booking_move_to_the_booked_one_and_a_dropped_reservation_is_recorded()
    {
        await using var w = await BankWorld.CreateAsync();
        var id = await w.AddLoginAsync(BankWorld.Alice);
        await w.SyncAsync(BankWorld.Alice, id, new SyncRequest(id, false, false));
        await w.LinkAllAsync(BankWorld.Alice, id);
        var account = (await w.RemoteAsync(BankWorld.Alice, id, FakeBankConnector.GiroId)).LinkedAccountId!.Value;
        var store = new BankTransactionStore();
        var day = new DateOnly(2026, 6, 1);
        var food = RowIds.Namespace(BankWorld.SourceAlice, 9001);
        var home = RowIds.Namespace(BankWorld.SourceAlice, 9002);

        await using (var db = await w.T.System.CreateDbContextAsync())
        {
            var group = RowIds.Namespace(BankWorld.SourceAlice, 9000);
            db.CategoryGroups.Add(new CategoryGroup { Id = group, OwnerUserId = BankWorld.Alice, Name = "Example Group" });
            db.Categories.Add(new Category { Id = food, OwnerUserId = BankWorld.Alice, Name = "Example Food", GroupId = group, Type = 1 });
            db.Categories.Add(new Category { Id = home, OwnerUserId = BankWorld.Alice, Name = "Example Home", GroupId = group, Type = 1 });
            await db.SaveChangesAsync();
            await store.ApplyAsync(db, account, new AccountRefresh(null,
            [
                new(-30m, "EUR", day, null, "Example Market", "Card payment", false),
                new(-12m, "EUR", day, null, "Example Bakery", "Card payment", false),
                new(-5m, "EUR", day, null, "Example Kiosk", "Card payment", false),
            ], []), day, DateTime.UtcNow, CancellationToken.None);
        }

        // The user splits the market booking, notes the bakery one and leaves the kiosk one alone.
        await using (var db = await w.T.System.CreateDbContextAsync())
        {
            var market = await db.Transactions.SingleAsync(t => t.AccountId == account && t.Name == "Example Market");
            market.Splitted = SplitState.Parent;
            market.SplitOriginalAmount = market.Amount;
            market.Note = "Weekly shop";
            var partBase = RowIds.Namespace(BankWorld.SourceAlice, BankTransactionStore.ConnectorRowBase + 5000);
            foreach (var (i, cents, cat) in new[] { (0, -2000L, food), (1, -1000L, home) })
            {
                db.Transactions.Add(new Transaction
                {
                    Id = partBase + i,
                    OwnerUserId = market.OwnerUserId,
                    AccountId = account,
                    Amount = cents,
                    Timestamp = market.Timestamp,
                    Name = market.Name,
                    Booked = false,
                    Splitted = SplitState.Child,
                    SplitParentId = market.Id,
                    CategoryId = cat,
                    CategorySetBy = TransactionCategorySource.Manual,
                });
            }
            var bakery = await db.Transactions.SingleAsync(t => t.AccountId == account && t.Name == "Example Bakery");
            bakery.CategoryId = food;
            bakery.CategorySetBy = TransactionCategorySource.Manual;
            await db.SaveChangesAsync();
        }

        // Next sync: the market booking is booked two days later at 33.00; the bakery reservation is gone.
        await using (var db = await w.T.System.CreateDbContextAsync())
        {
            await store.ApplyAsync(db, account, new AccountRefresh(null,
            [
                new(-33m, "EUR", day.AddDays(2), null, "EXAMPLE MARKET 123", "Card payment", true, BankTransactionId: "B-1"),
                new(-5m, "EUR", day, null, "Example Kiosk", "Card payment", false),
            ], []), day, DateTime.UtcNow, CancellationToken.None);
        }

        var rows = await w.TransactionsAsync(account);
        var booked = rows.Single(t => t.ExternalId == "id:B-1");
        Assert.Equal(SplitState.Parent, booked.Splitted);
        Assert.True(booked.SplitCheck);
        Assert.Equal("Weekly shop", booked.Note);
        var parts = rows.Where(t => t.SplitParentId == booked.Id).OrderBy(t => t.Id).ToList();
        Assert.Equal(new long[] { -2200, -1100 }, parts.Select(p => p.Amount));
        Assert.Equal(new long?[] { food, home }, parts.Select(p => p.CategoryId));
        Assert.All(parts, p => Assert.True(p.SplitCheck && p.IsNew && p.Booked));
        Assert.Null(rows.Single(t => t.Name == "Example Kiosk").CategoryId);
        Assert.DoesNotContain(rows, t => t.Name == "Example Bakery");

        await using (var db = await w.T.System.CreateDbContextAsync())
        {
            var cancelled = Assert.Single(await db.CancelledReservations.ToListAsync());
            Assert.Equal("Example Bakery", cancelled.Name);
            Assert.Equal(-1200, cancelled.Amount);
            Assert.Equal(account, cancelled.AccountId);
        }
    }
}
