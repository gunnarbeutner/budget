using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using OpenBudget.Connectors.Receipts;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.Connectors.Amazon;

/// <summary>What <see cref="AmazonReceiptMigration.RunAsync"/> did.</summary>
public sealed record AmazonMigrationResult(int Charges, int Matched, int SplitsAdopted, int SplitsLeftAlone, int MemoryEntries);

/// <summary>
/// Moves Amazon orders from their former tables (amazon_orders, amazon_order_items, amazon_charges) into the shared
/// receipt tables, once, as a data fix after the schema migrations: one receipt per charge with the items that charge paid
/// for, its match (booking and state) kept. A booking split by an accepted order suggestion stays as it is: when the
/// receipt's lines (each in the category the owner gave it) give exactly its parts, the split counts as the receipt's
/// (it follows later moves of items); otherwise it counts as changed by hand and is left alone. The categories the owner
/// gave items become item memory of the projects those categories belong to. Every charge must arrive (and every
/// matched one with its booking) or nothing is changed; then the former tables are dropped. Without them it does nothing.
/// </summary>
public static class AmazonReceiptMigration
{
    private sealed record OldCharge(long Id, string Owner, int Login, string Key, string OrderId, DateTime Date, long Cents, string InstrumentId, string Instrument, int State, long? TransactionId);

    private sealed record OldOrder(long Id, string Owner, int Login, string OrderId, DateTime? Date, long? Total, DateTime? FetchedAt);

    private sealed record OldItem(long Id, long OrderId, string Title, int Quantity, long? Price, int Shipment, long? CategoryId);

