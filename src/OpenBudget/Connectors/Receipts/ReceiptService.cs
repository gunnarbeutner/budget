using Microsoft.EntityFrameworkCore;
using OpenBudget.Connectors.OneDrive;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.Connectors.Receipts;

/// <summary>The signed-in user's receipt settings and mailbox state.</summary>
public sealed record ReceiptSettingsView(
    bool MicrosoftAvailable, CloudConnectionView? Connection, long? NonFoodCategoryId, DateTime? MailCheckedAt, DateTime? MailAttemptAt, string? MailLastError);

/// <summary>A stored receipt as the list shows it.</summary>
public sealed record ReceiptView(
    long Id, string Shop, string? OrderNumber, DateTime? OrderDate, DateTime? DeliveryDate, long PaymentCents, int Items,
    ReceiptMatchState State, long? TransactionId, DateTime? BookingDate, bool Split, string? Store = null)
{
    /// <summary>The shop and where or what: "REWE B-…" or "Lidl Teststraße 1, Musterstadt".</summary>
    public string Title => ReceiptService.Title(Shop, Store, OrderNumber);
}

/// <summary>A receipt line in a booking's details: name, quantity, line total and the category it went to.</summary>
public sealed record ReceiptLineView(long Id, string Name, string Quantity, long TotalCents, ReceiptItemKind Kind, long? CategoryId, string? Source);

/// <summary>The lines of one category: the category (null: the booking's own) and its share of the booking.</summary>
public sealed record ReceiptGroupView(long? CategoryId, long AmountCents, IReadOnlyList<ReceiptLineView> Lines);

/// <summary>A booking's receipt: its shop, order, the lines grouped by category and whether the receipt split the booking.</summary>
public sealed record BookingReceiptView(long ReceiptId, long TransactionId, string Shop, string? OrderNumber, DateTime? DeliveryDate, long BonusCents,
    IReadOnlyList<ReceiptGroupView> Groups, bool SplitByReceipt, string? Store = null)
{
    /// <summary>See <see cref="ReceiptView.Title"/>.</summary>
    public string Title => ReceiptService.Title(Shop, Store, OrderNumber);
}

/// <summary>A receipt source's receipts: how many, how many found their booking, and the latest still waiting for one.</summary>
public sealed record ReceiptSourceSummary(int Count, int Matched, int OpenCount, IReadOnlyList<ReceiptView> Open);

/// <summary>
/// The owner's side of receipts: settings (the non-food category), the list of read receipts, a booking's receipt
/// lines, and moving a line to another category (which the item memory keeps for later receipts and which re-splits the
/// booking). Only the receipt's owner sees or changes anything (owner-only query filters); the booking is rewritten
/// on a system context for that owner.
/// </summary>
public sealed class ReceiptService
{
    private readonly IDbContextFactory<BudgetDbContext> _dbFactory;
    private readonly SystemDbContextFactory _system;
    private readonly AccessScopeProvider _access;
    private readonly OneDriveService _oneDrive;
    private readonly ReceiptStore _store;
    private readonly MailReceiptSync _mail;
    private readonly ChangeNotificationService _notifier;
    private readonly ChangeOrigin _origin;
    private readonly TimeProvider _clock;

    public ReceiptService(
        IDbContextFactory<BudgetDbContext> dbFactory, SystemDbContextFactory system, AccessScopeProvider access, OneDriveService oneDrive,
        ReceiptStore store, MailReceiptSync mail, ChangeNotificationService notifier, ChangeOrigin origin, TimeProvider clock)
    {
        _dbFactory = dbFactory;
        _system = system;
        _access = access;
        _oneDrive = oneDrive;
        _store = store;
        _mail = mail;
        _notifier = notifier;
        _origin = origin;
        _clock = clock;
    }

    /// <summary>A receipt's heading: the shop's name and its store or order number.</summary>
    public static string Title(string shop, string? store, string? orderNumber) => shop switch
    {
        ReceiptShops.Lidl => store is { Length: > 0 } s ? (s.StartsWith("Lidl", StringComparison.OrdinalIgnoreCase) ? s : "Lidl " + s) : "Lidl",
        ReceiptShops.Rewe => ("REWE " + orderNumber).Trim(),
        _ => (shop + " " + (store ?? orderNumber)).Trim(),
    };

    private async Task<string> UserAsync() => (await _access.GetAsync()).UserId ?? throw new AccessDeniedException("Not signed in.");

