using OpenBudget.Connectors.Receipts;
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
}
