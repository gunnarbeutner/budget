using OpenBudget.Connectors.Amazon;
using OpenBudget.Connectors.LidlPlus;

namespace OpenBudget.Connectors.Receipts;

/// <summary>
/// How a shop's receipts are matched with bookings and shown. A booking fits a receipt when its amount is exactly minus
/// the payment, it lies from <see cref="DaysBefore"/> days before to <see cref="DaysAfter"/> days after the receipt's day
/// (a pending booking, dated with the bank's expected booking day: up to <see cref="PendingDaysAfter"/>), and it names the
/// shop (one of <see cref="PayeeWords"/> in its payee or purpose) when <see cref="RequirePayeeMatch"/>; otherwise bookings
/// naming the shop are only preferred over the others.
/// </summary>
public sealed record ReceiptSourceProfile
{
    /// <summary>The shop (<see cref="Models.ReceiptShops"/>) whose receipts this describes.</summary>
    public required string Shop { get; init; }

    public int DaysBefore { get; init; }

    public required int DaysAfter { get; init; }

    /// <summary>How long after the receipt a pending booking may be dated; null: as <see cref="DaysAfter"/>.</summary>
    public int? PendingDaysAfter { get; init; }

    /// <summary>Words (case-insensitive) of which one in a booking's payee or purpose names the shop.</summary>
    public required IReadOnlyList<string> PayeeWords { get; init; }

    /// <summary>True: only bookings naming the shop fit. False: they are preferred, any other booking fits when none does.</summary>
    public bool RequirePayeeMatch { get; init; } = true;

    /// <summary>
    /// Whether a receipt needs the account its payment method is mapped to (<see cref="ReceiptToMatch.AccountId"/>):
    /// without one it has no bookings.
    /// </summary>
    public bool RequiresAccount { get; init; }

    /// <summary>Whether payments may be negative (refunds, paid back as credits); otherwise only positive payments are matched.</summary>
    public bool SignedAmounts { get; init; }

    /// <summary>A text of which a booking's purpose carrying it is preferred over other fitting bookings (null: none).</summary>
    public Func<ReceiptToMatch, string?> PreferredPurposeToken { get; init; } = _ => null;

    /// <summary>A receipt's heading from its store and order numbers (the order's further numbers as stored).</summary>
    public required Func<string?, string?, string?, string> DisplayName { get; init; }

    /// <summary>For receipts read from mail: the subject's fixed start the mailbox is searched by.</summary>
    public string? MailSubjectPrefix { get; init; }

    /// <summary>For receipts read from mail: whether a subject is one of the shop's receipt mails.</summary>
    public Func<string?, bool>? IsMailSubject { get; init; }

    public int MaxPendingDaysAfter => Math.Max(DaysAfter, PendingDaysAfter ?? DaysAfter);

    /// <summary>Whether a payee or purpose names the shop.</summary>
    public bool Names(string? name, string? purpose) =>
        PayeeWords.Any(w => (name?.Contains(w, StringComparison.OrdinalIgnoreCase) ?? false) || (purpose?.Contains(w, StringComparison.OrdinalIgnoreCase) ?? false));
}

/// <summary>The shops' <see cref="ReceiptSourceProfile"/>s, one per shop; <see cref="Default"/> has every shop OpenBudget reads.</summary>
public sealed class ReceiptSourceProfiles
{
    public static readonly ReceiptSourceProfiles Default = new([ReweInvoice.MatchProfile, ReweEbon.MatchProfile, LidlPlusConnector.MatchProfile, AmazonReceiptStore.MatchProfile]);

    /// <summary>
    /// Bookings are read by timestamp, receipts compared by day: the query reaches to the end of its last day.
    /// </summary>
    private const int WholeLastDay = 1;

    private readonly Dictionary<string, ReceiptSourceProfile> _byShop;

    public ReceiptSourceProfiles(IEnumerable<ReceiptSourceProfile> profiles)
    {
        _byShop = profiles.ToDictionary(p => p.Shop, StringComparer.Ordinal);
        MaxDaysBefore = _byShop.Values.Select(p => p.DaysBefore).DefaultIfEmpty(0).Max();
        MaxDaysAfter = _byShop.Values.Select(p => p.MaxPendingDaysAfter).DefaultIfEmpty(0).Max();
    }

    public IEnumerable<ReceiptSourceProfile> All => _byShop.Values;

    /// <summary>The longest any shop's booking may come before its receipt.</summary>
    public int MaxDaysBefore { get; }

    /// <summary>The longest any shop's booking (pending ones included) may come after its receipt.</summary>
    public int MaxDaysAfter { get; }

    /// <summary>The shop's profile, null for a shop without one.</summary>
    public ReceiptSourceProfile? For(string shop) => _byShop.GetValueOrDefault(shop);

    /// <summary>
    /// The booking timestamps (Unix seconds, from inclusive, to exclusive) that may fit receipts dated from
    /// <paramref name="first"/> to <paramref name="last"/>, widened by <paramref name="slackDays"/> after.
    /// </summary>
    public (long From, long To) QueryRange(DateOnly first, DateOnly last, int slackDays = 0) =>
        (Seconds(first.AddDays(-MaxDaysBefore)), Seconds(last.AddDays(MaxDaysAfter + slackDays + WholeLastDay)));

    private static long Seconds(DateOnly day) => new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeSeconds();
}
