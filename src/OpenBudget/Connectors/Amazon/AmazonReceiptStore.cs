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
/// per card or bank charge or refund (an order paid in several charges, e.g. per shipment, has several), carrying the
/// items that charge paid for (see <see cref="AmazonShipments.ItemsForCharge"/>). The receipts are then matched and split
/// like any other (see <see cref="ReceiptStore"/>): by the account the charge's payment method is mapped to. Charges paid
/// with points or a gift card are no receipts of their own: they are credit lines (<see cref="ReceiptItemKind.Credit"/>,
/// named after the payment method) on a card charge of their order (see <see cref="Fold"/>), and tell which items the
/// card charges paid.
///
/// An order's items are read once; when an order gets another charge later, its items are taken from its receipts.
/// </summary>
public static class AmazonReceiptStore
{
    /// <summary>Where an Amazon receipt's credit lines start (after the order's items, which keep their place in the order).</summary>
    private const int CreditPosition = 10000;

    /// <summary>Orders whose details were read already (not asked for again).</summary>
    public static async Task<HashSet<string>> KnownOrdersAsync(BudgetDbContext db, int loginId, CancellationToken ct) =>
        (await db.ReceiptInvoices.AsNoTracking().Where(r => r.BankLoginId == loginId && r.Shop == ReceiptShops.Amazon && r.DetailsReadAt != null && r.OrderNumber != null)
            .Select(r => r.OrderNumber!).Distinct().ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);

    /// <summary>Stored orders whose details were not read yet (a sync stopped before it got to them, or an older sync left them out).</summary>
    public static async Task<List<string>> UnreadOrdersAsync(BudgetDbContext db, int loginId, CancellationToken ct)
    {
        var receipts = await db.ReceiptInvoices.AsNoTracking()
            .Where(r => r.BankLoginId == loginId && r.Shop == ReceiptShops.Amazon && r.OrderNumber != null)
            .Select(r => new { r.OrderNumber, r.DetailsReadAt, r.DeliveryDate }).ToListAsync(ct);
        return receipts.GroupBy(r => r.OrderNumber!, StringComparer.Ordinal).Where(g => g.All(r => r.DetailsReadAt == null))
            .OrderByDescending(g => g.Max(r => r.DeliveryDate)).Select(g => g.Key).ToList();
    }

    /// <summary>
    /// The first day the next sync reads: the whole configured history until one sync got that far
    /// (<see cref="BankLogin.OrderHistoryCompleteAt"/>; the payment list also ends where Amazon has nothing older), then
    /// a month before the newest charge.
    /// </summary>
    public static async Task<DateOnly> SinceAsync(BudgetDbContext db, BankLogin login, DateOnly today, int historyYears, CancellationToken ct)
    {
        var floor = today.AddYears(-historyYears);
        if (login.OrderHistoryCompleteAt == null) return floor;
        var newest = await db.ReceiptInvoices.AsNoTracking().Where(r => r.BankLoginId == login.Id && r.Shop == ReceiptShops.Amazon)
            .MaxAsync(r => r.DeliveryDate, ct);
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
        var known = stored.Select(s => s.SourceKey).Concat(stored.SelectMany(s => s.Items).Select(i => i.SourceKey)).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var touched = new HashSet<string>(StringComparer.Ordinal);
        var credits = new List<(string OrderId, ReceiptItem Line)>();

        // The wallet is the truth for the days it covered: charges it no longer lists there go (receipts and credit lines).
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var c in result.Charges)
        {
            var cents = Cents(c.Amount);
            var basis = string.Create(CultureInfo.InvariantCulture, $"{c.OrderId}|{c.Date:yyyy-MM-dd}|{cents}|{c.InstrumentId}");
            occurrences[basis] = occurrences.GetValueOrDefault(basis) + 1;
            var key = Truncate(basis + "|" + occurrences[basis].ToString(CultureInfo.InvariantCulture), 200)!;
            keys.Add(key);
            if (!known.Add(key)) continue;
            touched.Add(c.OrderId);
            if (AmazonPages.IsPointsLabel(c.Instrument))
            {
                credits.Add((c.OrderId, CreditLine(owner, key, c.Instrument)));
                continue;
            }
            var receipt = NewReceipt(owner, login.Id, key, c.OrderId, c.Date, cents, c.InstrumentId, c.Instrument, nowUtc);
            db.ReceiptInvoices.Add(receipt);
            stored.Add(receipt);
        }
        var from = result.CoveredFrom;
        var gone = stored.Where(s => s.SourceKey != null && Day(s.DeliveryDate) >= from && !keys.Contains(s.SourceKey)).ToList();
        foreach (var g in gone)
        {
            touched.Add(g.OrderNumber ?? "");
            stored.Remove(g);
        }
        db.ReceiptInvoices.RemoveRange(gone);
        foreach (var s in stored)
            foreach (var line in s.Items.Where(i => i.SourceKey != null && ChargeOf(i.SourceKey).Date >= from && !keys.Contains(i.SourceKey)).ToList())
            {
                touched.Add(s.OrderNumber ?? "");
                s.Items.Remove(line);
            }

