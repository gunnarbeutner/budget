using OpenBudget.Connectors.Amazon;
using OpenBudget.Connectors.LidlPlus;

using OpenBudget.Models;

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

    /// <summary>The source (<see cref="Models.ReceiptSources"/>) the shop's receipts come from; null: the shop's own key.</summary>
    public string? Source { get; init; }

    /// <summary>The order's page at the shop, for shops that have one.</summary>
    public Func<string, Uri>? OrderUrl { get; init; }

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

    /// <summary>
    /// Whether payments may be negative (refunds, paid back as credits) and need a booking too; otherwise only positive
    /// payments are matched and a negative one has no booking.
    /// </summary>
    public bool SignedAmounts { get; init; }

    /// <summary>
    /// Whether each receipt carries the payment method it was paid with (of its login), mapped by the owner to the account
    /// it is booked on; otherwise the source as a whole is mapped to accounts.
    /// </summary>
    public bool PerPaymentMethod { get; init; }

    /// <summary>Payment methods (by their label) that are never booked on an account (points, gift cards).</summary>
    public Func<string, bool> IsUnbookablePaymentMethod { get; init; } = _ => false;

    /// <summary>
    /// For shops whose orders may be paid in instalments: of one order's receipts on one login, the full-price entries of
    /// its plans (they never reach an account; the instalments do). Null: the shop has no instalments.
    /// </summary>
    public Func<IReadOnlyList<ReceiptInvoice>, IEnumerable<ReceiptInvoice>>? InstalmentFullPrices { get; init; }

    public bool HasInstalments => InstalmentFullPrices != null;

    /// <summary>Whether an order's items are listed with each of its charges' receipts (one purchase per order and position).</summary>
    public bool ItemsPerOrder { get; init; }

    /// <summary>Product keys are cut to this many characters at a word (long, varying titles); null: not cut.</summary>
    public int? ProductKeyLength { get; init; }

    /// <summary>Whether the shop's receipts count as grocery shopping.</summary>
    public bool Groceries { get; init; }

    /// <summary>A text of which a booking's purpose carrying it is preferred over other fitting bookings (null: none).</summary>
    public Func<ReceiptToMatch, string?> PreferredPurposeToken { get; init; } = _ => null;

    /// <summary>A receipt's heading from its store and order numbers (the order's further numbers as stored).</summary>
    public required Func<string?, string?, string?, string> DisplayName { get; init; }

    /// <summary>For receipts read from mail: the subject's fixed start the mailbox is searched by.</summary>
    public string? MailSubjectPrefix { get; init; }

    /// <summary>For receipts read from mail: whether a subject is one of the shop's receipt mails.</summary>
    public Func<string?, bool>? IsMailSubject { get; init; }

    /// <summary>The source the shop's receipts come from.</summary>
    public string SourceKey => Source ?? Shop;

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
        InstalmentShops = _byShop.Values.Where(p => p.HasInstalments).Select(p => p.Shop).ToList();
        GroceryShops = _byShop.Values.Where(p => p.Groceries).Select(p => p.Shop).ToHashSet(StringComparer.Ordinal);
        MailShops = _byShop.Values.Where(p => p.MailSubjectPrefix != null).Select(p => p.Shop).ToList();
        MappedSources = _byShop.Values.Where(p => !p.PerPaymentMethod).Select(p => p.SourceKey).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
    }

    public IEnumerable<ReceiptSourceProfile> All => _byShop.Values;

    /// <summary>The longest any shop's booking may come before its receipt.</summary>
    public int MaxDaysBefore { get; }

    /// <summary>The longest any shop's booking (pending ones included) may come after its receipt.</summary>
    public int MaxDaysAfter { get; }

    /// <summary>The source a shop's receipts come from (a shop without a profile is its own source).</summary>
    public string SourceOf(string shop) => For(shop)?.SourceKey ?? shop;

    /// <summary>The shops whose receipts come from <paramref name="source"/>.</summary>
    public IReadOnlyList<string> ShopsOf(string source) => _byShop.Values.Where(p => p.SourceKey == source).Select(p => p.Shop).ToList();

    /// <summary>The shops read from the owner's mailbox (those with receipt mails).</summary>
    public IReadOnlyList<string> MailShops { get; }

    /// <summary>
    /// The sources mapped to accounts as a whole; a shop whose receipts need the account of their payment method
    /// (<see cref="ReceiptSourceProfile.PerPaymentMethod"/>) maps each payment method instead.
    /// </summary>
    public IReadOnlyList<string> MappedSources { get; }

    /// <summary>The shops whose orders may be paid in instalments.</summary>
    public IReadOnlyList<string> InstalmentShops { get; }

    /// <summary>The shops whose receipts count as grocery shopping.</summary>
    public IReadOnlySet<string> GroceryShops { get; }

    /// <summary>Whether the shop's receipts carry payment methods mapped one by one (see <see cref="ReceiptSourceProfile.PerPaymentMethod"/>).</summary>
    public bool PerPaymentMethod(string shop) => For(shop)?.PerPaymentMethod == true;

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
