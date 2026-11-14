using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.Connectors.Receipts;

/// <summary>Places receipt lines the memory and keywords left open (the optional AI); line id → category.</summary>
public interface IReceiptItemClassifier
{
    Task<Dictionary<long, long>> ClassifyAsync(string ownerUserId, DateOnly date, IReadOnlyList<ReceiptLine> lines, CancellationToken ct);
}

/// <summary>
/// Stores parsed receipts, matches them with the owner's bookings (see <see cref="ReceiptMatcher"/>) and splits a matched
/// booking by the receipt's lines (see <see cref="ReceiptCategorizer"/>): one part per category, each carrying its lines
/// in <see cref="Transaction.OrderItems"/>; the booking itself carries all lines. Matching runs after every bank sync and
/// mailbox read, so a receipt that arrived before its booking is placed once the booking is there.
///
/// A booking is split automatically only when it is booked, not split yet and its category was not chosen by hand, or
/// when it carries exactly the split this receipt wrote before (then the split follows the receipt, e.g. after the
/// owner moved a line). A split the owner changed, and a booking they categorized by hand, keep what they have; the
/// receipt only adds its lines. Running again changes nothing. Works on a system context and only ever pairs receipts
/// and bookings of the same owner.
/// </summary>
public sealed class ReceiptStore
{
    private readonly IReceiptItemClassifier? _ai;

    public ReceiptStore(IReceiptItemClassifier? ai = null) => _ai = ai;

    /// <summary>Stores a parsed receipt for the owner; false when it is known already (same shop and invoice number).</summary>
    public static async Task<bool> AddAsync(
        BudgetDbContext db, string ownerUserId, string shop, ParsedReceipt r, string? messageId, DateTime nowUtc, CancellationToken ct, string? store = null)
    {
        if (await db.ReceiptInvoices.AnyAsync(i => i.OwnerUserId == ownerUserId && i.Shop == shop && i.InvoiceNumber == r.InvoiceNumber, ct)) return false;
        static DateTime? Utc(DateOnly? d) => d?.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var invoice = new ReceiptInvoice
        {
            OwnerUserId = ownerUserId,
            Shop = shop,
            Store = Truncate(store, 200),
            InvoiceNumber = Truncate(r.InvoiceNumber, 40)!,
            OrderNumber = Truncate(r.OrderNumber, 40),
            OrderDate = Utc(r.OrderDate),
            DeliveryDate = Utc(r.DeliveryDate),
            InvoiceDate = Utc(r.InvoiceDate),
            TotalCents = r.TotalCents,
            BonusCents = r.BonusCents,
            PaymentCents = r.PaymentCents,
            MessageId = Truncate(messageId, 500),
            ReadAt = nowUtc,
        };
        var position = 0;
        foreach (var i in r.Items)
            invoice.Items.Add(new ReceiptItem
            {
                OwnerUserId = ownerUserId,
                Position = position++,
                Name = Truncate(i.Name, 300)!,
                Quantity = Truncate(i.Quantity, 20)!,
                VatCode = Truncate(i.VatCode, 5),
                UnitCents = i.UnitCents,
                TotalCents = i.TotalCents,
                NoDiscount = i.NoDiscount,
                Kind = i.Kind,
            });
        db.ReceiptInvoices.Add(invoice);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>(Re)matches every receipt of the owner and brings the matched bookings' splits and item texts up to date.</summary>
    public async Task MatchAsync(BudgetDbContext db, string ownerUserId, CancellationToken ct)
    {
        var invoices = await db.ReceiptInvoices.Include(i => i.Items).Where(i => i.OwnerUserId == ownerUserId).ToListAsync(ct);
        if (invoices.Count == 0) return;

        var dated = invoices.Where(i => (i.DeliveryDate ?? i.OrderDate) != null).ToList();
        var bookings = new List<ReceiptBooking>();
        if (dated.Count > 0)
        {
            var min = new DateTimeOffset(dated.Min(i => (i.DeliveryDate ?? i.OrderDate)!.Value).AddDays(-1), TimeSpan.Zero).ToUnixTimeSeconds();
            var max = new DateTimeOffset(dated.Max(i => (i.DeliveryDate ?? i.OrderDate)!.Value).AddDays(ReceiptMatcher.MaxDaysAfter + 2), TimeSpan.Zero).ToUnixTimeSeconds();
            var rows = await db.Transactions.AsNoTracking()
                .Where(t => t.OwnerUserId == ownerUserId && t.Timestamp >= min && t.Timestamp <= max && t.Splitted != SplitState.Child)
                .Select(t => new { t.Id, t.Timestamp, t.Amount, t.SplitOriginalAmount, t.Name, t.Purpose })
                .ToListAsync(ct);
            var shops = invoices.Select(i => i.Shop).Distinct(StringComparer.Ordinal).ToList();
            bookings = rows.Where(r => shops.Any(s => ReceiptMatcher.IsShop(s, r.Name, r.Purpose)))
                .Select(r => new ReceiptBooking(r.Id, DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(r.Timestamp).UtcDateTime),
                    r.SplitOriginalAmount ?? r.Amount, r.Name, r.Purpose)).ToList();
        }

        // Earlier matches stay while their booking is still there with the same amount.
        var kept = new HashSet<long>();
        var lost = new List<long>();
        foreach (var i in invoices)
        {
            if (i.TransactionId is { } t && await StillFitsAsync(db, ownerUserId, t, i.PaymentCents, ct) && kept.Add(t))
            {
                i.State = ReceiptMatchState.Matched;
                continue;
            }
            if (i.TransactionId is { } gone) lost.Add(gone);
            i.TransactionId = null;
            i.SplitSignature = null;
            i.State = ReceiptMatchState.Unmatched;
        }
        var open = invoices.Where(i => i.State == ReceiptMatchState.Unmatched)
            .Select(i => new ReceiptToMatch(i.Id, i.PaymentCents, Day(i.OrderDate), Day(i.DeliveryDate), i.Shop)).ToList();
        var outcome = ReceiptMatcher.Match(open, bookings, kept);
        foreach (var i in invoices.Where(i => i.State == ReceiptMatchState.Unmatched))
        {
            if (outcome.Matches.TryGetValue(i.Id, out var t))
            {
                i.TransactionId = t;
                i.State = ReceiptMatchState.Matched;
            }
            else if (outcome.Ambiguous.Contains(i.Id))
            {
                i.State = ReceiptMatchState.Ambiguous;
            }
        }
        await db.SaveChangesAsync(ct);

        // A booking that lost its receipt loses the receipt's item text (its split, if any, stays as it is).
        foreach (var id in lost.Where(l => !kept.Contains(l) && invoices.All(i => i.TransactionId != l)))
            await db.Transactions.Where(t => t.Id == id && t.OwnerUserId == ownerUserId).ExecuteUpdateAsync(u => u.SetProperty(t => t.OrderItems, (string?)null), ct);

        foreach (var i in invoices.Where(i => i.TransactionId != null))
            await ApplyAsync(db, i, force: false, ct);
    }

