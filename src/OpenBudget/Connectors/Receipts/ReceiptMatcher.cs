using OpenBudget.Models;

namespace OpenBudget.Connectors.Receipts;

/// <summary>
/// A receipt to place: what was charged by card or bank (positive cents; a refund negative), when it was ordered and
/// delivered (or charged), its shop, the account its payment method is mapped to (<see cref="AccountId"/>, sources with payment methods) and
/// the accounts its source is mapped to (<see cref="Accounts"/>, see <see cref="ReceiptSourceAccount"/>).
/// </summary>
public sealed record ReceiptToMatch(long Id, long PaymentCents, DateOnly? OrderDate, DateOnly? DeliveryDate, string Shop = ReceiptShops.Rewe,
    long? AccountId = null, string? OrderNumber = null, IReadOnlyCollection<long>? Accounts = null);

/// <summary>A booking that may have paid a receipt; <see cref="Pending"/>: not booked yet, so its date is the bank's expected booking day.</summary>
public sealed record ReceiptBooking(long Id, DateOnly Date, long Cents, string? Name, string? Purpose, long? AccountId = null, bool Pending = false);

/// <summary>Which receipt got which booking, and which receipts had several equally good bookings.</summary>
public sealed record ReceiptMatchOutcome(IReadOnlyDictionary<long, long> Matches, IReadOnlySet<long> Ambiguous);

/// <summary>
/// Pairs receipts with their bookings. Which bookings fit a receipt is its shop's <see cref="ReceiptSourceProfile"/>:
/// the amount, the days around the receipt's day (the delivery day, else the order day), whether the booking must or
/// should name the shop, and a purpose text to prefer. A receipt mapped to accounts fits only bookings on them; a shop
/// without a profile has no bookings.
///
/// A receipt with exactly one free candidate takes it, when no other open receipt wants that booking; placing one can
/// settle another, so this repeats until nothing changes. Of the rest, a receipt and booking are paired when their
/// dates are at least <see cref="ClearlyCloserDays"/> days closer together than any other free pairing either of them
/// has (repeated purchases of one amount, weeks apart). A group of receipts and bookings that only fit each other is
/// paired as a whole when one pairing is clearly closer in total than any other (see <see cref="PairGroups"/>);
/// anything closer stays ambiguous.
///
/// Twins (receipts of one order with the same amount, day and account, e.g. a shop listing a charge twice) are
/// interchangeable: when no other receipt wants their bookings, they take them in order, nearest first; a twin left
/// without a booking stays unmatched.
/// </summary>
public static class ReceiptMatcher
{
    public const int ClearlyCloserDays = 7;

    public static ReceiptMatchOutcome Match(IReadOnlyList<ReceiptToMatch> receipts, IReadOnlyList<ReceiptBooking> bookings, IReadOnlySet<long>? taken = null,
        ReceiptSourceProfiles? profiles = null)
    {
        profiles ??= ReceiptSourceProfiles.Default;
        var used = new HashSet<long>(taken ?? new HashSet<long>());
        var candidates = receipts.ToDictionary(r => r.Id, r => Candidates(r, bookings, profiles));
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

    /// <summary>The bookings that fit a receipt (the preferred ones only, when any is preferred).</summary>
    public static List<ReceiptBooking> Candidates(ReceiptToMatch r, IReadOnlyList<ReceiptBooking> bookings, ReceiptSourceProfiles? profiles = null)
    {
        if ((r.DeliveryDate ?? r.OrderDate) is not { } from) return [];
        if ((profiles ?? ReceiptSourceProfiles.Default).For(r.Shop) is not { } p) return [];
        if (r.PaymentCents == 0 || (r.PaymentCents < 0 && !p.SignedAmounts)) return [];
        if (p.RequiresAccount && r.AccountId == null) return [];
        var fit = bookings.Where(b => b.Cents == -r.PaymentCents
            && (r.AccountId == null || b.AccountId == r.AccountId)
            && (r.Accounts == null || (b.AccountId is { } a && r.Accounts.Contains(a)))
            && b.Date >= from.AddDays(-p.DaysBefore) && b.Date <= from.AddDays(b.Pending ? p.PendingDaysAfter ?? p.DaysAfter : p.DaysAfter)
            && (!p.RequirePayeeMatch || p.Names(b.Name, b.Purpose))).ToList();
        if (!p.RequirePayeeMatch) fit = Prefer(fit, b => p.Names(b.Name, b.Purpose));
        if (p.PreferredPurposeToken(r) is { Length: > 0 } token)
            fit = Prefer(fit, b => b.Purpose?.Contains(token, StringComparison.Ordinal) == true);
        return fit;
    }

    private static List<ReceiptBooking> Prefer(List<ReceiptBooking> fit, Func<ReceiptBooking, bool> preferred) =>
        fit.Any(preferred) ? fit.Where(preferred).ToList() : fit;
}
