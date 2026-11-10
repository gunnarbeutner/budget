using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>One transaction's user-owned fields as they were before an edit; restoring them undoes it.</summary>
public sealed record TransactionEditSnapshot(
    long Id, long? CategoryId, string CategorySetBy, int? RuleId, long? CategorySetAt, string? Note, bool Checked, bool IsNew);

/// <summary>Everything needed to undo one edit (a bulk edit is one receipt = one undo step).</summary>
public sealed record EditReceipt(IReadOnlyList<TransactionEditSnapshot> Before, int SourceId);

/// <summary>
/// Outcome of an edit. <see cref="Changed"/> rows were written, <see cref="Skipped"/> ones were left alone
/// (read-only, not visible, or already as requested). <see cref="Receipt"/> is null when nothing changed.
/// </summary>
public sealed record EditResult(EditReceipt? Receipt, int Changed, int Skipped);

/// <summary>
/// Edits the user-owned fields of transactions (category, note, check mark) and undoes them. Every write goes through
/// the user-scoped context, so visibility and the SaveChanges write guard decide who may do what (see
/// <see cref="TransactionEditRules"/>); this service additionally skips rows the user may not edit and refuses a call
/// that could not edit anything. A manual category is stamped <c>manual</c> so rules never overwrite it.
/// </summary>
public sealed class TransactionEditService
{
    private const int Chunk = 500;

    private readonly IDbContextFactory<BudgetDbContext> _dbFactory;
    private readonly ChangeNotificationService _notifier;
    private readonly ChangeOrigin _origin;
    private readonly TimeProvider _time;