    private static async Task<bool> StillFitsAsync(BudgetDbContext db, string owner, long id, long paymentCents, CancellationToken ct)
    {
        var row = await db.Transactions.AsNoTracking().Where(t => t.Id == id && t.OwnerUserId == owner && t.Splitted != SplitState.Child)
            .Select(t => new { Cents = t.SplitOriginalAmount ?? t.Amount }).FirstOrDefaultAsync(ct);
        return row != null && row.Cents == -paymentCents;
    }

    /// <summary>
    /// Brings one matched receipt's booking up to date: the lines' categories, the split (see the class remarks; with
    /// <paramref name="force"/> also over a split or category the owner set by hand) and the item texts.
    /// True when the booking was split or re-split.
    /// </summary>
    public async Task<bool> ApplyAsync(BudgetDbContext db, ReceiptInvoice invoice, bool force, CancellationToken ct)
    {
        if (invoice.TransactionId is not { } txId) return false;
        var parent = await db.Transactions.FirstOrDefaultAsync(t => t.Id == txId && t.OwnerUserId == invoice.OwnerUserId, ct);
        if (parent == null) return false;
        var items = invoice.Items.OrderBy(i => i.Position).ToList();
        var lines = items.Select(i => new ReceiptLine(i.Id, i.Name, i.VatCode, i.TotalCents, i.NoDiscount, i.Kind)).ToList();

        var children = parent.Splitted == SplitState.Parent
            ? await db.Transactions.Where(t => t.SplitParentId == parent.Id && t.Splitted == SplitState.Child && t.OwnerUserId == parent.OwnerUserId).ToListAsync(ct)
            : [];
        var ours = invoice.SplitSignature != null && parent.Splitted == SplitState.Parent
            && ReceiptCategorizer.Signature(children.Select(c => (c.Amount, c.CategoryId))) == invoice.SplitSignature;
        if (!ours) invoice.SplitSignature = null;
        var mayWrite = force
            ? parent.Splitted != SplitState.Remainder
            : ours || (parent.Booked && (parent.Splitted ?? SplitState.Normal) == SplitState.Normal && parent.CategorySetBy != TransactionCategorySource.Manual);

        // Categories: the owner's moves stay, AI answers are asked once and kept, the rest follows memory and keywords.
        var settings = await db.ReceiptSettings.AsNoTracking().FirstOrDefaultAsync(s => s.OwnerUserId == invoice.OwnerUserId, ct);
        var nonFood = settings?.NonFoodCategoryId is { } nf && await db.Categories.AnyAsync(c => c.Id == nf && c.OwnerUserId == invoice.OwnerUserId, ct) ? nf : (long?)null;
        var memory = await MemoryAsync(db, invoice.OwnerUserId, items.Select(i => ReceiptCategorizer.Normalize(i.Name)).Distinct(StringComparer.Ordinal).ToList(), ct);
        var manual = items.Where(i => i.CategorySource == ReceiptCategorySource.Manual).ToDictionary(i => i.Id, i => i.CategoryId);
        var ai = items.Where(i => i.CategorySource == ReceiptCategorySource.Ai && i.CategoryId != null).ToDictionary(i => i.Id, i => i.CategoryId!.Value);
        if (mayWrite && _ai != null)
        {
            var ask = ReceiptCategorizer.Undecided(lines, nonFood, memory).Where(l => items.First(i => i.Id == l.Id).CategorySource == null).ToList();
            if (ask.Count > 0)
            {
                var date = Day(invoice.DeliveryDate ?? invoice.OrderDate) ?? DateOnly.FromDateTime(parent.Date);
                foreach (var (id, category) in await _ai.ClassifyAsync(invoice.OwnerUserId, date, ask, ct)) ai[id] = category;
            }
        }
        var shops = await ShopCategoriesAsync(db, invoice.OwnerUserId, ct);
        var assigned = ReceiptCategorizer.Assign(lines, parent.CategoryId, nonFood, memory, ai, manual, shops);
        foreach (var item in items)
        {
            var a = assigned[item.Id];
            if (item.CategorySource == ReceiptCategorySource.Manual) continue;
            item.CategoryId = a.Source == ReceiptCategorySource.Default ? null : a.CategoryId;
            // A line is marked as decided (so the AI is not asked about it again) once the receipt could split the booking.
            if (mayWrite || a.Source != ReceiptCategorySource.Default || item.CategorySource != null) item.CategorySource = a.Source;
        }

        var all = ReceiptCategorizer.Describe(items.Select(i => (i.Name, i.Quantity)));
        parent.OrderItems = all;
        var changed = false;
        if (mayWrite)
        {
            var parts = ReceiptCategorizer.Parts(lines, assigned, invoice.BonusCents, parent.SplitOriginalAmount ?? parent.Amount, parent.CategoryId);
            if (parts.Count >= 2)
            {
                var signature = ReceiptCategorizer.Signature(parts.Select(p => (p.AmountCents, p.CategoryId)));
                var texts = parts.Select(p => ReceiptCategorizer.Describe(p.LineIds.Select(id => items.First(i => i.Id == id)).Select(i => (i.Name, i.Quantity)))).ToList();
                var sameTexts = children.Count == parts.Count && children.OrderBy(c => c.Id).Select(c => c.OrderItems).SequenceEqual(texts, StringComparer.Ordinal);
                if (signature != invoice.SplitSignature || !ours || !sameTexts)
                {
                    await SplitService.WritePartsAsync(db, parent, parts.Select((p, n) => new SplitPartWrite(p.AmountCents, p.CategoryId, null, texts[n])).ToList(), ct);
                    invoice.SplitSignature = signature;
                    changed = true;
                }
            }
            else if (ours)
            {
                // Everything is in one category again: the receipt's split goes.
                db.Transactions.RemoveRange(children);
                parent.Splitted = SplitState.Normal;
                parent.SplitOriginalAmount = null;
                parent.SplitCheck = false;
                invoice.SplitSignature = null;
                changed = true;
            }
        }
        await db.SaveChangesAsync(ct);
        return changed;
    }

