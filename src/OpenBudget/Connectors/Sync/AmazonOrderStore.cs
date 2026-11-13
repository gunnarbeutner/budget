using System.Globalization;
using Microsoft.EntityFrameworkCore;
using OpenBudget.Connectors.Amazon;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.Connectors.Sync;

/// <summary>The items a booking paid for, whether the shipment was unclear or the charge an instalment, and its orders.</summary>
public sealed record BookingItems(IReadOnlyList<SplitItem> Items, bool Unclear, bool Instalment, IReadOnlyList<string> OrderIds);

/// <summary>
/// Stores what an order source read (orders, items, charges) and matches the charges with the bookings of the accounts
/// their instruments are mapped to (see <see cref="AmazonMatcher"/>). A matched booking gets the order's items in
/// <see cref="Transaction.OrderItems"/>; a booking that lost its match loses them again. Works on a system context
/// and only ever pairs charges and bookings of the same owner.
/// </summary>
public sealed class AmazonOrderStore
{
    /// <summary>Orders whose details were read already (not asked for again).</summary>
    public static async Task<HashSet<string>> KnownOrdersAsync(BudgetDbContext db, int loginId, CancellationToken ct) =>
        (await db.AmazonOrders.AsNoTracking().Where(o => o.BankLoginId == loginId && o.DetailsFetchedAt != null).Select(o => o.OrderId).ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>The first day the next sync reads: the configured history on the first one, later a month before the newest charge.</summary>
    public static async Task<DateOnly> SinceAsync(BudgetDbContext db, int loginId, DateOnly today, int historyYears, CancellationToken ct)
    {
        var floor = today.AddYears(-historyYears);
        var newest = await db.AmazonCharges.AsNoTracking().Where(c => c.BankLoginId == loginId).MaxAsync(c => (DateTime?)c.Date, ct);
        if (newest is not { } n) return floor;
        var since = DateOnly.FromDateTime(n).AddDays(-30);
        return since < floor ? floor : since;
    }

    public async Task ApplyAsync(BudgetDbContext db, BankLogin login, OrderSourceResult result, DateTime nowUtc, CancellationToken ct)
    {
        var owner = login.OwnerUserId;
        var orders = await db.AmazonOrders.Include(o => o.Items).Where(o => o.BankLoginId == login.Id).ToDictionaryAsync(o => o.OrderId, StringComparer.Ordinal, ct);
        foreach (var r in result.Orders)
        {
            if (!orders.TryGetValue(r.OrderId, out var order))
            {
                order = new AmazonOrder { OwnerUserId = owner, BankLoginId = login.Id, OrderId = r.OrderId };
                db.AmazonOrders.Add(order);
                orders[r.OrderId] = order;
            }
            order.OrderDate = r.Date?.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) ?? order.OrderDate;
            order.TotalCents = r.Total is { } t ? Cents(t) : order.TotalCents;
            order.Currency = r.Currency;
            order.DetailsFetchedAt = nowUtc;
            order.Items.Clear();
            foreach (var i in r.Items)
                order.Items.Add(new AmazonOrderItem
                {
                    OwnerUserId = owner,
                    Title = i.Title,
                    Quantity = i.Quantity,
                    PriceCents = i.Price is { } p ? Cents(p) : null,
                    Asin = i.Asin,
                    Shipment = i.Shipment,
                });
        }
        // Orders only seen as charges get a row too (their details may follow later).
        foreach (var id in result.Charges.Select(c => c.OrderId).Distinct(StringComparer.Ordinal).Where(id => !orders.ContainsKey(id)))
        {
            var order = new AmazonOrder { OwnerUserId = owner, BankLoginId = login.Id, OrderId = id };
            db.AmazonOrders.Add(order);
            orders[id] = order;
        }

        // The wallet is the truth for the days it covered: charges it no longer lists there go.
        var from = result.CoveredFrom.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var stored = await db.AmazonCharges.Where(c => c.BankLoginId == login.Id).ToListAsync(ct);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var c in result.Charges)
        {
            var cents = Cents(c.Amount);
            var basis = string.Create(CultureInfo.InvariantCulture, $"{c.OrderId}|{c.Date:yyyy-MM-dd}|{cents}|{c.InstrumentId}");
            occurrences[basis] = occurrences.GetValueOrDefault(basis) + 1;
            var key = basis + "|" + occurrences[basis].ToString(CultureInfo.InvariantCulture);
            keys.Add(key);
            if (stored.Any(s => s.ChargeKey == key)) continue;
            db.AmazonCharges.Add(new AmazonCharge
            {
                OwnerUserId = owner,
                BankLoginId = login.Id,
                ChargeKey = key,
                OrderId = c.OrderId,
                Date = c.Date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
                AmountCents = cents,
                Currency = c.Currency,
                InstrumentId = c.InstrumentId,
                Instrument = c.Instrument,
                State = c.Skip ? AmazonChargeState.Skipped : AmazonChargeState.Unmatched,
            });
        }
        db.AmazonCharges.RemoveRange(stored.Where(s => s.Date >= from && !keys.Contains(s.ChargeKey)));
        await db.SaveChangesAsync(ct);
        await MatchAsync(db, owner, ct);
    }

    /// <summary>(Re)matches every charge of the owner and brings the bookings' item texts up to date.</summary>
    public async Task MatchAsync(BudgetDbContext db, string ownerUserId, CancellationToken ct)
    {
        var charges = await db.AmazonCharges.Where(c => c.OwnerUserId == ownerUserId).ToListAsync(ct);
        var previouslyEnriched = await db.Transactions.Where(t => t.OwnerUserId == ownerUserId && t.OrderItems != null).Select(t => t.Id).ToListAsync(ct);
        if (charges.Count == 0 && previouslyEnriched.Count == 0) return;
        var loginIds = charges.Select(c => c.BankLoginId).Distinct().ToList();
        var mapping = (await db.BankLoginAccounts.AsNoTracking()
                .Where(a => loginIds.Contains(a.BankLoginId) && a.EnrichAccountId != null).ToListAsync(ct))
            .ToDictionary(a => (a.BankLoginId, a.RemoteId), a => a.EnrichAccountId!.Value);
        // Only the owner's own accounts.
        var accountIds = mapping.Values.Distinct().ToList();
        var owned = (await db.Accounts.AsNoTracking().Where(a => accountIds.Contains(a.Id) && a.OwnerUserId == ownerUserId).Select(a => a.Id).ToListAsync(ct)).ToHashSet();

        long? AccountOf(AmazonCharge c) => !AmazonPages.IsPointsLabel(c.Instrument) && mapping.TryGetValue((c.BankLoginId, c.InstrumentId), out var a) && owned.Contains(a) ? a : null;

        var open = charges.Where(c => AccountOf(c) != null).ToList();
        var bookings = new List<BookingToMatch>();
        var bookingRows = new Dictionary<long, (long Account, long Cents)>();
        if (open.Count > 0)
        {
            var min = new DateTimeOffset(open.Min(c => c.Date).AddDays(-AmazonMatcher.DaysBefore - 1), TimeSpan.Zero).ToUnixTimeSeconds();
            var max = new DateTimeOffset(open.Max(c => c.Date).AddDays(AmazonMatcher.DaysAfter + 2), TimeSpan.Zero).ToUnixTimeSeconds();
            var accounts = open.Select(c => AccountOf(c)!.Value).Distinct().ToList();
            var rows = await db.Transactions.AsNoTracking()
                .Where(t => t.OwnerUserId == ownerUserId && t.AccountId != null && accounts.Contains(t.AccountId.Value)
                    && t.Timestamp >= min && t.Timestamp <= max && t.Splitted != SplitState.Child)
                .Select(t => new { t.Id, t.AccountId, t.Timestamp, t.Amount, t.SplitOriginalAmount, t.Name, t.Purpose })
                .ToListAsync(ct);
            foreach (var r in rows)
            {
                var cents = r.SplitOriginalAmount ?? r.Amount;
                bookingRows[r.Id] = (r.AccountId!.Value, cents);
                bookings.Add(new BookingToMatch(r.Id, r.AccountId!.Value, DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(r.Timestamp).UtcDateTime),
                    cents, AmazonMatcher.IsShopLike(r.Name, r.Purpose)));
            }
        }

        // Earlier matches stay while their booking is still there with the same account and amount.
        var kept = new HashSet<long>();
        foreach (var c in charges)
        {
            var account = AccountOf(c);
            if (account == null || AmazonPages.IsPointsLabel(c.Instrument))
            {
                c.State = AmazonChargeState.Skipped;
                c.TransactionId = null;
            }
            else if (c.TransactionId is { } t && bookingRows.TryGetValue(t, out var b) && b.Account == account && b.Cents == c.AmountCents && kept.Add(t))
            {
                c.State = AmazonChargeState.Matched;
            }
            else
            {
                c.TransactionId = null;
                c.State = AmazonChargeState.Unmatched;
            }
        }
        var toPlace = charges.Where(c => c.State == AmazonChargeState.Unmatched)
            .Select(c => new ChargeToMatch(c.Id, AccountOf(c)!.Value, DateOnly.FromDateTime(c.Date), c.AmountCents)).ToList();
        var outcome = AmazonMatcher.Match(toPlace, bookings, kept);
        foreach (var c in charges.Where(c => c.State == AmazonChargeState.Unmatched))
        {
            if (outcome.Matches.TryGetValue(c.Id, out var t))
            {
                c.TransactionId = t;
                c.State = AmazonChargeState.Matched;
            }
            else if (outcome.Ambiguous.Contains(c.Id))
            {
                c.State = AmazonChargeState.Ambiguous;
            }
        }
        await db.SaveChangesAsync(ct);

        // Item texts of the matched bookings.
        var matched = charges.Where(c => c.TransactionId != null).ToList();
        var texts = await ItemTextsAsync(db, ownerUserId, matched, ct);
        var touched = texts.Keys.Concat(previouslyEnriched).Distinct().ToList();
        foreach (var chunk in touched.Chunk(500))
        {
            foreach (var row in await db.Transactions.Where(t => chunk.Contains(t.Id) && t.OwnerUserId == ownerUserId).ToListAsync(ct))
                row.OrderItems = texts.GetValueOrDefault(row.Id);
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>For each booking with matched charges: the items those charges paid for, as one line.</summary>
    internal static async Task<Dictionary<long, string>> ItemTextsAsync(BudgetDbContext db, string ownerUserId, IReadOnlyList<AmazonCharge> matched, CancellationToken ct)
    {
        var result = new Dictionary<long, string>();
        foreach (var (transactionId, paid) in await ItemsByBookingAsync(db, ownerUserId, matched, ct))
        {
            if (paid.Items.Count > 0) result[transactionId] = AmazonSplit.Describe(paid.Items.Select(i => (i.Title, i.Quantity)));
        }
        return result;
    }

    /// <summary>For each booking with matched charges: the order items those charges paid for (see <see cref="AmazonSplit.ItemsForCharge"/>).</summary>
    public static async Task<Dictionary<long, BookingItems>> ItemsByBookingAsync(
        BudgetDbContext db, string ownerUserId, IReadOnlyList<AmazonCharge> matched, CancellationToken ct)
    {
        var result = new Dictionary<long, BookingItems>();
        if (matched.Count == 0) return result;
        var orderIds = matched.Select(c => c.OrderId).Distinct(StringComparer.Ordinal).ToList();
        var loginIds = matched.Select(c => c.BankLoginId).Distinct().ToList();
        var orders = await db.AmazonOrders.AsNoTracking().Include(o => o.Items)
            .Where(o => o.OwnerUserId == ownerUserId && loginIds.Contains(o.BankLoginId) && orderIds.Contains(o.OrderId)).ToListAsync(ct);
        var all = await db.AmazonCharges.AsNoTracking()
            .Where(c => c.OwnerUserId == ownerUserId && loginIds.Contains(c.BankLoginId) && orderIds.Contains(c.OrderId)).ToListAsync(ct);
        foreach (var c in matched)
        {
            var order = orders.Find(o => o.BankLoginId == c.BankLoginId && o.OrderId == c.OrderId);
            if (order == null || order.Items.Count == 0) continue;
            var items = order.Items.OrderBy(i => i.Id)
                .Select(i => new SplitItem(i.Id, i.Title, i.Quantity, i.PriceCents * i.Quantity, i.Shipment, i.CategoryId)).ToList();
            var ofOrder = all.Where(x => x.BankLoginId == c.BankLoginId && x.OrderId == c.OrderId).ToList();
            var points = ofOrder.Where(x => AmazonPages.IsPointsLabel(x.Instrument) && x.AmountCents < 0).Sum(x => -x.AmountCents);
            var card = ofOrder.Where(x => !AmazonPages.IsPointsLabel(x.Instrument)).Select(x => x.AmountCents).ToList();
            var paid = AmazonSplit.ItemsForCharge(items, c.AmountCents, card, points, order.TotalCents);
            var id = c.TransactionId!.Value;
            result[id] = result.TryGetValue(id, out var before)
                ? new BookingItems([.. before.Items, .. paid.Items.Where(p => before.Items.All(b => b.Id != p.Id))], before.Unclear || paid.Unclear, before.Instalment || paid.Instalment, [.. before.OrderIds, c.OrderId])
                : new BookingItems(paid.Items, paid.Unclear, paid.Instalment, [c.OrderId]);
        }
        return result;
    }

    private static long Cents(decimal amount) => (long)Math.Round(amount * 100m, MidpointRounding.AwayFromZero);
}
