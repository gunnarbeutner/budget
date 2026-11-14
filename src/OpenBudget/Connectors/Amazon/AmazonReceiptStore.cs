using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using OpenBudget.Connectors.Receipts;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.Connectors.Amazon;

/// <summary>An order item as stored on its order's receipts: its place in the order and what the details page showed.</summary>
public sealed record AmazonItem(int Position, string Title, int Quantity, long? PriceCents, int Shipment);

/// <summary>
/// Stores what Amazon's wallet and order pages gave as receipts of the shop <see cref="ReceiptShops.Amazon"/>: one receipt
/// per charge or refund (an order paid in several charges, e.g. per shipment, has several), carrying the items that
/// charge paid for (see <see cref="AmazonShipments.ItemsForCharge"/>). The receipts are then matched and split like any
/// other (see <see cref="ReceiptStore"/>): by the account the charge's payment method is mapped to. Charges paid with
/// points or a gift card are kept (they tell which items the card charges paid) but have no booking.
///
/// An order's items are read once; when an order gets another charge later, its items are taken from its receipts.
/// </summary>
public static class AmazonReceiptStore
{
    /// <summary>Orders whose details were read already (not asked for again).</summary>
    public static async Task<HashSet<string>> KnownOrdersAsync(BudgetDbContext db, int loginId, CancellationToken ct) =>
        (await db.ReceiptInvoices.AsNoTracking().Where(r => r.BankLoginId == loginId && r.Shop == ReceiptShops.Amazon && r.DetailsReadAt != null && r.OrderNumber != null)
            .Select(r => r.OrderNumber!).Distinct().ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);

    /// <summary>The first day the next sync reads: the configured history on the first one, later a month before the newest charge.</summary>
    public static async Task<DateOnly> SinceAsync(BudgetDbContext db, int loginId, DateOnly today, int historyYears, CancellationToken ct)
    {
        var floor = today.AddYears(-historyYears);
        var newest = await db.ReceiptInvoices.AsNoTracking().Where(r => r.BankLoginId == loginId && r.Shop == ReceiptShops.Amazon).MaxAsync(r => r.DeliveryDate, ct);
        if (newest is not { } n) return floor;
        var since = DateOnly.FromDateTime(n).AddDays(-30);
        return since < floor ? floor : since;
    }

