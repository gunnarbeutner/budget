using System.Globalization;
using OpenBudget.Models;

namespace OpenBudget.Connectors.Receipts;

/// <summary>
/// A receipt to place: what was charged by card or bank (positive cents; an Amazon refund negative), when it was
/// ordered and delivered (Amazon: charged), its shop and (Amazon) the account its payment method is booked on.
/// </summary>
public sealed record ReceiptToMatch(long Id, long PaymentCents, DateOnly? OrderDate, DateOnly? DeliveryDate, string Shop = ReceiptShops.Rewe, long? AccountId = null, string? OrderNumber = null);

/// <summary>A booking that may have paid a receipt; <see cref="Pending"/>: not booked yet, so its date is the bank's expected booking day.</summary>
public sealed record ReceiptBooking(long Id, DateOnly Date, long Cents, string? Name, string? Purpose, long? AccountId = null, bool Pending = false);

/// <summary>Which receipt got which booking, and which receipts had several equally good bookings.</summary>
public sealed record ReceiptMatchOutcome(IReadOnlyDictionary<long, long> Matches, IReadOnlySet<long> Ambiguous);

/// <summary>
/// Pairs supermarket receipts with their direct debits: the booking is exactly minus the payment, names the shop
/// (payee or purpose contains "REWE") and is booked from the delivery day up to <see cref="DaysAfter"/> days later
/// (from the order day when the delivery day is unknown). Among several such bookings one whose purpose contains the
/// order day as yyMMdd is preferred (REWE's direct debits carry it). A receipt is matched only when exactly one
/// booking is left and no other open receipt wants it; placing one can settle another, so this repeats until nothing
/// changes. The rest is ambiguous (several fit) or unmatched.
///
/// Lidl receipts (bought in a store, paid by card or Lidl Pay) fit a booking of exactly minus the card/app part, booked
/// on the purchase day or up to <see cref="LidlDaysAfter"/> days later (card bookings lag), whose payee or purpose
/// names Lidl or Lidl's payment companies ("S. Payment Solutions", "S. Digits").
///
/// A REWE eBon (in-store receipt) is paid by card or app on its day: the booking is exactly minus what was paid that way, names
/// REWE or PAYBACK (PAYBACK PAY), and is booked up to <see cref="EbonDaysAfter"/> days later. A receipt paid only in
/// cash has nothing to match.
///
/// An Amazon charge (or refund) fits a booking on the account its payment method is mapped to, of exactly the same
/// signed amount, booked from <see cref="AmazonDaysBefore"/> days before to <see cref="AmazonDaysAfter"/> days after the
/// charge (a pending booking, dated with the bank's expected booking day, up to <see cref="AmazonPendingDaysAfter"/>);
/// bookings that name Amazon are preferred over others. (Card accounts differ: some book days before the date Amazon
/// lists, some a week after.)
///
/// A receipt with exactly one free candidate takes it. Of the rest, a receipt and booking are paired when their dates
/// are at least <see cref="ClearlyCloserDays"/> days closer together than any other free pairing either of them has
/// (repeated purchases of one amount, weeks apart). A group of receipts and bookings that only fit each other is
/// paired as a whole when one pairing is clearly closer in total than any other (see <see cref="PairGroups"/>);
/// anything closer stays ambiguous.
///
/// Twins (receipts of one order with the same amount, day and account, e.g. a shop listing a charge twice) are
/// interchangeable: when no other receipt wants their bookings, they take them in order, nearest first; a twin left
/// without a booking stays unmatched.
/// </summary>
public static class ReceiptMatcher
{
    public const int DaysAfter = 10;
    public const int EbonDaysAfter = 10;

    public const int LidlDaysAfter = 16;

    public const int ClearlyCloserDays = 7;
    public const int AmazonDaysBefore = 7;
    public const int AmazonDaysAfter = 9;
    public const int AmazonPendingDaysAfter = 14;

    /// <summary>The longest any shop's booking may come after its receipt.</summary>
    public const int MaxDaysAfter = AmazonPendingDaysAfter;

    /// <summary>The longest any shop's booking may come before its receipt.</summary>
    public const int MaxDaysBefore = AmazonDaysBefore;

    private static readonly string[] LidlPayees = ["lidl", "s. payment solutions", "s.payment solutions", "s. digits", "s.digits"];

