using OpenBudget.Connectors.Receipts;
using OpenBudget.Models;
using Xunit;

namespace OpenBudget.Tests.Receipts;

/// <summary>Pairing receipts with their direct debits.</summary>
public sealed class ReceiptMatcherTests
{
    private static readonly DateOnly Ordered = new(2026, 10, 26);
    private static readonly DateOnly Delivered = new(2026, 10, 27);

    private static ReceiptToMatch Receipt(long id = 1, long cents = 8523) => new(id, cents, Ordered, Delivered);

    private static ReceiptBooking Booking(long id, int daysAfterDelivery, long cents = -8523, string name = "REWE Markt GmbH", string? purpose = "48466003261099900881") =>
        new(id, Delivered.AddDays(daysAfterDelivery), cents, name, purpose);

    [Fact]
    public void Matches_amount_payee_and_day()
    {
        var outcome = ReceiptMatcher.Match([Receipt()], [Booking(10, 3)]);
        Assert.Equal(10, outcome.Matches[1]);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(11)]
    public void Leaves_bookings_outside_the_window(int days) =>
        Assert.Empty(ReceiptMatcher.Match([Receipt()], [Booking(10, days)]).Matches);

    [Fact]
    public void Needs_the_exact_amount_and_the_shop()
    {
        Assert.Empty(ReceiptMatcher.Match([Receipt()], [Booking(10, 2, cents: -8524)]).Matches);
        Assert.Empty(ReceiptMatcher.Match([Receipt()], [Booking(10, 2, name: "Other Shop", purpose: null)]).Matches);
        Assert.Single(ReceiptMatcher.Match([Receipt()], [Booking(10, 2, name: "Lastschrift", purpose: "REWE SAGT DANKE")]).Matches);
    }

    [Fact]
    public void Prefers_the_booking_carrying_the_order_day()
    {
        var outcome = ReceiptMatcher.Match([Receipt()], [Booking(10, 1, purpose: "48466003000000000000"), Booking(11, 3, purpose: "48466003261026300881")]);
        Assert.Equal(11, outcome.Matches[1]);
    }

    [Fact]
    public void Several_equal_bookings_are_ambiguous()
    {
        var outcome = ReceiptMatcher.Match([Receipt()], [Booking(10, 1), Booking(11, 2)]);
        Assert.Empty(outcome.Matches);
        Assert.Contains(1L, outcome.Ambiguous);
    }

    [Fact]
    public void A_taken_booking_is_not_used_again()
    {
        var outcome = ReceiptMatcher.Match([Receipt(2)], [Booking(10, 1)], new HashSet<long> { 10 });
        Assert.Empty(outcome.Matches);
        Assert.Empty(outcome.Ambiguous);
    }

    private static ReceiptToMatch Charge(long id, DateOnly day, string order) => new(id, 899, day, day, ReceiptShops.Amazon, 7, order);

    private static ReceiptBooking Card(long id, DateOnly day) => new(id, day, -899, "AMZN Mktp DE", null, 7);

    [Fact]
    public void Two_charges_of_one_amount_days_apart_pair_with_their_bookings_in_order()
    {
        var day = new DateOnly(2026, 7, 10);
        var outcome = ReceiptMatcher.Match([Charge(1, day, "A"), Charge(2, day.AddDays(4), "B")], [Card(10, day.AddDays(-1)), Card(11, day.AddDays(4))]);

        Assert.Equal(10, outcome.Matches[1]);
        Assert.Equal(11, outcome.Matches[2]);
        Assert.Empty(outcome.Ambiguous);
    }

    [Fact]
    public void Two_charges_whose_pairings_are_nearly_as_close_stay_ambiguous()
    {
        var day = new DateOnly(2026, 7, 10);
        var outcome = ReceiptMatcher.Match([Charge(1, day, "A"), Charge(2, day.AddDays(1), "B")], [Card(10, day.AddDays(2)), Card(11, day.AddDays(3))]);

        Assert.Empty(outcome.Matches);
        Assert.Equal([1L, 2L], outcome.Ambiguous.Order());
    }