    /// <summary>A charge's invoice number: unique per owner across logins, at most 40 characters.</summary>
    public static string InvoiceNumber(int loginId, string chargeKey) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(loginId.ToString(CultureInfo.InvariantCulture) + "|" + chargeKey)))[..40];

    /// <summary>Stores a sync's charges and order details for the login, then matches the owner's receipts.</summary>
    public static async Task ApplyAsync(BudgetDbContext db, BankLogin login, OrderSourceResult result, DateTime nowUtc, ReceiptStore receipts, CancellationToken ct)
    {
        var owner = login.OwnerUserId;
        var stored = await db.ReceiptInvoices.Include(r => r.Items).Where(r => r.BankLoginId == login.Id && r.Shop == ReceiptShops.Amazon).ToListAsync(ct);
        var touched = new HashSet<string>(StringComparer.Ordinal);

        // The wallet is the truth for the days it covered: charges it no longer lists there go.
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var c in result.Charges)
        {
            var cents = Cents(c.Amount);
            var basis = string.Create(CultureInfo.InvariantCulture, $"{c.OrderId}|{c.Date:yyyy-MM-dd}|{cents}|{c.InstrumentId}");
            occurrences[basis] = occurrences.GetValueOrDefault(basis) + 1;
            var key = basis + "|" + occurrences[basis].ToString(CultureInfo.InvariantCulture);
            keys.Add(key);
            if (stored.Any(s => s.SourceKey == key)) continue;
            var receipt = NewReceipt(owner, login.Id, key, c.OrderId, c.Date, cents, c.InstrumentId, c.Instrument, nowUtc);
            db.ReceiptInvoices.Add(receipt);
            stored.Add(receipt);
            touched.Add(c.OrderId);
        }
        var from = result.CoveredFrom.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var gone = stored.Where(s => s.DeliveryDate >= from && !keys.Contains(s.SourceKey ?? "")).ToList();
        foreach (var g in gone)
        {
            touched.Add(g.OrderNumber ?? "");
            stored.Remove(g);
        }
        db.ReceiptInvoices.RemoveRange(gone);

        var details = result.Orders.ToDictionary(o => o.OrderId, StringComparer.Ordinal);
        foreach (var orderId in touched.Concat(details.Keys).Distinct(StringComparer.Ordinal))
        {
            var ofOrder = stored.Where(s => s.OrderNumber == orderId).ToList();
            if (ofOrder.Count == 0) continue;
            if (details.TryGetValue(orderId, out var d))
            {
                var items = d.Items.Select((i, n) => new AmazonItem(n, i.Title, i.Quantity, i.Price is { } p ? Cents(p) : null, i.Shipment)).ToList();
                Assign(ofOrder, items, d.Date, d.Total is { } t ? Cents(t) : null, nowUtc);
            }
            else if (ofOrder.Any(o => o.DetailsReadAt != null))
            {
                var known = ofOrder.Where(o => o.DetailsReadAt != null).ToList();
                Assign(ofOrder, ItemsOf(known), Day(known[0].OrderDate), known[0].OrderTotalCents, known.Max(o => o.DetailsReadAt)!.Value);
            }
        }
        await db.SaveChangesAsync(ct);

        // A booking whose charge went loses the receipt's item text (its split, if any, stays as it is).
        var lost = gone.Where(g => g.TransactionId != null).Select(g => g.TransactionId!.Value).ToList();
        foreach (var id in lost)
            if (!await db.ReceiptInvoices.AnyAsync(r => r.TransactionId == id, ct))
                await db.Transactions.Where(t => t.Id == id && t.OwnerUserId == owner).ExecuteUpdateAsync(u => u.SetProperty(t => t.OrderItems, (string?)null), ct);

        await receipts.MatchAsync(db, owner, ct);
    }

    /// <summary>A new, unmatched receipt for one wallet charge (positive cents: refund; negative: charge).</summary>
    public static ReceiptInvoice NewReceipt(string owner, int loginId, string key, string orderId, DateOnly date, long cents, string instrumentId, string instrument, DateTime nowUtc) => new()
    {
        OwnerUserId = owner,
        Shop = ReceiptShops.Amazon,
        BankLoginId = loginId,
        SourceKey = Truncate(key, 200),
        InvoiceNumber = InvoiceNumber(loginId, key),
        OrderNumber = Truncate(orderId, 40),
        DeliveryDate = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
        PaymentCents = -cents,
        PaymentMethodId = Truncate(instrumentId, 200),
        PaymentMethod = Truncate(instrument, 200),
        ReadAt = nowUtc,
        State = AmazonPages.IsPointsLabel(instrument) ? ReceiptMatchState.Skipped : ReceiptMatchState.Unmatched,
    };

    /// <summary>The order's items as its receipts carry them (each item once, by its place in the order).</summary>
    public static List<AmazonItem> ItemsOf(IEnumerable<ReceiptInvoice> receipts) =>
        receipts.SelectMany(r => r.Items).GroupBy(i => i.Position).OrderBy(g => g.Key).Select(g => g.First())
            .Select(i => new AmazonItem(i.Position, i.Name, int.TryParse(i.Quantity, NumberStyles.Integer, CultureInfo.InvariantCulture, out var q) ? q : 1, i.UnitCents, i.Shipment ?? 0))
            .ToList();

    /// <summary>
    /// Gives each receipt of an order the items its charge paid for (none for points and gift cards), the order's day and
    /// total; the lines add up to the charge through <see cref="ReceiptInvoice.BonusCents"/> (shipping, discounts, points).
    /// A receipt whose items stay the same keeps its lines (and the owner's moves).
    /// </summary>
    public static void Assign(IReadOnlyList<ReceiptInvoice> ofOrder, IReadOnlyList<AmazonItem> items, DateOnly? orderDate, long? orderTotalCents, DateTime detailsReadAt)
    {
        var split = items.Select(i => new SplitItem(i.Position, i.Title, i.Quantity, i.PriceCents * i.Quantity, i.Shipment)).ToList();
        var points = ofOrder.Where(IsPoints).Where(r => r.PaymentCents > 0).Sum(r => r.PaymentCents);
        var card = ofOrder.Where(r => !IsPoints(r)).Select(r => -r.PaymentCents).ToList();
        foreach (var r in ofOrder)
        {
            r.OrderDate = orderDate?.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) ?? r.OrderDate;
            r.OrderTotalCents = orderTotalCents ?? r.OrderTotalCents;
            r.DetailsReadAt = detailsReadAt;
            var paid = IsPoints(r) ? [] : AmazonShipments.ItemsForCharge(split, -r.PaymentCents, card, points, orderTotalCents).Items.Select(i => (int)i.Id).ToHashSet();
            var wanted = items.Where(i => paid.Contains(i.Position)).ToList();
            if (!r.Items.Select(i => i.Position).Order().SequenceEqual(wanted.Select(i => i.Position)))
            {
                r.Items.Clear();
                foreach (var i in wanted)
                    r.Items.Add(new ReceiptItem
                    {
                        OwnerUserId = r.OwnerUserId,
                        Position = i.Position,
                        Name = Truncate(i.Title, 300)!,
                        Quantity = i.Quantity.ToString(CultureInfo.InvariantCulture),
                        UnitCents = i.PriceCents,
                        TotalCents = (i.PriceCents ?? 0) * i.Quantity,
                        Shipment = i.Shipment,
                        Kind = ReceiptItemKind.Product,
                    });
            }
            r.TotalCents = r.Items.Sum(i => i.TotalCents);
            r.BonusCents = r.Items.Count == 0 ? 0 : Math.Abs(r.PaymentCents) - r.TotalCents;
        }
    }

    private static bool IsPoints(ReceiptInvoice r) => AmazonPages.IsPointsLabel(r.PaymentMethod ?? "");

    private static DateOnly? Day(DateTime? d) => d is { } v ? DateOnly.FromDateTime(v) : null;

    private static long Cents(decimal amount) => (long)Math.Round(amount * 100m, MidpointRounding.AwayFromZero);

    private static string? Truncate(string? value, int max) => value == null || value.Length <= max ? value : value[..max];
}