    public static ReceiptMatchOutcome Match(IReadOnlyList<ReceiptToMatch> receipts, IReadOnlyList<ReceiptBooking> bookings, IReadOnlySet<long>? taken = null)
    {
        var used = new HashSet<long>(taken ?? new HashSet<long>());
        var candidates = receipts.ToDictionary(r => r.Id, r => Candidates(r, bookings));
        var matches = new Dictionary<long, long>();
        bool progress;
        do
        {
            progress = false;
            var open = receipts.Where(r => !matches.ContainsKey(r.Id)).ToList();
            var live = open.ToDictionary(r => r.Id, r => candidates[r.Id].Where(b => !used.Contains(b.Id)).ToList());
            foreach (var r in open)
            {
                if (live[r.Id].Where(b => !used.Contains(b.Id)).ToList() is not [var only]) continue;
                if (open.Any(o => o.Id != r.Id && !matches.ContainsKey(o.Id) && live[o.Id].Any(b => b.Id == only.Id))) continue;
                matches[r.Id] = only.Id;
                used.Add(only.Id);
                progress = true;
            }
        }
        while (progress);

        // Closest dates first: a pair counts only when neither side has another free pairing as close.
        var pairs = receipts.Where(r => !matches.ContainsKey(r.Id))
            .SelectMany(r => candidates[r.Id].Where(b => !used.Contains(b.Id)).Select(b => (R: r.Id, B: b.Id, Gap: Gap(r, b))))
            .ToList();
        foreach (var p in pairs.OrderBy(p => p.Gap))
        {
            if (matches.ContainsKey(p.R) || used.Contains(p.B)) continue;
            var rivals = pairs.Where(o => (o.R == p.R) != (o.B == p.B) && !matches.ContainsKey(o.R) && !used.Contains(o.B));
            if (rivals.Any(o => o.Gap < p.Gap + ClearlyCloserDays)) continue;
            matches[p.R] = p.B;
            used.Add(p.B);
        }
        PairGroups(receipts, candidates, matches, used);
        foreach (var twins in receipts.Where(r => !matches.ContainsKey(r.Id) && r.OrderNumber != null)
            .GroupBy(r => (r.OrderNumber, r.PaymentCents, r.AccountId, Day: r.DeliveryDate ?? r.OrderDate))
            .Where(g => g.Count() > 1))
        {
            var ids = twins.Select(r => r.Id).ToHashSet();
            var free = twins.SelectMany(r => candidates[r.Id]).Where(b => !used.Contains(b.Id)).DistinctBy(b => b.Id).ToList();
            if (free.Count == 0) continue;
            if (receipts.Any(o => !ids.Contains(o.Id) && !matches.ContainsKey(o.Id) && candidates[o.Id].Any(b => free.Any(f => f.Id == b.Id)))) continue;
            var first = twins.First();
            var ordered = free.OrderBy(b => Gap(first, b)).ThenBy(b => b.Date).ThenBy(b => b.Id).ToList();
            foreach (var (r, b) in twins.OrderBy(r => r.Id).Zip(ordered))
            {
                matches[r.Id] = b.Id;
                used.Add(b.Id);
            }
        }
        var ambiguous = receipts.Where(r => !matches.ContainsKey(r.Id) && candidates[r.Id].Any(b => !used.Contains(b.Id))).Select(r => r.Id).ToHashSet();
        return new ReceiptMatchOutcome(matches, ambiguous);
    }

    /// <summary>The most receipts (and bookings) a group may have to be paired as a whole.</summary>
    private const int MaxGroup = 6;

    /// <summary>
    /// Groups of open receipts and free bookings that only fit each other, as many receipts as bookings: when one way to
    /// pair them all has dates at least <see cref="ClearlyCloserDays"/> days closer in total than every other way, the
    /// group is paired that way (two charges of one amount a few days apart and their two bookings).
    /// </summary>
    private static void PairGroups(IReadOnlyList<ReceiptToMatch> receipts, Dictionary<long, List<ReceiptBooking>> candidates,
        Dictionary<long, long> matches, HashSet<long> used)
    {
        var open = receipts.Where(r => !matches.ContainsKey(r.Id)).ToList();
        var edges = open.ToDictionary(r => r.Id, r => candidates[r.Id].Where(b => !used.Contains(b.Id)).ToList());
        var seen = new HashSet<long>();
        foreach (var start in open.Where(r => edges[r.Id].Count > 0))
        {
            if (!seen.Add(start.Id)) continue;
            // The connected group: receipts linked through bookings they share.
            var group = new List<ReceiptToMatch> { start };
            var books = new Dictionary<long, ReceiptBooking>();
            for (var i = 0; i < group.Count; i++)
                foreach (var b in edges[group[i].Id])
                {
                    if (!books.TryAdd(b.Id, b)) continue;
                    foreach (var o in open.Where(o => edges[o.Id].Any(x => x.Id == b.Id) && seen.Add(o.Id))) group.Add(o);
                }
            if (group.Count < 2 || group.Count != books.Count || group.Count > MaxGroup) continue;

            int best = int.MaxValue, second = int.MaxValue;
            long[]? bestPick = null;
            void Walk(int i, long[] pick, HashSet<long> taken, int total)
            {
                if (total >= second) return;
                if (i == group.Count)
                {
                    if (total < best) { second = best; best = total; bestPick = (long[])pick.Clone(); }
                    else second = total;
                    return;
                }
                foreach (var b in edges[group[i].Id].Where(b => !taken.Contains(b.Id)))
                {
                    pick[i] = b.Id;
                    taken.Add(b.Id);
                    Walk(i + 1, pick, taken, total + Gap(group[i], b));
                    taken.Remove(b.Id);
                }
            }
            Walk(0, new long[group.Count], [], 0);
            if (bestPick == null || (second != int.MaxValue && second < best + ClearlyCloserDays)) continue;
            for (var i = 0; i < group.Count; i++)
            {
                matches[group[i].Id] = bestPick[i];
                used.Add(bestPick[i]);
            }
        }
    }