    public async Task<ReceiptSettingsView> GetSettingsAsync(CancellationToken ct = default)
    {
        var user = await UserAsync();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var s = await db.ReceiptSettings.AsNoTracking().FirstOrDefaultAsync(x => x.OwnerUserId == user, ct);
        var connection = _oneDrive.IsEnabled ? await _oneDrive.GetConnectionAsync(user, ct) : null;
        return new ReceiptSettingsView(_oneDrive.IsEnabled, connection, s?.NonFoodCategoryId, s?.MailCheckedAt, s?.MailAttemptAt, s?.MailLastError);
    }

    /// <summary>Sets where non-food items go (null: nowhere special) and brings the matched bookings up to date.</summary>
    public async Task SetNonFoodCategoryAsync(long? categoryId, CancellationToken ct = default)
    {
        var user = await UserAsync();
        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
        {
            if (categoryId is { } c && !await db.Categories.AnyAsync(x => x.Id == c && x.OwnerUserId == user, ct))
                throw new AccessDeniedException("Category not found.");
            var s = await db.ReceiptSettings.FirstOrDefaultAsync(x => x.OwnerUserId == user, ct);
            if (s == null)
            {
                s = new ReceiptSettings { OwnerUserId = user };
                db.ReceiptSettings.Add(s);
            }
            s.NonFoodCategoryId = categoryId;
            await db.SaveChangesAsync(ct);
        }
        await using (var sys = await _system.CreateDbContextAsync(ct))
            await _store.MatchAsync(sys, user, ct);
        Publish(user);
    }

    /// <summary>Reads the signed-in user's mailbox now.</summary>
    public async Task<MailCheckResult> CheckMailNowAsync(CancellationToken ct = default) => await _mail.CheckNowAsync(await UserAsync(), ct);

    /// <summary>Whether the signed-in user's mailbox is being read right now.</summary>
    public bool IsReadingMail(string userId) => _mail.IsReading(userId);

    /// <summary>Stops reading the signed-in user's mailbox.</summary>
    public async Task StopMailAsync(CancellationToken ct = default) => await _oneDrive.StopMailAccessAsync(await UserAsync(), ct);

    /// <summary>The user's receipts, newest first.</summary>
    public async Task<List<ReceiptView>> ListAsync(int take = 50, CancellationToken ct = default)
    {
        var user = await UserAsync();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.ReceiptInvoices.AsNoTracking().Where(r => r.OwnerUserId == user)
            .OrderByDescending(r => r.DeliveryDate ?? r.OrderDate).ThenByDescending(r => r.Id).Take(take)
            .Select(r => new { r.Id, r.Shop, r.Store, r.OrderNumber, r.OrderDate, r.DeliveryDate, r.PaymentCents, Items = r.Items.Count, r.State, r.TransactionId, r.SplitSignature })
            .ToListAsync(ct);
        var txIds = rows.Where(r => r.TransactionId != null).Select(r => r.TransactionId!.Value).ToList();
        var dates = await db.Transactions.AsNoTracking().Where(t => txIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Timestamp, ct);
        return rows.Select(r => new ReceiptView(r.Id, r.Shop, r.OrderNumber, r.OrderDate, r.DeliveryDate, r.PaymentCents, r.Items, r.State, r.TransactionId,
            r.TransactionId is { } t && dates.TryGetValue(t, out var ts) ? DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime : null, r.SplitSignature != null, r.Store)).ToList();
    }

    /// <summary>The user's receipts of one shop (a receipt source's login card): counts and the latest without a booking.</summary>
    public async Task<ReceiptSourceSummary> GetShopSummaryAsync(string shop, int take = 10, CancellationToken ct = default)
    {
        var user = await UserAsync();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var q = db.ReceiptInvoices.AsNoTracking().Where(r => r.OwnerUserId == user && r.Shop == shop);
        var count = await q.CountAsync(ct);
        var matched = await q.CountAsync(r => r.TransactionId != null, ct);
        // a receipt paid in cash has no booking to wait for
        var open = q.Where(r => r.TransactionId == null && r.PaymentCents > 0);
        var openCount = await open.CountAsync(ct);
        var latest = await open.OrderByDescending(r => r.OrderDate).ThenByDescending(r => r.Id).Take(take)
            .Select(r => new ReceiptView(r.Id, r.Shop, r.OrderNumber, r.OrderDate, r.DeliveryDate, r.PaymentCents, r.Items.Count, r.State, null, null, false, r.Store))
            .ToListAsync(ct);
        return new ReceiptSourceSummary(count, matched, openCount, latest);
    }

