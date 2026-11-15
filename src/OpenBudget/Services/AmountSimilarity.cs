namespace OpenBudget.Services;

/// <summary>
/// When two amounts in cents count as the same: at most <see cref="MinCents"/> apart, or within <see cref="Percent"/>
/// of the reference amount (the larger of the two, or the first one). With <see cref="SameSign"/>, a zero first amount
/// or amounts of opposite sign never match.
/// </summary>
public sealed record AmountSimilarity(int Percent, long MinCents, bool RelativeToLarger, bool SameSign)
{
    /// <summary>Charges of one recurring series: within 5 % of the larger amount, at least 10 cents.</summary>
    public static AmountSimilarity Recurring { get; } = new(5, 10, RelativeToLarger: true, SameSign: false);

    /// <summary>Bookings compared for categorizing: within 1 % of the first amount, at least a cent, same sign.</summary>
    public static AmountSimilarity Booking { get; } = new(1, 1, RelativeToLarger: false, SameSign: true);

    public bool Same(long a, long b)
    {
        if (SameSign && (a == 0 || Math.Sign(a) != Math.Sign(b))) return false;
        var diff = Math.Abs(a - b);
        if (diff <= MinCents) return true;
        var reference = RelativeToLarger ? Math.Max(Math.Abs(a), Math.Abs(b)) : Math.Abs(a);
        return diff * 100 <= reference * Percent;
    }
}
