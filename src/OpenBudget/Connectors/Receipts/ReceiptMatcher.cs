using System.Globalization;
using OpenBudget.Models;

namespace OpenBudget.Connectors.Receipts;

/// <summary>A receipt to place: what was charged by card or bank (positive cents), when it was ordered and delivered, and its shop.</summary>
public sealed record ReceiptToMatch(long Id, long PaymentCents, DateOnly? OrderDate, DateOnly? DeliveryDate, string Shop = ReceiptShops.Rewe);

/// <summary>A booking that may have paid a receipt.</summary>
public sealed record ReceiptBooking(long Id, DateOnly Date, long Cents, string? Name, string? Purpose);

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
/// </summary>
public static class ReceiptMatcher
{
    public const int DaysAfter = 10;
    public const int EbonDaysAfter = 5;

    public const int LidlDaysAfter = 5;

    /// <summary>The longest any shop's booking may come after its receipt.</summary>
    public const int MaxDaysAfter = DaysAfter;

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
        var ambiguous = receipts.Where(r => !matches.ContainsKey(r.Id) && candidates[r.Id].Any(b => !used.Contains(b.Id))).Select(r => r.Id).ToHashSet();
        return new ReceiptMatchOutcome(matches, ambiguous);
    }

    /// <summary>The bookings that fit a receipt (the preferred ones only, when any carries the order day).</summary>
    public static List<ReceiptBooking> Candidates(ReceiptToMatch r, IReadOnlyList<ReceiptBooking> bookings)
    {
        if ((r.DeliveryDate ?? r.OrderDate) is not { } from) return [];
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

    /// <summary>Whether a payee or purpose names the shop (Lidl: Lidl or its payment companies; REWE eBons: REWE or PAYBACK).</summary>
    public static bool IsShop(string shop, string? name, string? purpose)
    {
        IReadOnlyList<string> words = shop switch
        {
            ReceiptShops.Lidl => LidlPayees,
            ReceiptShops.ReweEbon => ["rewe", "payback"],
            _ => ["rewe"],
        };
        return words.Any(w => (name?.Contains(w, StringComparison.OrdinalIgnoreCase) ?? false) || (purpose?.Contains(w, StringComparison.OrdinalIgnoreCase) ?? false));
    }
}
