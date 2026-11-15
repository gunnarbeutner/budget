using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.Connectors.Receipts;

/// <summary>A user's price history: their products, the price index and their regular purchases.</summary>
public sealed record PriceHistoryView(IReadOnlyList<ProductHistory> Products, PriceIndex Index, IReadOnlyList<RegularProduct> Regulars, DateOnly Today);

/// <summary>
/// Per-user cache of <see cref="PriceHistoryView"/>. Dropped entirely on any change to imported data, receipts,
/// cloud files or projects (sharing changes what a user sees), and after <see cref="Lifetime"/>. A singleton for the
/// app's lifetime, subscribed to the change notifications.
/// </summary>
public sealed class PriceHistoryCache
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<string, (DateTimeOffset At, PriceHistoryView View)> _entries = new(StringComparer.Ordinal);
    private int _generation;

    public PriceHistoryCache(ChangeNotificationService notifier) => notifier.Changed += OnChanged;

    private void OnChanged(DataChange change, Guid? origin)
    {
        if (change.Area is DataArea.ImportedData or DataArea.CloudFiles or DataArea.Projects) Clear();
    }

    public PriceHistoryView? Get(string user, DateTimeOffset now) =>
        _entries.TryGetValue(user, out var e) && now - e.At < Lifetime && e.View.Today == DateOnly.FromDateTime(now.UtcDateTime) ? e.View : null;

    /// <summary>Counts the clears: a view computed while a clear happened is not stored.</summary>
    public int Generation => Volatile.Read(ref _generation);

    public void Set(string user, DateTimeOffset now, PriceHistoryView view, int generation)
    {
        if (generation == Generation) _entries[user] = (now, view);
    }

    public void Clear()
    {
        Interlocked.Increment(ref _generation);
        _entries.Clear();
    }
}

/// <summary>
/// Price history over the itemised receipts the signed-in user sees, with the visibility of
/// <see cref="ReceiptService"/>: their own receipts, and other users' receipts linked to bookings the user can see.
/// Product lines only (no deposits, fees or credits). Merges of products are the user's own.
/// </summary>
public sealed class PriceHistoryService
{
    private readonly IDbContextFactory<BudgetDbContext> _dbFactory;
    private readonly SystemDbContextFactory _system;
    private readonly AccessScopeProvider _access;
    private readonly PriceHistoryCache _cache;
    private readonly ChangeNotificationService _notifier;
    private readonly ChangeOrigin _origin;
    private readonly TimeProvider _clock;

    public PriceHistoryService(IDbContextFactory<BudgetDbContext> dbFactory, SystemDbContextFactory system, AccessScopeProvider access,
        PriceHistoryCache cache, ChangeNotificationService notifier, ChangeOrigin origin, TimeProvider clock)
    {
        _dbFactory = dbFactory;
        _system = system;
        _access = access;
        _cache = cache;
        _notifier = notifier;
        _origin = origin;
        _clock = clock;
    }

    private async Task<string> UserAsync() => (await _access.GetAsync()).UserId ?? throw new AccessDeniedException("Not signed in.");

    public async Task<PriceHistoryView> GetAsync(CancellationToken ct = default)
    {
        var user = await UserAsync();
        var now = _clock.GetUtcNow();
        if (_cache.Get(user, now) is { } cached) return cached;
        var generation = _cache.Generation;
        var lines = await LinesAsync(user, ct);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var merges = await db.ReceiptProductMerges.AsNoTracking().Where(m => m.OwnerUserId == user)
            .ToDictionaryAsync(m => m.Shop + "|" + m.FromKey, m => m.Shop + "|" + m.ToKey, StringComparer.Ordinal, ct);
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var products = PriceAnalysis.Products(lines, merges);
        var view = new PriceHistoryView(products, PriceAnalysis.Index(products, today), PriceAnalysis.Regulars(products, today), today);
        _cache.Set(user, now, view, generation);
        return view;
    }

