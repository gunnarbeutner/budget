namespace OpenBudget.Connectors.Amazon;

/// <summary>A charge to place: the account its instrument is mapped to, the wallet's day and the signed cents.</summary>
public sealed record ChargeToMatch(long Id, long AccountId, DateOnly Date, long Cents);

/// <summary>
/// A booking that may be a charge's; <see cref="ShopLike"/>: payee or purpose names the shop; <see cref="Pending"/>: not
/// booked yet, so its date is the bank's expected booking day.
/// </summary>
public sealed record BookingToMatch(long Id, long AccountId, DateOnly Date, long Cents, bool ShopLike, bool Pending = false);

/// <summary>Which charge got which booking, and which charges had several equally good bookings.</summary>
public sealed record MatchOutcome(IReadOnlyDictionary<long, long> Matches, IReadOnlySet<long> Ambiguous);

/// <summary>
/// Pairs wallet charges with bookings: same account, exactly the same signed amount, booked from two days before to
/// five days after the wallet's day (a pending booking, dated with the bank's expected booking day, up to fourteen days
/// after), each booking used once. Bookings that name the shop are preferred over others.
/// A charge is matched only when exactly one booking fits and that booking fits no other open charge; placing one
/// charge can settle another, so this repeats until nothing changes. The rest is ambiguous (several fit) or unmatched.
/// </summary>
public static class AmazonMatcher
{
    public const int DaysBefore = 2;
    public const int DaysAfter = 5;
    public const int PendingDaysAfter = 14;

    public static MatchOutcome Match(IReadOnlyList<ChargeToMatch> charges, IReadOnlyList<BookingToMatch> bookings, IReadOnlySet<long>? taken = null)
    {
        var used = new HashSet<long>(taken ?? new HashSet<long>());
        var candidates = new Dictionary<long, List<BookingToMatch>>();
        foreach (var c in charges)
        {
            var fit = bookings.Where(b => b.AccountId == c.AccountId && b.Cents == c.Cents
                && b.Date >= c.Date.AddDays(-DaysBefore) && b.Date <= c.Date.AddDays(b.Pending ? PendingDaysAfter : DaysAfter)).ToList();
            if (fit.Any(b => b.ShopLike)) fit = fit.Where(b => b.ShopLike).ToList();
            candidates[c.Id] = fit;
        }
        var matches = new Dictionary<long, long>();
        bool progress;
        do
        {
            progress = false;
            var open = charges.Where(c => !matches.ContainsKey(c.Id)).ToList();
            var live = open.ToDictionary(c => c.Id, c => candidates[c.Id].Where(b => !used.Contains(b.Id)).ToList());
            foreach (var c in open)
            {
                if (live[c.Id].Where(b => !used.Contains(b.Id)).ToList() is not [var only]) continue;
                if (open.Any(o => o.Id != c.Id && !matches.ContainsKey(o.Id) && live[o.Id].Any(b => b.Id == only.Id))) continue;
                matches[c.Id] = only.Id;
                used.Add(only.Id);
                progress = true;
            }
        }
        while (progress);
        var ambiguous = charges.Where(c => !matches.ContainsKey(c.Id) && candidates[c.Id].Any(b => !used.Contains(b.Id))).Select(c => c.Id).ToHashSet();
        return new MatchOutcome(matches, ambiguous);
    }

    /// <summary>Whether a payee or purpose names Amazon.</summary>
    public static bool IsShopLike(string? name, string? purpose) =>
        Contains(name) || Contains(purpose);

    private static bool Contains(string? text) =>
        text != null && (text.Contains("amazon", StringComparison.OrdinalIgnoreCase) || text.Contains("amzn", StringComparison.OrdinalIgnoreCase));
}