    /// <summary>A made-up bakery chain: paid by card, booked up to 3 days later, its bookings name "Kornblume" or its card processor.</summary>
    private static readonly ReceiptSourceProfile Bakery = new()
    {
        Shop = "kornblume",
        DaysBefore = 1,
        DaysAfter = 3,
        PayeeWords = ["kornblume", "brotpay"],
        PreferredPurposeToken = r => r.OrderNumber,
        DisplayName = (store, _, _) => "Kornblume " + store,
    };

    private static readonly ReceiptSourceProfiles WithBakery = new(ReceiptSourceProfiles.Default.All.Append(Bakery));

    [Fact]
    public void A_shop_is_added_by_registering_its_profile()
    {
        var bun = new ReceiptToMatch(1, 420, Delivered, Delivered, "kornblume");
        ReceiptBooking Card(long id, int days, string name = "BROTPAY*Filiale 12", long cents = -420) => new(id, Delivered.AddDays(days), cents, name, null);

        Assert.Equal(10, ReceiptMatcher.Match([bun], [Card(10, 3)], profiles: WithBakery).Matches[1]);
        Assert.Equal(10, ReceiptMatcher.Match([bun], [Card(10, -1)], profiles: WithBakery).Matches[1]);
        Assert.Empty(ReceiptMatcher.Match([bun], [Card(10, 4)], profiles: WithBakery).Matches);
        Assert.Empty(ReceiptMatcher.Match([bun], [Card(10, -2)], profiles: WithBakery).Matches);
        Assert.Empty(ReceiptMatcher.Match([bun], [Card(10, 1, "REWE Markt GmbH")], profiles: WithBakery).Matches);
        Assert.Empty(ReceiptMatcher.Match([bun], [Card(10, 1, cents: 420)], profiles: WithBakery).Matches);
    }

    [Fact]
    public void A_registered_shop_prefers_its_purpose_token_and_keeps_to_mapped_accounts()
    {
        var bun = new ReceiptToMatch(1, 420, Delivered, Delivered, "kornblume", OrderNumber: "K-77", Accounts: [5]);
        var outcome = ReceiptMatcher.Match([bun], [
            new ReceiptBooking(10, Delivered, -420, "Kornblume", "Beleg K-12", 5),
            new ReceiptBooking(11, Delivered, -420, "Kornblume", "Beleg K-77", 5),
            new ReceiptBooking(12, Delivered, -420, "Kornblume", "Beleg K-77", 6),
        ], profiles: WithBakery);
        Assert.Equal(11, outcome.Matches[1]);
    }

    [Fact]
    public void A_shop_without_a_profile_has_no_bookings()
    {
        var bun = new ReceiptToMatch(1, 420, Delivered, Delivered, "kornblume");
        var booking = new ReceiptBooking(10, Delivered, -420, "Kornblume", null);
        Assert.Empty(ReceiptMatcher.Candidates(bun, [booking]));
        Assert.Single(ReceiptMatcher.Candidates(bun, [booking], WithBakery));
    }

    [Fact]
    public void Query_range_covers_every_profile()
    {
        Assert.Equal(1, new ReceiptSourceProfiles([Bakery]).MaxDaysBefore);
        Assert.Equal(3, new ReceiptSourceProfiles([Bakery]).MaxDaysAfter);
        Assert.Equal(7, WithBakery.MaxDaysBefore);
        Assert.Equal(16, WithBakery.MaxDaysAfter);
        var (from, to) = new ReceiptSourceProfiles([Bakery]).QueryRange(Delivered, Delivered);
        Assert.Equal(new DateTimeOffset(2026, 10, 26, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(), from);
        Assert.Equal(new DateTimeOffset(2026, 10, 31, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(), to);
    }
}