    // Several app instances on one database (tests) start together: one moves the tables, the others then find none.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<AmazonMigrationResult?> RunAsync(BudgetDbContext db, DateTime nowUtc, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            return await MoveAsync(db, nowUtc, ct);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<AmazonMigrationResult?> MoveAsync(BudgetDbContext db, DateTime nowUtc, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open) await db.Database.OpenConnectionAsync(ct);
        if (await ScalarAsync(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'amazon_charges'", ct) == 0) return null;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var charges = await ReadAsync(db, "SELECT Id, OwnerUserId, BankLoginId, ChargeKey, OrderId, Date, AmountCents, InstrumentId, Instrument, State, TransactionId FROM amazon_charges ORDER BY Id",
            r => new OldCharge(r.GetInt64(0), r.GetString(1), r.GetInt32(2), r.GetString(3), r.GetString(4), ParseDate(r.GetString(5)), r.GetInt64(6), r.GetString(7), r.GetString(8), r.GetInt32(9),
                r.IsDBNull(10) ? null : r.GetInt64(10)), ct);
        var orders = await ReadAsync(db, "SELECT Id, OwnerUserId, BankLoginId, OrderId, OrderDate, TotalCents, DetailsFetchedAt FROM amazon_orders",
            r => new OldOrder(r.GetInt64(0), r.GetString(1), r.GetInt32(2), r.GetString(3), r.IsDBNull(4) ? null : ParseDate(r.GetString(4)), r.IsDBNull(5) ? null : r.GetInt64(5),
                r.IsDBNull(6) ? null : ParseDate(r.GetString(6))), ct);
        var items = await ReadAsync(db, "SELECT Id, AmazonOrderId, Title, Quantity, PriceCents, Shipment, CategoryId FROM amazon_order_items ORDER BY Id",
            r => new OldItem(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetInt32(3), r.IsDBNull(4) ? null : r.GetInt64(4), r.GetInt32(5), r.IsDBNull(6) ? null : r.GetInt64(6)), ct);

        // ---- receipts ----
        var existing = (await db.ReceiptInvoices.IgnoreQueryFilters().Where(r => r.Shop == ReceiptShops.Amazon && r.SourceKey != null)
            .Select(r => new { r.BankLoginId, r.SourceKey }).ToListAsync(ct)).Select(r => (r.BankLoginId, r.SourceKey)).ToHashSet();
        var created = new List<(ReceiptInvoice Receipt, OldOrder? Order)>();
        foreach (var group in charges.GroupBy(c => (c.Login, c.OrderId)))
        {
            var order = orders.Find(o => o.Login == group.Key.Login && o.OrderId == group.Key.OrderId);
            var receipts = new List<ReceiptInvoice>();
            foreach (var c in group)
            {
                if (existing.Contains((c.Login, c.Key))) continue;
                var r = AmazonReceiptStore.NewReceipt(c.Owner, c.Login, c.Key, c.OrderId, DateOnly.FromDateTime(c.Date), c.Cents, c.InstrumentId, c.Instrument, nowUtc);
                r.State = c.State switch { 1 => ReceiptMatchState.Matched, 2 => ReceiptMatchState.Ambiguous, 3 => ReceiptMatchState.Skipped, _ => ReceiptMatchState.Unmatched };
                r.TransactionId = c.TransactionId;
                receipts.Add(r);
            }
            if (receipts.Count == 0) continue;
            var ofOrder = order == null ? [] : items.Where(i => i.OrderId == order.Id).ToList();
            if (order != null && (order.FetchedAt != null || ofOrder.Count > 0))
                AmazonReceiptStore.Assign(receipts,
                    ofOrder.Select((i, n) => new AmazonItem(n, i.Title, i.Quantity, i.Price, i.Shipment)).ToList(),
                    order.Date is { } d ? DateOnly.FromDateTime(d) : null, order.Total, order.FetchedAt ?? nowUtc);
            db.ReceiptInvoices.AddRange(receipts);
            created.AddRange(receipts.Select(r => (r, order)));
        }
        await db.SaveChangesAsync(ct);

        // ---- splits the owner accepted from order suggestions ----
        var adopted = 0;
        var leftAlone = 0;
        foreach (var (receipt, order) in created.Where(c => c.Receipt.TransactionId != null))
        {
            var parent = await db.Transactions.IgnoreQueryFilters().FirstOrDefaultAsync(t => t.Id == receipt.TransactionId && t.OwnerUserId == receipt.OwnerUserId, ct);
            if (parent?.Splitted != SplitState.Parent || receipt.Items.Count == 0) continue;
            var children = await db.Transactions.IgnoreQueryFilters()
                .Where(t => t.SplitParentId == parent.Id && t.Splitted == SplitState.Child && t.OwnerUserId == parent.OwnerUserId).ToListAsync(ct);
            var given = order == null ? [] : items.Where(i => i.OrderId == order.Id).Select((i, n) => (Position: n, i.CategoryId)).Where(x => x.CategoryId != null)
                .ToDictionary(x => x.Position, x => x.CategoryId!.Value);
            var lines = receipt.Items.OrderBy(i => i.Position).Select(i => new ReceiptLine(i.Id, i.Name, i.VatCode, i.TotalCents, i.NoDiscount, i.Kind)).ToList();
            var home = parent.CategoryId;
            var categories = receipt.Items.ToDictionary(i => i.Id, i => given.TryGetValue(i.Position, out var c)
                ? new LineCategory(c, ReceiptCategorySource.Manual) : new LineCategory(home, ReceiptCategorySource.Default));
            var parts = ReceiptCategorizer.Parts(lines, categories, receipt.BonusCents, parent.SplitOriginalAmount ?? parent.Amount, home);
            var signature = ReceiptCategorizer.Signature(parts.Select(p => (p.AmountCents, p.CategoryId)));
            if (parts.Count < 2 || signature != ReceiptCategorizer.Signature(children.Select(c => (c.Amount, c.CategoryId))))
            {
                leftAlone++;
                continue;
            }
            adopted++;
            receipt.SplitSignature = signature;
            // The split stays as the owner made it: items they categorized are theirs, the rest keep the booking's category.
            foreach (var item in receipt.Items)
            {
                if (given.TryGetValue(item.Position, out var own))
                    (item.CategoryId, item.CategorySource) = (own, ReceiptCategorySource.Manual);
                else if (item.Kind == ReceiptItemKind.Product)
                    (item.CategoryId, item.CategorySource) = (home, home == null ? null : ReceiptCategorySource.Default);
            }
            var free = children.ToList();
            foreach (var part in parts)
            {
                var child = free.First(c => c.Amount == part.AmountCents && c.CategoryId == part.CategoryId);
                free.Remove(child);
                child.OrderItems = ReceiptCategorizer.Describe(part.LineIds.Select(id => receipt.Items.First(i => i.Id == id)).Select(i => (i.Name, i.Quantity)));
            }
        }
        await db.SaveChangesAsync(ct);

        // ---- item memory from the categories the owner gave items ----
        var memoryAdded = 0;
        foreach (var owner in items.Where(i => i.CategoryId != null).Select(i => orders.First(o => o.Id == i.OrderId).Owner).Distinct(StringComparer.Ordinal))
        {
            var projectIds = await db.Projects.IgnoreQueryFilters().Where(p => p.OwnerUserId == owner).Select(p => p.Id).ToListAsync(ct);
            var members = await ReceiptProjects.CategoriesAsync(db, owner, projectIds, ct);
            var known = (await db.ItemCategoryMemory.IgnoreQueryFilters().Where(m => m.OwnerUserId == owner).Select(m => new { m.ProjectId, m.NormalizedName }).ToListAsync(ct))
                .Select(m => (m.ProjectId, m.NormalizedName)).ToHashSet();
            var ownerOrders = orders.Where(o => o.Owner == owner).Select(o => o.Id).ToHashSet();
            foreach (var item in items.Where(i => i.CategoryId != null && ownerOrders.Contains(i.OrderId)).OrderByDescending(i => i.Id))
            {
                var name = ReceiptCategorizer.Normalize(item.Title);
                if (name.Length == 0) continue;
                foreach (var (project, _) in members.Where(m => m.Value.Contains(item.CategoryId!.Value)))
                {
                    if (!known.Add((project, name))) continue;
                    db.ItemCategoryMemory.Add(new ItemCategoryMemory { OwnerUserId = owner, ProjectId = project, NormalizedName = name, CategoryId = item.CategoryId!.Value, UpdatedAt = nowUtc });
                    memoryAdded++;
                }
            }
        }
        await db.SaveChangesAsync(ct);

        // ---- check, then drop the former tables ----
        var keys = charges.Select(c => (c.Login, c.Key)).ToHashSet();
        var stored = (await db.ReceiptInvoices.IgnoreQueryFilters().AsNoTracking().Where(r => r.Shop == ReceiptShops.Amazon && r.SourceKey != null)
            .Select(r => new { r.BankLoginId, r.SourceKey, r.TransactionId }).ToListAsync(ct))
            .Where(r => keys.Contains((r.BankLoginId ?? 0, r.SourceKey!))).ToList();
        var matchedBefore = charges.Count(c => c.TransactionId != null);
        if (stored.Count != charges.Count || stored.Count(r => r.TransactionId != null) != matchedBefore)
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                $"Moving Amazon charges to receipts failed: {charges.Count} charges ({matchedBefore} matched), {stored.Count} receipts ({stored.Count(r => r.TransactionId != null)} matched)."));
        await db.Database.ExecuteSqlRawAsync("DROP TABLE amazon_order_items", ct);
        await db.Database.ExecuteSqlRawAsync("DROP TABLE amazon_orders", ct);
        await db.Database.ExecuteSqlRawAsync("DROP TABLE amazon_charges", ct);
        await tx.CommitAsync(ct);
        return new AmazonMigrationResult(charges.Count, matchedBefore, adopted, leftAlone, memoryAdded);
    }

    private static DateTime ParseDate(string text) =>
        DateTime.SpecifyKind(DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal), DateTimeKind.Utc);

    // The queries below are constants of this class.
#pragma warning disable CA2100
    private static async Task<long> ScalarAsync(DbConnection connection, string sql, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    private static async Task<List<T>> ReadAsync<T>(BudgetDbContext db, string sql, Func<DbDataReader, T> map, CancellationToken ct)
    {
        await using var cmd = db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = Microsoft.EntityFrameworkCore.Storage.DbContextTransactionExtensions.GetDbTransaction(db.Database.CurrentTransaction!);
        var result = new List<T>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(map(reader));
        return result;
    }
#pragma warning restore CA2100
}