    public TransactionEditService(
        IDbContextFactory<BudgetDbContext> dbFactory, ChangeNotificationService notifier, ChangeOrigin origin, TimeProvider? time = null)
    {
        _dbFactory = dbFactory;
        _notifier = notifier;
        _origin = origin;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Sets (or with null removes) the category of the given transactions; the result is marked as chosen by hand.</summary>
    public Task<EditResult> SetCategoryAsync(IReadOnlyCollection<long> ids, long? categoryId, CancellationToken ct = default) =>
        EditAsync(ids, validate: async (db, rows) =>
        {
            if (categoryId is not { } id) return;
            var category = await db.Categories.AsNoTracking().Where(c => c.Id == id).Select(c => new { c.SourceId, c.Name }).FirstOrDefaultAsync(ct)
                ?? throw new InvalidOperationException("Unknown category.");
            if (string.IsNullOrEmpty(category.Name)) throw new InvalidOperationException("That category cannot be assigned.");
            if (rows.Any(r => r.SourceId != category.SourceId)) throw new InvalidOperationException("The category belongs to another source.");
        }, apply: t =>
        {
            if (t.CategoryId == categoryId && t.CategorySetBy == TransactionCategorySource.Manual) return false;
            t.CategoryId = categoryId;
            t.CategorySetBy = TransactionCategorySource.Manual;
            t.RuleId = null;
            t.CategorySetAt = _time.GetUtcNow().ToUnixTimeSeconds();
            t.IsNew = false;
            return true;
        }, ct);

    /// <summary>Sets the note (empty = remove it).</summary>
    public Task<EditResult> SetNoteAsync(IReadOnlyCollection<long> ids, string? note, CancellationToken ct = default)
    {
        var text = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        return EditAsync(ids, validate: null, apply: t =>
        {
            if (string.Equals(t.Note, text, StringComparison.Ordinal)) return false;
            t.Note = text;
            t.IsNew = false;
            return true;
        }, ct);
    }

    /// <summary>Sets the check mark on all given transactions.</summary>
    public Task<EditResult> SetCheckedAsync(IReadOnlyCollection<long> ids, bool value, CancellationToken ct = default) =>
        EditAsync(ids, validate: null, apply: t =>
        {
            if (t.Checked == value && !t.IsNew) return false;
            t.Checked = value;
            t.IsNew = false;
            return true;
        }, ct);

    /// <summary>
    /// "Toggle check mark" on a selection: if any selected transaction is unchecked they all become checked, otherwise they
    /// all become unchecked.
    /// </summary>
    public async Task<EditResult> ToggleCheckedAsync(IReadOnlyCollection<long> ids, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var anyUnchecked = false;
        foreach (var chunk in ids.Chunk(Chunk))
        {
            var part = chunk;
            if (await db.Transactions.AsNoTracking().AnyAsync(t => part.Contains(t.Id) && !t.Checked, ct)) { anyUnchecked = true; break; }
        }
        return await SetCheckedAsync(ids, anyUnchecked, ct);
    }

    /// <summary>Clears the "new" marker of rows the user has looked at. Not an undoable edit; silently skips what is not editable.</summary>
    public async Task MarkViewedAsync(IReadOnlyCollection<long> ids, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var sources = new HashSet<int>();
        foreach (var chunk in ids.Chunk(Chunk))
        {
            var part = chunk;
            var rows = await db.Transactions.Where(t => part.Contains(t.Id) && t.IsNew).ToListAsync(ct);
            var editable = await EditableAsync(db, rows, ct);
            foreach (var t in editable) { t.IsNew = false; sources.Add(t.SourceId); }
        }
        if (db.ChangeTracker.HasChanges())
        {
            await db.SaveChangesAsync(ct);
            foreach (var s in sources) Publish(s);
        }
    }

    /// <summary>
    /// "Mark all as checked" for a filter: every editable transaction the query matches (any paging is ignored) gets
    /// the check mark. One receipt, so one undo.
    /// </summary>
    public async Task<EditResult> MarkAllCheckedAsync(TransactionQuery query, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var ids = await TransactionQueryService.BuildFilteredQuery(db, query)
            .Where(t => !t.Checked || t.IsNew)
            .Select(t => t.Id)
            .ToListAsync(ct);
        if (ids.Count == 0) return new EditResult(null, 0, 0);
        return await SetCheckedAsync(ids, true, ct);
    }

    /// <summary>Restores the fields a receipt recorded. The same permission checks apply as for the original edit.</summary>
    public async Task<int> UndoAsync(EditReceipt receipt, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var byId = receipt.Before.ToDictionary(s => s.Id);
        var rows = new List<Transaction>();
        foreach (var chunk in byId.Keys.Chunk(Chunk))
        {
            var part = chunk;
            rows.AddRange(await db.Transactions.Where(t => part.Contains(t.Id)).ToListAsync(ct));
        }
        var editable = await EditableAsync(db, rows, ct);
        foreach (var t in editable)
        {
            var s = byId[t.Id];
            t.CategoryId = s.CategoryId;
            t.CategorySetBy = s.CategorySetBy;
            t.RuleId = s.RuleId;
            t.CategorySetAt = s.CategorySetAt;
            t.Note = s.Note;
            t.Checked = s.Checked;
            t.IsNew = s.IsNew;
        }
        if (editable.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            Publish(receipt.SourceId);
        }
        return editable.Count;
    }

    private async Task<EditResult> EditAsync(
        IReadOnlyCollection<long> ids,
        Func<BudgetDbContext, List<Transaction>, Task>? validate,
        Func<Transaction, bool> apply,
        CancellationToken ct)
    {
        if (ids.Count == 0) return new EditResult(null, 0, 0);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var rows = new List<Transaction>();
        foreach (var chunk in ids.Distinct().Chunk(Chunk))
        {
            var part = chunk;
            rows.AddRange(await db.Transactions.Where(t => part.Contains(t.Id)).ToListAsync(ct));
        }
        var distinct = ids.Distinct().Count();

        var editable = await EditableAsync(db, rows, ct);
        if (editable.Count == 0)
            throw new AccessDeniedException("None of these transactions can be edited here.");
        if (validate != null) await validate(db, editable);

        var before = new List<TransactionEditSnapshot>();
        foreach (var t in editable)
        {
            var snapshot = new TransactionEditSnapshot(t.Id, t.CategoryId, t.CategorySetBy, t.RuleId, t.CategorySetAt, t.Note, t.Checked, t.IsNew);
            if (apply(t)) before.Add(snapshot);
        }
        if (before.Count == 0) return new EditResult(null, 0, distinct);

        await db.SaveChangesAsync(ct);
        var sourceId = editable[0].SourceId;
        foreach (var s in editable.Select(t => t.SourceId).Distinct()) Publish(s);
        return new EditResult(new EditReceipt(before, sourceId), before.Count, distinct - before.Count);
    }

    /// <summary>The subset of <paramref name="rows"/> the current user may edit (provider rule + role).</summary>
    private static async Task<List<Transaction>> EditableAsync(BudgetDbContext db, List<Transaction> rows, CancellationToken ct)
    {
        var accountIds = rows.Where(r => r.AccountId.HasValue).Select(r => r.AccountId!.Value).Distinct().ToList();
        var providers = new Dictionary<long, string>();
        foreach (var chunk in accountIds.Chunk(Chunk))
        {
            var part = chunk;
            foreach (var a in await db.Accounts.AsNoTracking().Where(a => part.Contains(a.Id)).Select(a => new { a.Id, a.Provider }).ToListAsync(ct))
                providers[a.Id] = a.Provider;
        }
        return rows
            .Where(r => TransactionEditRules.Check(db.Scope, r.SourceId, r.AccountId,
                r.AccountId is { } id && providers.TryGetValue(id, out var p) ? p : null) == EditBlock.None)
            .ToList();
    }

    private void Publish(int sourceId) => _notifier.Publish(new DataChange(DataArea.ImportedData, SourceId: sourceId), _origin.Id);
}