    /// <summary>The product lines of every receipt the user sees.</summary>
    private async Task<List<PriceLine>> LinesAsync(string user, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        await using var sys = await _system.CreateDbContextAsync(ct);
        var foreign = await sys.ReceiptInvoices.AsNoTracking().Where(r => r.OwnerUserId != user && r.TransactionId != null)
            .Select(r => new { r.Id, Tx = r.TransactionId!.Value }).ToListAsync(ct);
        var visible = new HashSet<long>();
        foreach (var chunk in foreign.Select(f => f.Tx).Distinct().Chunk(500))
            visible.UnionWith(await db.Transactions.AsNoTracking().Where(t => chunk.Contains(t.Id)).Select(t => t.Id).ToListAsync(ct));
        var ids = foreign.Where(f => visible.Contains(f.Tx)).Select(f => f.Id).ToList();

        var rows = await sys.ReceiptItems.AsNoTracking()
            .Where(i => i.Kind == ReceiptItemKind.Product && (i.Invoice.OwnerUserId == user || ids.Contains(i.ReceiptInvoiceId)))
            .Select(i => new
            {
                i.ReceiptInvoiceId,
                i.Invoice.OwnerUserId,
                i.Invoice.Shop,
                i.Invoice.OrderNumber,
                i.Position,
                Date = i.Invoice.DeliveryDate ?? i.Invoice.OrderDate ?? i.Invoice.InvoiceDate,
                i.Name,
                i.Quantity,
                i.UnitCents,
                i.TotalCents,
            }).ToListAsync(ct);
        // Amazon lists an order's items with each of its charges' receipts: one purchase per order and position.
        return rows.Where(r => r.Date != null)
            .DistinctBy(r => r.Shop == ReceiptShops.Amazon && r.OrderNumber != null ? $"{r.OwnerUserId}|{r.OrderNumber}|{r.Position}" : $"#{r.ReceiptInvoiceId}|{r.Position}", StringComparer.Ordinal)
            .Select(r => new PriceLine(r.ReceiptInvoiceId, r.Shop, DateOnly.FromDateTime(r.Date!.Value), r.Name, r.Quantity, r.UnitCents, r.TotalCents, r.OwnerUserId == user))
            .ToList();
    }

    /// <summary>Merges product <paramref name="fromId"/> into <paramref name="toId"/> (ids "shop|key", same shop) for the signed-in user.</summary>
    public async Task MergeAsync(string fromId, string toId, CancellationToken ct = default)
    {
        var user = await UserAsync();
        var (shop, from) = Split(fromId);
        var (toShop, to) = Split(toId);
        if (shop != toShop) throw new InvalidOperationException("Only products of the same shop can be merged.");
        if (from == to) return;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var existing = await db.ReceiptProductMerges.FirstOrDefaultAsync(m => m.OwnerUserId == user && m.Shop == shop && m.FromKey == from, ct);
        if (existing != null) existing.ToKey = to;
        else db.ReceiptProductMerges.Add(new ReceiptProductMerge { OwnerUserId = user, Shop = shop, FromKey = from, ToKey = to });
        // A product merged into the one now being merged away follows it.
        foreach (var m in await db.ReceiptProductMerges.Where(m => m.OwnerUserId == user && m.Shop == shop && m.ToKey == from).ToListAsync(ct))
            if (m.FromKey == to) db.ReceiptProductMerges.Remove(m);
            else m.ToKey = to;
        await db.SaveChangesAsync(ct);
        Changed(user);
    }

    /// <summary>Takes a product (id "shop|key") that was merged into another apart again.</summary>
    public async Task UnmergeAsync(string id, CancellationToken ct = default)
    {
        var user = await UserAsync();
        var (shop, key) = Split(id);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.ReceiptProductMerges.Where(m => m.OwnerUserId == user && m.Shop == shop && m.FromKey == key).ToListAsync(ct);
        db.ReceiptProductMerges.RemoveRange(rows);
        await db.SaveChangesAsync(ct);
        Changed(user);
    }

    private void Changed(string user)
    {
        _cache.Clear();
        _notifier.Publish(new DataChange(DataArea.CloudFiles, UserId: user), _origin.Id);
    }

    private static (string Shop, string Key) Split(string id)
    {
        var bar = id.IndexOf('|', StringComparison.Ordinal);
        if (bar <= 0 || bar == id.Length - 1) throw new InvalidOperationException("Not a product.");
        return (id[..bar], id[(bar + 1)..]);
    }
}