    private static int Gap(ReceiptToMatch r, ReceiptBooking b) =>
        (r.DeliveryDate ?? r.OrderDate) is { } from ? Math.Abs(b.Date.DayNumber - from.DayNumber) : int.MaxValue;

    /// <summary>The bookings that fit a receipt (the preferred ones only, when any carries the order day).</summary>
    public static List<ReceiptBooking> Candidates(ReceiptToMatch r, IReadOnlyList<ReceiptBooking> bookings)
    {
        if ((r.DeliveryDate ?? r.OrderDate) is not { } from) return [];
        if (r.Shop == ReceiptShops.Amazon)
        {
            var amazon = bookings.Where(b => r.AccountId != null && b.AccountId == r.AccountId && r.PaymentCents != 0 && b.Cents == -r.PaymentCents
                && b.Date >= from.AddDays(-AmazonDaysBefore) && b.Date <= from.AddDays(b.Pending ? AmazonPendingDaysAfter : AmazonDaysAfter)).ToList();
            return amazon.Any(b => IsShop(r.Shop, b.Name, b.Purpose)) ? amazon.Where(b => IsShop(r.Shop, b.Name, b.Purpose)).ToList() : amazon;
        }
        var days = r.Shop switch { ReceiptShops.Lidl => LidlDaysAfter, ReceiptShops.ReweEbon => EbonDaysAfter, _ => DaysAfter };
        var fit = bookings.Where(b => r.PaymentCents > 0 && b.Cents == -r.PaymentCents && IsShop(r.Shop, b.Name, b.Purpose)
            && b.Date >= from && b.Date <= from.AddDays(days)).ToList();
        if (r.Shop != ReceiptShops.Rewe) return fit;
        if (r.OrderDate is { } ordered)
        {
            var day = ordered.ToString("yyMMdd", CultureInfo.InvariantCulture);
            var preferred = fit.Where(b => b.Purpose?.Contains(day, StringComparison.Ordinal) == true).ToList();
            if (preferred.Count > 0) return preferred;
        }
        return fit;
    }

    /// <summary>Whether a payee or purpose names PAYBACK (payments with PAYBACK PAY).</summary>
    public static bool IsPayback(string? name, string? purpose) =>
        (name?.Contains("payback", StringComparison.OrdinalIgnoreCase) ?? false) || (purpose?.Contains("payback", StringComparison.OrdinalIgnoreCase) ?? false);

    /// <summary>Whether a payee or purpose names REWE.</summary>
    public static bool IsShop(string? name, string? purpose) => IsShop(ReceiptShops.Rewe, name, purpose);

    /// <summary>Whether a payee or purpose names the shop (Lidl: Lidl or its payment companies; REWE eBons: REWE or PAYBACK; Amazon: Amazon or AMZN).</summary>
    public static bool IsShop(string shop, string? name, string? purpose)
    {
        IReadOnlyList<string> words = shop switch
        {
            ReceiptShops.Lidl => LidlPayees,
            ReceiptShops.ReweEbon => ["rewe", "payback"],
            ReceiptShops.Amazon => ["amazon", "amzn"],
            _ => ["rewe"],
        };
        return words.Any(w => (name?.Contains(w, StringComparison.OrdinalIgnoreCase) ?? false) || (purpose?.Contains(w, StringComparison.OrdinalIgnoreCase) ?? false));
    }
}