    /// <summary>
    /// The categories of the owner's bookings that receipts were matched to: the shops' own categories ("Supermarkt ›
    /// Lidl", "Supermarkt › Rewe"), which an item of another shop's receipt never goes to.
    /// </summary>
    public static async Task<HashSet<long>> ShopCategoriesAsync(BudgetDbContext db, string ownerUserId, CancellationToken ct)
    {
        var ids = db.ReceiptInvoices.Where(i => i.OwnerUserId == ownerUserId && i.TransactionId != null).Select(i => i.TransactionId!.Value);
        var rows = await db.Transactions.AsNoTracking().Where(t => t.OwnerUserId == ownerUserId && ids.Contains(t.Id) && t.CategoryId != null)
            .Select(t => t.CategoryId!.Value).Distinct().ToListAsync(ct);
        return rows.ToHashSet();
    }

    /// <summary>The owner's memory entries for these normalised names.</summary>
    public static async Task<Dictionary<string, long>> MemoryAsync(BudgetDbContext db, string ownerUserId, IReadOnlyList<string> names, CancellationToken ct)
    {
        var rows = await db.ItemCategoryMemory.AsNoTracking().Where(m => m.OwnerUserId == ownerUserId && names.Contains(m.NormalizedName))
            .Select(m => new { m.NormalizedName, m.CategoryId }).ToListAsync(ct);
        var valid = (await db.Categories.AsNoTracking().Where(c => c.OwnerUserId == ownerUserId).Select(c => c.Id).ToListAsync(ct)).ToHashSet();
        return rows.Where(r => valid.Contains(r.CategoryId)).ToDictionary(r => r.NormalizedName, r => r.CategoryId, StringComparer.Ordinal);
    }

    private static DateOnly? Day(DateTime? d) => d is { } v ? DateOnly.FromDateTime(v) : null;

    private static string? Truncate(string? value, int max) => value == null || value.Length <= max ? value : value[..max];
}
