using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using OpenBudget.Connectors.Receipts;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.Connectors.Amazon;

/// <summary>An order item as stored on its order's receipts: its place in the order and what the details page showed.</summary>
public sealed record AmazonItem(int Position, string Title, int Quantity, long? PriceCents, int Shipment, bool Returned = false);

/// <summary>
/// An order paid in monthly instalments on one payment method: the receipt of its full price, which the wallet lists
/// once but never reaches an account, and the receipts of the instalments listed so far (each a charge of its own).
/// </summary>
public sealed record InstalmentPlan(ReceiptInvoice Full, int Count, IReadOnlyList<ReceiptInvoice> Instalments);

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

    /// <summary>Where the items of a receipt's further orders start: its n-th order's items are at n × this plus their place in the order.</summary>
    private const int OrderPositions = 1000;

    /// <summary>The most orders one receipt carries items of (the rest of a larger payment's orders are left out).</summary>
    private const int MaxOrders = CreditPosition / OrderPositions;

    /// <summary>
    /// Orders whose details were read already (not asked for again). A receipt of several orders counts as read once all
    /// its orders were.
    /// </summary>
    public static async Task<HashSet<string>> KnownOrdersAsync(BudgetDbContext db, int loginId, CancellationToken ct) =>
        (await db.ReceiptInvoices.AsNoTracking().Where(r => r.BankLoginId == loginId && r.Shop == ReceiptShops.Amazon && r.DetailsReadAt != null && r.OrderNumber != null)
            .Select(r => new { r.OrderNumber, r.OtherOrderNumbers }).ToListAsync(ct))
            .SelectMany(r => ReceiptInvoice.OrderNumbersOf(r.OrderNumber, r.OtherOrderNumbers)).ToHashSet(StringComparer.Ordinal);

    /// <summary>Stored orders whose details were not read yet (a sync stopped before it got to them, or an older sync left them out).</summary>
    public static async Task<List<string>> UnreadOrdersAsync(BudgetDbContext db, int loginId, CancellationToken ct)
    {
        var receipts = await db.ReceiptInvoices.AsNoTracking()
            .Where(r => r.BankLoginId == loginId && r.Shop == ReceiptShops.Amazon && r.OrderNumber != null)
            .Select(r => new { r.OrderNumber, r.OtherOrderNumbers, r.DetailsReadAt, r.DeliveryDate }).ToListAsync(ct);
        return receipts.SelectMany(r => ReceiptInvoice.OrderNumbersOf(r.OrderNumber, r.OtherOrderNumbers).Select(o => (Order: o, r.DetailsReadAt, r.DeliveryDate)))
            .GroupBy(r => r.Order, StringComparer.Ordinal).Where(g => g.All(r => r.DetailsReadAt == null))
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
    public static async Task ApplyAsync(BudgetDbContext db, BankLogin login, OrderSourceResult result, DateTime nowUtc, ReceiptStore receipts, CancellationToken ct, Action<string>? progress = null)
    {
        var owner = login.OwnerUserId;
        var stored = await db.ReceiptInvoices.Include(r => r.Items).Where(r => r.BankLoginId == login.Id && r.Shop == ReceiptShops.Amazon).ToListAsync(ct);
        var known = stored.Select(s => s.SourceKey).Concat(stored.SelectMany(s => s.Items).Select(i => i.SourceKey)).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var bySource = stored.Where(s => s.SourceKey != null).GroupBy(s => s.SourceKey!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var readBefore = stored.Where(s => s.DetailsReadAt != null).SelectMany(s => s.OrderNumbers).ToHashSet(StringComparer.Ordinal);
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
            // A payment of several orders is keyed by its first: a receipt stored for that order alone takes the others on.
            var others = Others(c);
            if (bySource.TryGetValue(key, out var seen) && seen.OtherOrderNumbers != others)
            {
                seen.OtherOrderNumbers = others;
                touched.UnionWith(seen.OrderNumbers);
            }
            if (!known.Add(key)) continue;
            touched.UnionWith(c.OrderIds);
            if (AmazonPages.IsPointsLabel(c.Instrument))
            {
                credits.Add((c.OrderId, CreditLine(owner, key, c.Instrument)));
                continue;
            }
            var receipt = NewReceipt(owner, login.Id, key, c.OrderId, c.Date, cents, c.InstrumentId, c.Instrument, nowUtc);
            receipt.OtherOrderNumbers = others;
            db.ReceiptInvoices.Add(receipt);
            stored.Add(receipt);
        }
        var from = result.CoveredFrom;
        bool Read(DateOnly? day) => day >= from && !(result.Skipped is { } skip && day >= skip.From && day <= skip.To);
        var gone = stored.Where(s => s.SourceKey != null && Read(Day(s.DeliveryDate)) && !keys.Contains(s.SourceKey)).ToList();
        foreach (var g in gone)
        {
            touched.UnionWith(g.OrderNumbers);
            stored.Remove(g);
        }
        db.ReceiptInvoices.RemoveRange(gone);
        foreach (var s in stored)
            foreach (var line in s.Items.Where(i => i.SourceKey != null && Read(ChargeOf(i.SourceKey).Date) && !keys.Contains(i.SourceKey)).ToList())
            {
                touched.UnionWith(s.OrderNumbers);
                s.Items.Remove(line);
            }

        var details = result.Orders.ToDictionary(o => o.OrderId, StringComparer.Ordinal);
        foreach (var orderId in touched.Concat(details.Keys).Distinct(StringComparer.Ordinal))
        {
            var ofOrder = Fold(db, login, orderId, stored, credits.Where(c => c.OrderId == orderId).Select(c => c.Line).ToList(), nowUtc);
            if (ofOrder.Count == 0) continue;
            if (details.TryGetValue(orderId, out var d))
            {
                var items = d.Items.Select((i, n) => new AmazonItem(n, i.Title, i.Quantity, i.Price is { } p ? Cents(p) : null, i.Shipment, i.Returned)).ToList();
                Assign(ofOrder, items, d.Date, d.Total is { } t ? Cents(t) : null, nowUtc, orderId);
            }
            else
                Reassign(ofOrder, orderId);
        }
        // A receipt of several orders is read once each of its orders was (in this sync or before).
        foreach (var s in stored.Where(s => s.OtherOrderNumbers != null && s.OrderNumbers.Any(touched.Contains)))
            s.DetailsReadAt = s.OrderNumbers.All(o => details.ContainsKey(o) || readBefore.Contains(o)) ? s.DetailsReadAt ?? nowUtc : null;
        await db.SaveChangesAsync(ct);

        // A booking whose charge went loses the receipt's item text (its split, if any, stays as it is).
        var lost = gone.Where(g => g.TransactionId != null).Select(g => g.TransactionId!.Value).ToList();
        var shared = lost.Count == 0 ? [] : (await ReceiptStore.SharedAccountsAsync(db, owner, ct)).ToList();
        foreach (var id in lost)
            if (!await db.ReceiptInvoices.AnyAsync(r => r.TransactionId == id, ct))
                await db.Transactions.Where(t => t.Id == id && (t.OwnerUserId == owner || (t.AccountId != null && shared.Contains(t.AccountId.Value))))
                    .ExecuteUpdateAsync(u => u.SetProperty(t => t.OrderItems, (string?)null), ct);

        await receipts.MatchAsync(db, owner, ct, progress);
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

    /// <summary>
    /// Gives stored refund receipts the items they pay back, from the order's items as stored (see
    /// <see cref="Assign"/>), at startup; idempotent. Returns the owners whose receipts changed, for matching again.
    /// </summary>
    public static async Task<IReadOnlySet<string>> AssignRefundItemsAsync(BudgetDbContext db, CancellationToken ct = default)
    {
        var orders = await db.ReceiptInvoices.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.Shop == ReceiptShops.Amazon && r.BankLoginId != null && r.OrderNumber != null && r.OtherOrderNumbers == null
                && r.PaymentCents < 0 && r.DetailsReadAt != null)
            .Select(r => new { Login = r.BankLoginId!.Value, Order = r.OrderNumber! }).Distinct().ToListAsync(ct);
        var changed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var o in orders)
        {
            var ofOrder = await db.ReceiptInvoices.IgnoreQueryFilters().Include(r => r.Items)
                .Where(r => r.BankLoginId == o.Login && r.Shop == ReceiptShops.Amazon && r.OrderNumber == o.Order && r.OtherOrderNumbers == null).ToListAsync(ct);
            var before = ofOrder.ToDictionary(r => r, Positions);
            Reassign(ofOrder);
            foreach (var r in ofOrder.Where(r => !before[r].SequenceEqual(Positions(r))))
                changed.Add(r.OwnerUserId);
        }
        await db.SaveChangesAsync(ct);
        return changed;

        static List<int> Positions(ReceiptInvoice r) =>
            r.Items.Where(i => i.Kind == ReceiptItemKind.Product).Select(i => i.Position).Order().ToList();
    }

    /// <summary>A charge's further orders as <see cref="ReceiptInvoice.OtherOrderNumbers"/> stores them (null: one order).</summary>
    private static string? Others(RemoteCharge c)
    {
        var others = c.OtherOrderIds.Where(o => o != c.OrderId).Take(MaxOrders - 1).Select(o => Truncate(o, 40)!).ToList();
        return others.Count == 0 ? null : string.Join(' ', others);
    }

    /// <summary>Which of a receipt's orders <paramref name="orderId"/> is (0: its first, or a receipt of one order).</summary>
    private static int OrderIndex(ReceiptInvoice r, string? orderId) =>
        orderId == null ? 0 : Math.Max(0, r.OrderNumbers.ToList().IndexOf(orderId));

    /// <summary>Whether a receipt carries several orders' items.</summary>
    private static bool Shared(ReceiptInvoice r) => r.OtherOrderNumbers != null;

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
        var ofOrder = stored.Where(s => s.OrderNumbers.Contains(orderId, StringComparer.Ordinal)).ToList();
        // A charge of several orders carries the credits of its first.
        var lines = ofOrder.Where(r => OrderIndex(r, orderId) == 0).SelectMany(r => r.Items.Where(i => i.Kind == ReceiptItemKind.Credit && i.SourceKey != null).Select(i => (Line: i, On: (ReceiptInvoice?)r)))
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
    private static void Reassign(List<ReceiptInvoice> ofOrder, string? orderId = null)
    {
        var known = ofOrder.Where(o => o.DetailsReadAt != null).ToList();
        // The order's day and total are on the receipts it comes first on.
        var own = known.FirstOrDefault(r => OrderIndex(r, orderId) == 0);
        if (known.Count > 0) Assign(ofOrder, ItemsOf(known, orderId), Day(own?.OrderDate), own?.OrderTotalCents, known.Max(o => o.DetailsReadAt)!.Value, orderId);
        else
            foreach (var r in ofOrder) Totals(r);
    }

    /// <summary>The order's items as its receipts carry them (each item once, by its place in the order).</summary>
    public static List<AmazonItem> ItemsOf(IEnumerable<ReceiptInvoice> receipts, string? orderId = null) =>
        receipts.SelectMany(r => r.Items.Where(i => i.Kind == ReceiptItemKind.Product && i.Position / OrderPositions == OrderIndex(r, orderId))
                .Select(i => (Position: i.Position % OrderPositions, Line: i)))
            .GroupBy(i => i.Position).OrderBy(g => g.Key).Select(g => (g.Key, g.First().Line))
            .Select(x => new AmazonItem(x.Key, x.Line.Name, int.TryParse(x.Line.Quantity, NumberStyles.Integer, CultureInfo.InvariantCulture, out var q) ? q : 1, x.Line.UnitCents, x.Line.Shipment ?? 0))
            .ToList();

    /// <summary>
    /// Gives each receipt of an order the items its charge paid for (an order paid by points or gift card alone: all on
    /// its one receipt), the order's day and total. Credit lines stay where they are; what they paid tells which items
    /// the card charges paid. The lines add up to the charge through <see cref="ReceiptInvoice.BonusCents"/> (shipping,
    /// discounts). A receipt whose items stay the same keeps its lines (and the owner's moves), taking over their
    /// quantities and prices as read.
    ///
    /// A charge of several orders (<paramref name="orderId"/> one of them) gets the order's items no charge of the order
    /// alone paid for, next to its other orders' items; it keeps the day and total of its first order, and when it was
    /// read is up to the caller (all its orders must be).
    /// </summary>
    public static void Assign(IReadOnlyList<ReceiptInvoice> ofOrder, IReadOnlyList<AmazonItem> items, DateOnly? orderDate, long? orderTotalCents, DateTime detailsReadAt,
        string? orderId = null)
    {
        var split = items.Select(i => new SplitItem(i.Position, i.Title, i.Quantity, i.PriceCents * i.Quantity, i.Shipment, i.Returned)).ToList();
        var points = ofOrder.Where(r => OrderIndex(r, orderId) == 0).SelectMany(r => r.Items).Where(i => i.Kind == ReceiptItemKind.Credit && i.SourceKey != null)
            .Select(i => ChargeOf(i.SourceKey!).Cents).Where(c => c < 0).Sum(c => -c);
        var plans = InstalmentPlans(ofOrder);
        var whole = plans.Select(p => p.Full).Concat(plans.SelectMany(p => p.Instalments)).ToHashSet();
        var card = ofOrder.Where(r => !IsPoints(r) && !whole.Contains(r)).Select(r => -r.PaymentCents).ToList();
        var all = items.Select(i => i.Position).ToHashSet();
        var paidBy = new Dictionary<ReceiptInvoice, HashSet<int>>();
        var claimed = new HashSet<int>();
        foreach (var r in ofOrder.Where(r => !Shared(r)))
        {
            if (IsPoints(r) || whole.Contains(r))
            {
                paidBy[r] = all;
                continue;
            }
            var charge = AmazonShipments.ItemsForCharge(split, -r.PaymentCents, card, points, orderTotalCents);
            paidBy[r] = charge.Items.Select(i => (int)i.Id).ToHashSet();
            if (r.PaymentCents > 0 && !charge.Unclear) claimed.UnionWith(paidBy[r]);
        }
        // A charge of several orders paid what this order's own charges did not (a refund: all of it).
        foreach (var r in ofOrder.Where(Shared))
            paidBy[r] = r.PaymentCents > 0 ? all.Where(p => !claimed.Contains(p)).ToHashSet() : all;
        foreach (var r in ofOrder)
        {
            var index = OrderIndex(r, orderId);
            var offset = index * OrderPositions;
            if (index == 0)
            {
                r.OrderDate = orderDate?.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) ?? r.OrderDate;
                r.OrderTotalCents = orderTotalCents ?? r.OrderTotalCents;
            }
            if (!Shared(r)) r.DetailsReadAt = detailsReadAt;
            var paid = paidBy[r];
            var wanted = items.Where(i => paid.Contains(i.Position) && i.Position < OrderPositions).ToList();
            var products = r.Items.Where(i => i.Kind == ReceiptItemKind.Product && i.Position / OrderPositions == index).ToList();
            if (!products.Select(i => i.Position).Order().SequenceEqual(wanted.Select(i => i.Position + offset)))
            {
                foreach (var old in products) r.Items.Remove(old);
                foreach (var i in wanted)
                    r.Items.Add(new ReceiptItem
                    {
                        OwnerUserId = r.OwnerUserId,
                        Position = i.Position + offset,
                        Name = Truncate(i.Title, 300)!,
                        Quantity = i.Quantity.ToString(CultureInfo.InvariantCulture),
                        UnitCents = i.PriceCents,
                        TotalCents = (i.PriceCents ?? 0) * i.Quantity,
                        Shipment = i.Shipment,
                        Kind = ReceiptItemKind.Product,
                    });
            }
            else
            {
                // The same items keep their lines (and the owner's moves); a corrected quantity or price is taken over.
                foreach (var line in products)
                {
                    var i = wanted.First(w => w.Position + offset == line.Position);
                    line.Name = Truncate(i.Title, 300)!;
                    line.Quantity = i.Quantity.ToString(CultureInfo.InvariantCulture);
                    line.UnitCents = i.PriceCents;
                    line.TotalCents = (i.PriceCents ?? 0) * i.Quantity;
                    line.Shipment = i.Shipment;
                }
            }
            Totals(r);
        }
    }

    /// <summary>The most instalments a plan is looked for with.</summary>
    public const int MaxInstalments = 24;

    /// <summary>
    /// The instalment plans among an order's receipts. A charge is the full price of a plan of n instalments (2 to
    /// <see cref="MaxInstalments"/>) when other charges of the same payment method, on its day or later, come to about
    /// a nth of it: each within n cents of full/n, at least one of them within one cent (n × amount within n cents of the
    /// full price), no more than n of them and together no more than the full price plus n cents. Exactly one n must fit.
    /// When the order's total is known the full price must be it (an order shipped in two charges, one twice the other,
    /// stays two charges); without it, two instalments must be listed. A charge of several orders is in no plan.
    /// </summary>
    public static List<InstalmentPlan> InstalmentPlans(IReadOnlyList<ReceiptInvoice> ofOrder)
    {
        var plans = new List<InstalmentPlan>();
        var charges = ofOrder.Where(r => r.PaymentCents > 0 && r.SourceKey != null && !IsPoints(r) && !Shared(r)).ToList();
        foreach (var full in charges.OrderByDescending(r => r.PaymentCents).ThenBy(r => r.SourceKey, StringComparer.Ordinal))
        {
            if (plans.Any(p => p.Full == full || p.Instalments.Contains(full))) continue;
            var total = full.PaymentCents;
            var others = charges.Where(r => r != full && r.PaymentMethodId == full.PaymentMethodId && r.PaymentCents < total
                && Day(r.DeliveryDate) >= Day(full.DeliveryDate) && plans.All(p => p.Full != r && !p.Instalments.Contains(r))).ToList();
            var fits = new List<(int N, List<ReceiptInvoice> Of)>();
            for (var n = 2; n <= MaxInstalments; n++)
            {
                var of = others.Where(r => Math.Abs(n * r.PaymentCents - total) <= (long)n * n).ToList();
                if (of.Count == 0 || of.Count > n || !of.Any(r => Math.Abs(n * r.PaymentCents - total) <= n) || of.Sum(r => r.PaymentCents) > total + n) continue;
                fits.Add((n, of));
            }
            if (fits is not [var fit]) continue;
            var orderTotal = ofOrder.Select(r => r.OrderTotalCents).FirstOrDefault(t => t != null);
            if (orderTotal is { } known ? known != total : fit.Of.Count < 2) continue;
            plans.Add(new InstalmentPlan(full, fit.N, fit.Of.OrderBy(r => r.DeliveryDate).ToList()));
        }
        return plans;
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