    /// <summary>The receipt of a booking (given by the booking or one of its parts), or null; only for the receipt's owner.</summary>
    public async Task<BookingReceiptView?> GetForBookingAsync(long transactionId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.Transactions.AsNoTracking().Where(t => t.Id == transactionId).Select(t => new { t.Id, t.SplitParentId, t.Splitted }).FirstOrDefaultAsync(ct);
        if (row == null) return null;
        var bookingId = row.Splitted == SplitState.Child && row.SplitParentId is { } p ? p : row.Id;
        var invoice = await db.ReceiptInvoices.AsNoTracking().Include(r => r.Items).FirstOrDefaultAsync(r => r.TransactionId == bookingId, ct);
        if (invoice == null) return null;
        var booking = await db.Transactions.AsNoTracking().FirstAsync(t => t.Id == bookingId, ct);

        var items = invoice.Items.OrderBy(i => i.Position).ToList();
        var lines = items.Select(i => new ReceiptLine(i.Id, i.Name, i.VatCode, i.TotalCents, i.NoDiscount, i.Kind)).ToList();
        var categories = items.ToDictionary(i => i.Id, i => new LineCategory(i.CategoryId ?? booking.CategoryId, i.CategorySource ?? ReceiptCategorySource.Default));
        var parts = ReceiptCategorizer.Parts(lines, categories, invoice.BonusCents, booking.SplitOriginalAmount ?? booking.Amount, booking.CategoryId);
        var groups = parts.Select(part => new ReceiptGroupView(part.CategoryId, part.AmountCents, part.LineIds
                .Select(id => items.First(i => i.Id == id))
                .Select(i => new ReceiptLineView(i.Id, i.Name, i.Quantity, i.TotalCents, i.Kind, i.CategoryId ?? booking.CategoryId, i.CategorySource)).ToList()))
            .ToList();
        return new BookingReceiptView(invoice.Id, bookingId, invoice.Shop, invoice.OrderNumber, invoice.DeliveryDate, invoice.BonusCents, groups, invoice.SplitSignature != null, invoice.Store);
    }

    /// <summary>
    /// Moves a receipt line to <paramref name="categoryId"/>: the line keeps it, the item memory remembers it for the
    /// product (deposit lines and fees are not remembered), and the booking is split by the receipt again.
    /// </summary>
    public async Task MoveItemAsync(long itemId, long categoryId, CancellationToken ct = default)
    {
        var user = await UserAsync();
        string name;
        long invoiceId;
        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
        {
            var item = await db.ReceiptItems.AsNoTracking().FirstOrDefaultAsync(i => i.Id == itemId && i.OwnerUserId == user, ct)
                ?? throw new AccessDeniedException("Receipt item not found.");
            if (!await db.Categories.AnyAsync(c => c.Id == categoryId && c.OwnerUserId == user && c.Name != null && c.Name != "", ct))
                throw new AccessDeniedException("Category not found.");
            name = item.Kind == ReceiptItemKind.Product ? ReceiptCategorizer.Normalize(item.Name) : "";
            invoiceId = item.ReceiptInvoiceId;
        }

        await using (var sys = await _system.CreateDbContextAsync(ct))
        {
            var invoice = await sys.ReceiptInvoices.Include(r => r.Items).FirstAsync(r => r.Id == invoiceId && r.OwnerUserId == user, ct);
            var line = invoice.Items.First(i => i.Id == itemId);
            line.CategoryId = categoryId;
            line.CategorySource = ReceiptCategorySource.Manual;
            if (name.Length > 0)
            {
                var memory = await sys.ItemCategoryMemory.FirstOrDefaultAsync(m => m.OwnerUserId == user && m.NormalizedName == name, ct);
                if (memory == null)
                {
                    memory = new ItemCategoryMemory { OwnerUserId = user, NormalizedName = name };
                    sys.ItemCategoryMemory.Add(memory);
                }
                memory.CategoryId = categoryId;
                memory.UpdatedAt = _clock.GetUtcNow().UtcDateTime;
            }
            await sys.SaveChangesAsync(ct);
            await _store.ApplyAsync(sys, invoice, force: true, ct);
        }
        Publish(user);
    }

    private void Publish(string user)
    {
        _notifier.Publish(new DataChange(DataArea.ImportedData, OwnerUserId: user), _origin.Id);
        _notifier.Publish(new DataChange(DataArea.CloudFiles, UserId: user), _origin.Id);
    }
}