        var details = result.Orders.ToDictionary(o => o.OrderId, StringComparer.Ordinal);
        foreach (var orderId in touched.Concat(details.Keys).Distinct(StringComparer.Ordinal))
        {
            var ofOrder = Fold(db, login, orderId, stored, credits.Where(c => c.OrderId == orderId).Select(c => c.Line).ToList(), nowUtc);
            if (ofOrder.Count == 0) continue;
            if (details.TryGetValue(orderId, out var d))
            {
                var items = d.Items.Select((i, n) => new AmazonItem(n, i.Title, i.Quantity, i.Price is { } p ? Cents(p) : null, i.Shipment)).ToList();
                Assign(ofOrder, items, d.Date, d.Total is { } t ? Cents(t) : null, nowUtc);
            }
            else
                Reassign(ofOrder);
        }
        await db.SaveChangesAsync(ct);

        // A booking whose charge went loses the receipt's item text (its split, if any, stays as it is).
        var lost = gone.Where(g => g.TransactionId != null).Select(g => g.TransactionId!.Value).ToList();
        foreach (var id in lost)
            if (!await db.ReceiptInvoices.AnyAsync(r => r.TransactionId == id, ct))
                await db.Transactions.Where(t => t.Id == id && t.OwnerUserId == owner).ExecuteUpdateAsync(u => u.SetProperty(t => t.OrderItems, (string?)null), ct);

