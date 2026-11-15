using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.Connectors.Receipts;

/// <summary>A project's price history for the signed-in user: its products and the receipts' payments.</summary>
public sealed record PriceHistoryView(IReadOnlyList<ProductHistory> Products, IReadOnlyList<ReceiptSpend> Receipts, DateOnly Today)
{
    /// <summary>The page's figures for a period.</summary>
    public PriceSummary Summarise(PricePeriod period) => PriceAnalysis.Summarise(Products, Receipts, period, Today);
}

/// <summary>
/// Per-user, per-project cache of <see cref="PriceHistoryView"/>. Dropped entirely on any change to imported data,
/// receipts, cloud files or projects (sharing changes what a user sees), and after <see cref="Lifetime"/>. A singleton
/// for the app's lifetime, subscribed to the change notifications.
/// </summary>
public sealed class PriceHistoryCache
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<(string User, int Project), (DateTimeOffset At, PriceHistoryView View)> _entries = new();
    private int _generation;

    public PriceHistoryCache(ChangeNotificationService notifier) => notifier.Changed += OnChanged;

    private void OnChanged(DataChange change, Guid? origin)
    {
        if (change.Area is DataArea.ImportedData or DataArea.CloudFiles or DataArea.Projects) Clear();
    }

    public PriceHistoryView? Get(string user, int project, DateTimeOffset now) =>
        _entries.TryGetValue((user, project), out var e) && now - e.At < Lifetime && e.View.Today == DateOnly.FromDateTime(now.UtcDateTime) ? e.View : null;

    /// <summary>Counts the clears: a view computed while a clear happened is not stored.</summary>
    public int Generation => Volatile.Read(ref _generation);

    public void Set(string user, int project, DateTimeOffset now, PriceHistoryView view, int generation)
    {
        if (generation == Generation) _entries[(user, project)] = (now, view);
    }

    public void Clear()
    {
        Interlocked.Increment(ref _generation);
        _entries.Clear();
    }
}

/// <summary>
/// Price history over the itemised receipts of a project, with the visibility of
/// <see cref="ReceiptService.ListProjectAsync"/>: receipts linked to bookings on the project's accounts that the user
/// may see, and open receipts of sources booking into it. Product lines only (no deposits, fees or credits). Merges of
/// products are the user's own and apply in every project.
/// </summary>
public sealed class PriceHistoryService
{
    private readonly IDbContextFactory<BudgetDbContext> _dbFactory;
    private readonly SystemDbContextFactory _system;
    private readonly AccessScopeProvider _access;
    private readonly ReceiptService _receipts;
    private readonly PriceHistoryCache _cache;
    private readonly ChangeNotificationService _notifier;
    private readonly ChangeOrigin _origin;
    private readonly TimeProvider _clock;

    public PriceHistoryService(IDbContextFactory<BudgetDbContext> dbFactory, SystemDbContextFactory system, AccessScopeProvider access, ReceiptService receipts,
        PriceHistoryCache cache, ChangeNotificationService notifier, ChangeOrigin origin, TimeProvider clock)
    {
        _dbFactory = dbFactory;
        _system = system;
        _access = access;
        _receipts = receipts;
        _cache = cache;
        _notifier = notifier;
        _origin = origin;
        _clock = clock;
    }

    private async Task<string> UserAsync() => (await _access.GetAsync()).UserId ?? throw new AccessDeniedException("Not signed in.");

    /// <summary>The project's price history; throws <see cref="AccessDeniedException"/> without access to the project.</summary>
    public async Task<PriceHistoryView> GetAsync(int projectId, CancellationToken ct = default)
    {
        var user = await UserAsync();
        var now = _clock.GetUtcNow();
        if (_cache.Get(user, projectId, now) is { } cached) return cached;
        var generation = _cache.Generation;
        var receipts = (await _receipts.ListProjectAsync(projectId, take: int.MaxValue, ct: ct)).Where(r => r.Date != null).ToList();
        var lines = await LinesAsync(user, receipts.Select(r => r.Id).ToList(), ct);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var merges = await db.ReceiptProductMerges.AsNoTracking().Where(m => m.OwnerUserId == user)
            .ToDictionaryAsync(m => m.Shop + "|" + m.FromKey, m => m.Shop + "|" + m.ToKey, StringComparer.Ordinal, ct);
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var spend = receipts.Select(r => new ReceiptSpend(r.Shop, DateOnly.FromDateTime(r.Date!.Value), r.PaymentCents)).ToList();
        var view = new PriceHistoryView(PriceAnalysis.Products(lines, merges), spend, today);
        _cache.Set(user, projectId, now, view, generation);
        return view;
    }

    /// <summary>The product lines of the given receipts.</summary>
    private async Task<List<PriceLine>> LinesAsync(string user, List<long> ids, CancellationToken ct)
    {
        await using var sys = await _system.CreateDbContextAsync(ct);
        var rows = new List<LineRow>();
        foreach (var chunk in ids.Chunk(500))
            rows.AddRange(await sys.ReceiptItems.AsNoTracking()
                .Where(i => i.Kind == ReceiptItemKind.Product && chunk.Contains(i.ReceiptInvoiceId))
                .Select(i => new LineRow(i.ReceiptInvoiceId, i.Invoice.OwnerUserId, i.Invoice.Shop, i.Invoice.OrderNumber, i.Position,
                    i.Invoice.DeliveryDate ?? i.Invoice.OrderDate ?? i.Invoice.InvoiceDate, i.Name, i.Quantity, i.UnitCents, i.TotalCents))
                .ToListAsync(ct));
        // Amazon lists an order's items with each of its charges' receipts: one purchase per order and position.
        return rows.Where(r => r.Date != null)
            .DistinctBy(r => r.Shop == ReceiptShops.Amazon && r.OrderNumber != null ? $"{r.OwnerUserId}|{r.OrderNumber}|{r.Position}" : $"#{r.ReceiptInvoiceId}|{r.Position}", StringComparer.Ordinal)
            .Select(r => new PriceLine(r.ReceiptInvoiceId, r.Shop, DateOnly.FromDateTime(r.Date!.Value), r.Name, r.Quantity, r.UnitCents, r.TotalCents, r.OwnerUserId == user))
            .ToList();
    }

    private sealed record LineRow(long ReceiptInvoiceId, string OwnerUserId, string Shop, string? OrderNumber, int Position, DateTime? Date,
        string Name, string Quantity, long? UnitCents, long TotalCents);

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