        await receipts.MatchAsync(db, owner, ct);
    }

    /// <summary>
    /// Turns stored receipts of single points or gift card charges into credit lines of their order (see
    /// <see cref="Fold"/>), at startup; idempotent. The order's other receipts keep their bookings, states and the
    /// owner's item moves. Returns how many such receipts were folded.
    /// </summary>
    public static async Task<int> FoldPointsReceiptsAsync(BudgetDbContext db, DateTime nowUtc, CancellationToken ct = default)
    {
        var points = (await db.ReceiptInvoices.IgnoreQueryFilters().AsNoTracking()
                .Where(r => r.Shop == ReceiptShops.Amazon && r.BankLoginId != null && r.SourceKey != null && r.OrderNumber != null)
                .Select(r => new { r.BankLoginId, r.OrderNumber, r.PaymentMethod }).ToListAsync(ct))
            .Where(r => AmazonPages.IsPointsLabel(r.PaymentMethod ?? "")).ToList();
        foreach (var group in points.GroupBy(r => (Login: r.BankLoginId!.Value, Order: r.OrderNumber!)))
        {
            var login = await db.BankLogins.IgnoreQueryFilters().SingleAsync(l => l.Id == group.Key.Login, ct);
            var stored = await db.ReceiptInvoices.IgnoreQueryFilters().Include(r => r.Items)
                .Where(r => r.BankLoginId == group.Key.Login && r.Shop == ReceiptShops.Amazon && r.OrderNumber == group.Key.Order).ToListAsync(ct);
            var own = stored.Where(r => r.SourceKey != null && IsPoints(r)).ToList();
            var lines = own.Select(r => CreditLine(r.OwnerUserId, r.SourceKey!, r.PaymentMethod!)).ToList();
            foreach (var r in own) stored.Remove(r);
            db.ReceiptInvoices.RemoveRange(own);
            // An order points alone paid keeps that its details were read (its points receipts carried no items).
            var read = own.Where(r => r.DetailsReadAt != null).MaxBy(r => r.DetailsReadAt);
            var ofOrder = Fold(db, login, group.Key.Order, stored, lines, nowUtc);
            if (read != null && ofOrder.All(r => r.DetailsReadAt == null))
                foreach (var r in ofOrder)
                {
                    r.OrderDate = read.OrderDate;
                    r.OrderTotalCents = read.OrderTotalCents;
                    r.DetailsReadAt = read.DetailsReadAt;
                }
            Reassign(ofOrder);
        }
        await db.SaveChangesAsync(ct);
        return points.Count;
    }

    /// <summary>A new, unmatched receipt for one wallet charge by card or bank (positive cents: refund; negative: charge).</summary>
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
        State = ReceiptMatchState.Unmatched,
    };

    /// <summary>The credit line of a points or gift card charge, named after the payment method; <see cref="Fold"/> places it.</summary>
    private static ReceiptItem CreditLine(string owner, string key, string instrument) => new()
    {
        OwnerUserId = owner,
        SourceKey = key,
        Name = Truncate(instrument, 300)!,
        Quantity = "1",
        Kind = ReceiptItemKind.Credit,
    };

    /// <summary>The charge a key ("order|day|cents|payment method|occurrence") names: its day, signed cents (negative: paid) and payment method.</summary>
    private static (DateOnly Date, long Cents, string InstrumentId) ChargeOf(string key)
    {
        var parts = key.Split('|');
        var n = parts.Length;
        return (DateOnly.ParseExact(parts[n - 4], "yyyy-MM-dd", CultureInfo.InvariantCulture), long.Parse(parts[n - 3], CultureInfo.InvariantCulture), parts[n - 2]);
    }

    /// <summary>
    /// Places an order's points and gift card charges (its credit lines and <paramref name="added"/>) on its receipts and
    /// returns the order's receipts. Each credit goes to one card or bank receipt of its direction (a points payment to a
    /// charge, a points refund to a refund): the one of the same day, else the largest. Amazon takes points together with
    /// the card charge of the shipment they paid for, so that is the receipt whose items they reduced. An order no card
    /// or bank charge paid keeps its credits on one receipt of its own (no charge key, named after its first credit, paid
    /// nothing), which needs no booking.
    /// </summary>
    private static List<ReceiptInvoice> Fold(BudgetDbContext db, BankLogin login, string orderId, List<ReceiptInvoice> stored, IReadOnlyList<ReceiptItem> added, DateTime nowUtc)
    {
        var ofOrder = stored.Where(s => s.OrderNumber == orderId).ToList();
        var lines = ofOrder.SelectMany(r => r.Items.Where(i => i.Kind == ReceiptItemKind.Credit && i.SourceKey != null).Select(i => (Line: i, On: (ReceiptInvoice?)r)))
            .Concat(added.Select(i => (Line: i, On: (ReceiptInvoice?)null))).ToList();
        var cards = ofOrder.Where(r => !IsPoints(r)).ToList();
        var holders = ofOrder.Where(IsPoints).ToList();
        void Drop(ReceiptInvoice r)
        {
            stored.Remove(r);
            ofOrder.Remove(r);
            db.ReceiptInvoices.Remove(r);
        }
        foreach (var h in holders.Skip(cards.Count > 0 || lines.Count == 0 ? 0 : 1)) Drop(h);
        if (lines.Count == 0) return ofOrder;
        var holder = cards.Count > 0 ? null : holders.FirstOrDefault();
        if (cards.Count == 0)
        {
            var first = ChargeOf(lines[0].Line.SourceKey!);
            if (holder == null)
            {
                holder = NewReceipt(login.OwnerUserId, login.Id, orderId + "|points", orderId, first.Date, 0, first.InstrumentId, lines[0].Line.Name, nowUtc);
                holder.SourceKey = null;
                holder.State = ReceiptMatchState.Skipped;
                db.ReceiptInvoices.Add(holder);
                stored.Add(holder);
                ofOrder.Add(holder);
            }
            holder.DeliveryDate = lines.Min(l => ChargeOf(l.Line.SourceKey!).Date).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
            holder.PaymentCents = 0;
        }
        foreach (var (line, was) in lines)
        {
            var charge = ChargeOf(line.SourceKey!);
            var target = holder ?? Target(cards, charge.Date, charge.Cents);
            // Against the receipt's direction: a points payment lowers a charge, a points refund lowers a refund.
            var cents = (charge.Cents < 0) == (target.PaymentCents >= 0) ? -Math.Abs(charge.Cents) : Math.Abs(charge.Cents);
            var placed = line;
            if (was != target)
            {
                if (was != null)
                {
                    // A line moving to another receipt is stored anew (the old one goes with its receipt or is removed).
                    was.Items.Remove(line);
                    placed = CreditLine(line.OwnerUserId, line.SourceKey!, line.Name);
                }
                placed.Position = CreditPosition + target.Items.Count(i => i.Kind == ReceiptItemKind.Credit);
                target.Items.Add(placed);
            }
            placed.TotalCents = cents;
            placed.UnitCents = cents;
        }
        return ofOrder;
    }

    private static ReceiptInvoice Target(List<ReceiptInvoice> cards, DateOnly date, long cents)
    {
        var sameWay = cards.Where(r => (r.PaymentCents > 0) == (cents < 0)).ToList();
        return (sameWay.Count > 0 ? sameWay : cards).OrderByDescending(r => Day(r.DeliveryDate) == date).ThenByDescending(r => Math.Abs(r.PaymentCents))
            .ThenBy(r => r.SourceKey, StringComparer.Ordinal).First();
    }

    /// <summary>Gives an order's receipts their items again from what they carry (once its details were read), or just their totals.</summary>
    private static void Reassign(List<ReceiptInvoice> ofOrder)
    {
        var known = ofOrder.Where(o => o.DetailsReadAt != null).ToList();
        if (known.Count > 0) Assign(ofOrder, ItemsOf(known), Day(known[0].OrderDate), known[0].OrderTotalCents, known.Max(o => o.DetailsReadAt)!.Value);
        else
            foreach (var r in ofOrder) Totals(r);
    }

    /// <summary>The order's items as its receipts carry them (each item once, by its place in the order).</summary>
    public static List<AmazonItem> ItemsOf(IEnumerable<ReceiptInvoice> receipts) =>
        receipts.SelectMany(r => r.Items).Where(i => i.Kind == ReceiptItemKind.Product).GroupBy(i => i.Position).OrderBy(g => g.Key).Select(g => g.First())
            .Select(i => new AmazonItem(i.Position, i.Name, int.TryParse(i.Quantity, NumberStyles.Integer, CultureInfo.InvariantCulture, out var q) ? q : 1, i.UnitCents, i.Shipment ?? 0))
            .ToList();

    /// <summary>
    /// Gives each receipt of an order the items its charge paid for (an order paid by points or gift card alone: all on
    /// its one receipt), the order's day and total. Credit lines stay where they are; what they paid tells which items
    /// the card charges paid. The lines add up to the charge through <see cref="ReceiptInvoice.BonusCents"/> (shipping,
    /// discounts). A receipt whose items stay the same keeps its lines (and the owner's moves).
    /// </summary>
    public static void Assign(IReadOnlyList<ReceiptInvoice> ofOrder, IReadOnlyList<AmazonItem> items, DateOnly? orderDate, long? orderTotalCents, DateTime detailsReadAt)
    {
        var split = items.Select(i => new SplitItem(i.Position, i.Title, i.Quantity, i.PriceCents * i.Quantity, i.Shipment)).ToList();
        var points = ofOrder.SelectMany(r => r.Items).Where(i => i.Kind == ReceiptItemKind.Credit && i.SourceKey != null)
            .Select(i => ChargeOf(i.SourceKey!).Cents).Where(c => c < 0).Sum(c => -c);
        var card = ofOrder.Where(r => !IsPoints(r)).Select(r => -r.PaymentCents).ToList();
        foreach (var r in ofOrder)
        {
            r.OrderDate = orderDate?.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) ?? r.OrderDate;
            r.OrderTotalCents = orderTotalCents ?? r.OrderTotalCents;
            r.DetailsReadAt = detailsReadAt;
            var paid = IsPoints(r) ? items.Select(i => i.Position).ToHashSet()
                : AmazonShipments.ItemsForCharge(split, -r.PaymentCents, card, points, orderTotalCents).Items.Select(i => (int)i.Id).ToHashSet();
            var wanted = items.Where(i => paid.Contains(i.Position)).ToList();
            var products = r.Items.Where(i => i.Kind == ReceiptItemKind.Product).ToList();
            if (!products.Select(i => i.Position).Order().SequenceEqual(wanted.Select(i => i.Position)))
            {
                foreach (var old in products) r.Items.Remove(old);
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
            Totals(r);
        }
    }

    /// <summary>The receipt's sum of lines and what the charge differs from it.</summary>
    private static void Totals(ReceiptInvoice r)
    {
        r.TotalCents = r.Items.Sum(i => i.TotalCents);
        r.BonusCents = r.Items.Count == 0 ? 0 : Math.Abs(r.PaymentCents) - r.TotalCents;
    }

    /// <summary>The receipt of an order that only points or gift cards paid (see <see cref="Fold"/>).</summary>
    private static bool IsPoints(ReceiptInvoice r) => AmazonPages.IsPointsLabel(r.PaymentMethod ?? "");

    private static DateOnly? Day(DateTime? d) => d is { } v ? DateOnly.FromDateTime(v) : null;

    private static long Cents(decimal amount) => (long)Math.Round(amount * 100m, MidpointRounding.AwayFromZero);

    private static string? Truncate(string? value, int max) => value == null || value.Length <= max ? value : value[..max];
}
