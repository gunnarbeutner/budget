using System.Globalization;
using Microsoft.EntityFrameworkCore;
using OpenBudget.Connectors.Sync;
using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>One part of a split as the user enters it (amount in cents, signed like the booking).</summary>
public sealed record SplitPartInput(long AmountCents, long? CategoryId, string? Note);

/// <summary>A stored split part.</summary>
public sealed record SplitPartView(long Id, long AmountCents, long? CategoryId, string? CategoryName, string? Note);

/// <summary>The split of one booking: its total and its parts (empty when it is not split).</summary>
public sealed record SplitInfo(long ParentId, int SourceId, long TotalCents, string? Currency, IReadOnlyList<SplitPartView> Parts);

/// <summary>Why a split cannot be saved.</summary>
public enum SplitError
{
    None,
    /// <summary>A split needs at least two parts.</summary>
    TooFewParts,
    /// <summary>A part without an amount.</summary>
    ZeroPart,
    /// <summary>The parts don't add up to the booking.</summary>
    SumMismatch,
}

/// <summary>A booking that could be the other side of a transfer.</summary>
public sealed record TransferCandidate(long Id, long AccountId, string AccountName, DateTime Date, long AmountCents, string? Name);

/// <summary>Transfer state of a booking. <see cref="PeerId"/> is null for a one-sided transfer.</summary>
public sealed record TransferInfo(bool IsTransfer, long? PeerId, string? PeerAccountName, DateTime? PeerDate);

/// <summary>Raised for a split / transfer request that is not possible for this booking.</summary>
public sealed class SplitTransferException : InvalidOperationException
{
    public SplitTransferException() : this(SplitTransferProblem.Invalid) { }
    public SplitTransferException(string message) : base(message) => Problem = SplitTransferProblem.Invalid;
    public SplitTransferException(string message, Exception innerException) : base(message, innerException) => Problem = SplitTransferProblem.Invalid;
    public SplitTransferException(SplitTransferProblem problem) : base(problem.ToString()) => Problem = problem;

    public SplitTransferProblem Problem { get; }
}

public enum SplitTransferProblem
{
    NotFound,
    Pending,
    IsTransfer,
    IsSplit,
    Invalid,
    NotACandidate,
}

/// <summary>
/// Splits bookings into parts and links transfers between the household's own accounts.
///
/// A split stores its parts as child rows (<see cref="SplitState.Child"/>, <see cref="Transaction.SplitParentId"/>) and
/// marks the booking itself as a split parent, exactly the shape MoneyMoney imports have, so every list and total that
/// already skips split parents counts the parts instead. Part ids come from the connector row range of the source.
///
/// A transfer sets <see cref="Transaction.IsTransfer"/> on both bookings (and points them at each other) or on one booking
/// whose counterpart account is not tracked. Transfers drop out of income/spending totals but stay in balances.
///
/// Everything goes through the user-scoped context: only rows the user may edit are touched (see
/// <see cref="TransactionEditRules"/>), so MoneyMoney-owned accounts can neither be split nor be a transfer counterpart
/// (their next upload would replace the rows and lose the link). Pending bookings are rebuilt on every sync, so they are
/// excluded too.
/// </summary>
public sealed class SplitTransferService
{
    /// <summary>How far apart (days) the two bookings of a transfer may be.</summary>
    public const int TransferWindowDays = 5;

    private readonly IDbContextFactory<BudgetDbContext> _dbFactory;
    private readonly ChangeNotificationService _notifier;
    private readonly ChangeOrigin _origin;
    private readonly TimeProvider _time;

    public SplitTransferService(
        IDbContextFactory<BudgetDbContext> dbFactory, ChangeNotificationService notifier, ChangeOrigin origin, TimeProvider? time = null)
    {
        _dbFactory = dbFactory;
        _notifier = notifier;
        _origin = origin;
        _time = time ?? TimeProvider.System;
    }

    // ---- pure logic ----

    /// <summary>What is left to assign: the booking minus the parts so far (cents).</summary>
    public static long Remaining(long totalCents, IEnumerable<long> partCents) => totalCents - partCents.Sum();

    /// <summary>
    /// Parses an amount the user typed ("12,34", "12.34", "1.234,50", "-5") into cents, or null when it isn't a number.
    /// The last separator is the decimal one when one or two digits follow it.
    /// </summary>
    public static long? ParseCents(string? text)
    {
        var s = (text ?? "").Trim().Replace(" ", "", StringComparison.Ordinal).Replace("€", "", StringComparison.Ordinal);
        if (s.Length == 0) return null;
        var negative = s.StartsWith('-', StringComparison.Ordinal);
        if (negative || s.StartsWith('+', StringComparison.Ordinal)) s = s[1..];
        var last = s.LastIndexOfAny(['.', ',']);
        string whole = s, frac = "";
        if (last >= 0 && s.Length - last - 1 is 1 or 2)
        {
            whole = s[..last];
            frac = s[(last + 1)..];
        }
        whole = whole.Replace(".", "", StringComparison.Ordinal).Replace(",", "", StringComparison.Ordinal);
        if (whole.Length == 0) whole = "0";
        if (!whole.All(char.IsAsciiDigit) || !frac.All(char.IsAsciiDigit) || whole.Length > 13) return null;
        var cents = (long.Parse(whole, CultureInfo.InvariantCulture) * 100)
            + (frac.Length == 0 ? 0 : long.Parse(frac.PadRight(2, '0'), CultureInfo.InvariantCulture));
        return negative ? -cents : cents;
    }

    public static SplitError Validate(long totalCents, IReadOnlyList<SplitPartInput> parts)
    {
        if (parts.Count < 2) return SplitError.TooFewParts;
        if (parts.Any(p => p.AmountCents == 0)) return SplitError.ZeroPart;
        if (Remaining(totalCents, parts.Select(p => p.AmountCents)) != 0) return SplitError.SumMismatch;
        return SplitError.None;
    }

    /// <summary>Whether <paramref name="candidate"/> can be the other side of a transfer of <paramref name="row"/>.</summary>
    public static bool IsTransferMatch(Transaction row, Transaction candidate) =>
        candidate.Id != row.Id
        && candidate.AccountId != null && candidate.AccountId != row.AccountId
        && candidate.SourceId == row.SourceId
        && row.Amount != 0 && candidate.Amount == -row.Amount
        && Math.Abs(candidate.Timestamp - row.Timestamp) <= TransferWindowDays * 86400L
        && candidate.Booked
        && !candidate.IsTransfer
        && (candidate.Splitted ?? SplitState.Normal) is SplitState.Normal;

    // ---- splits ----

    /// <summary>The split of the booking (given by the booking's or one part's id); null when the user can't see it.</summary>
    public async Task<SplitInfo?> GetSplitAsync(long id, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.Transactions.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);
        if (row == null) return null;
        var parent = row.Splitted == SplitState.Child && row.SplitParentId is { } pid
            ? await db.Transactions.AsNoTracking().FirstOrDefaultAsync(t => t.Id == pid, ct)
            : row;
        if (parent == null) return null;
        var parts = parent.Splitted == SplitState.Parent
            ? await (from t in db.Transactions.AsNoTracking()
                     where t.SplitParentId == parent.Id && t.Splitted == SplitState.Child
                     orderby t.Id
                     select new SplitPartView(t.Id, t.Amount, t.CategoryId, t.Category != null ? t.Category.Name : null, t.Note))
                .ToListAsync(ct)
            : [];
        return new SplitInfo(parent.Id, parent.SourceId, parent.SplitOriginalAmount ?? parent.Amount, parent.Currency, parts);
    }

    /// <summary>
    /// Splits the booking (or replaces the parts of an existing split) into <paramref name="parts"/>. <paramref name="id"/>
    /// may be the booking or one of its parts.
    /// </summary>
    public async Task SplitAsync(long id, IReadOnlyList<SplitPartInput> parts, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var parent = await LoadParentAsync(db, id, ct);
        await RequireEditableAsync(db, [parent], ct);
        if (!parent.Booked) throw new SplitTransferException(SplitTransferProblem.Pending);
        if (parent.IsTransfer) throw new SplitTransferException(SplitTransferProblem.IsTransfer);
        if (parent.Splitted is SplitState.Remainder) throw new SplitTransferException(SplitTransferProblem.Invalid);
        var total = parent.SplitOriginalAmount ?? parent.Amount;
        if (Validate(total, parts) != SplitError.None) throw new SplitTransferException(SplitTransferProblem.Invalid);

        var categoryIds = parts.Where(p => p.CategoryId != null).Select(p => p.CategoryId!.Value).Distinct().ToList();
        if (categoryIds.Count > 0)
        {
            var valid = await db.Categories.AsNoTracking()
                .Where(c => categoryIds.Contains(c.Id) && c.SourceId == parent.SourceId && c.Name != null && c.Name != "")
                .CountAsync(ct);
            if (valid != categoryIds.Count) throw new InvalidOperationException("Unknown category.");
        }

        await BankTransactionStore.Gate.WaitAsync(ct);
        try
        {
            var old = await db.Transactions.Where(t => t.SplitParentId == parent.Id && t.Splitted == SplitState.Child).ToListAsync(ct);
            db.Transactions.RemoveRange(old);

            // Ids are allocated over the whole source (not just what this user sees), in the connector range.
            var rangeStart = SourceScope.Namespace(parent.SourceId, BankTransactionStore.ConnectorRowBase);
            var nextId = await db.Transactions.IgnoreQueryFilters()
                .Where(t => t.SourceId == parent.SourceId && t.Id >= rangeStart)
                .MaxAsync(t => (long?)t.Id, ct) ?? rangeStart;

            var now = _time.GetUtcNow().ToUnixTimeSeconds();
            foreach (var p in parts)
            {
                db.Transactions.Add(new Transaction
                {
                    Id = ++nextId,
                    SourceId = parent.SourceId,
                    AccountId = parent.AccountId,
                    Amount = p.AmountCents,
                    Currency = parent.Currency,
                    Timestamp = parent.Timestamp,
                    ValueTimestamp = parent.ValueTimestamp,
                    Name = parent.Name,
                    Purpose = parent.Purpose,
                    BookingText = parent.BookingText,
                    Booked = parent.Booked,
                    Checked = parent.Checked,
                    Checkmark = 0,
                    Splitted = SplitState.Child,
                    SplitParentId = parent.Id,
                    CategoryId = p.CategoryId,
                    CategorySetBy = TransactionCategorySource.Manual,
                    CategorySetAt = p.CategoryId != null ? now : null,
                    Note = string.IsNullOrWhiteSpace(p.Note) ? null : p.Note.Trim(),
                });
            }
            if (parent.Splitted != SplitState.Parent)
            {
                parent.Splitted = SplitState.Parent;
                parent.SplitOriginalAmount = parent.Amount;
            }
            parent.IsNew = false;
            await db.SaveChangesAsync(ct);
        }
        finally
        {
            BankTransactionStore.Gate.Release();
        }
        Publish(parent.SourceId);
    }

    /// <summary>Removes the split: the parts go away and the booking counts as one again (keeping its own category).</summary>
    public async Task RemoveSplitAsync(long id, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var parent = await LoadParentAsync(db, id, ct);
        await RequireEditableAsync(db, [parent], ct);
        if (parent.Splitted != SplitState.Parent) return;
        var parts = await db.Transactions.Where(t => t.SplitParentId == parent.Id && t.Splitted == SplitState.Child).ToListAsync(ct);
        db.Transactions.RemoveRange(parts);
        parent.Splitted = SplitState.Normal;
        parent.SplitOriginalAmount = null;
        await db.SaveChangesAsync(ct);
        Publish(parent.SourceId);
    }

    // ---- transfers ----

    public async Task<TransferInfo?> GetTransferAsync(long id, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.Transactions.AsNoTracking().Where(t => t.Id == id)
            .Select(t => new { t.IsTransfer, t.TransferPeerId }).FirstOrDefaultAsync(ct);
        if (row == null) return null;
        if (!row.IsTransfer || row.TransferPeerId is not { } peerId) return new TransferInfo(row.IsTransfer, null, null, null);
        var peer = await db.Transactions.AsNoTracking().Where(t => t.Id == peerId)
            .Select(t => new { t.Timestamp, AccountName = t.Account != null ? t.Account.Name : null }).FirstOrDefaultAsync(ct);
        return peer == null
            ? new TransferInfo(true, null, null, null)
            : new TransferInfo(true, peerId, peer.AccountName, DateTimeOffset.FromUnixTimeSeconds(peer.Timestamp).UtcDateTime);
    }

    /// <summary>
    /// Bookings that could be the other side of a transfer: opposite amount, within ±<see cref="TransferWindowDays"/> days,
    /// on another account of the project that the user may edit (not MoneyMoney's), not already a transfer or split.
    /// Closest date first.
    /// </summary>
    public async Task<List<TransferCandidate>> GetTransferCandidatesAsync(long id, int projectId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.Transactions.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);
        if (row == null || row.AccountId == null) return [];

        var accountIds = await db.ProjectAccountAssignments.AsNoTracking()
            .Where(a => a.ProjectId == projectId && a.AccountId != row.AccountId)
            .Select(a => a.AccountId).ToListAsync(ct);
        var accounts = await db.Accounts.AsNoTracking()
            .Where(a => accountIds.Contains(a.Id) && a.SourceId == row.SourceId)
            .Select(a => new { a.Id, a.Name, a.Provider, a.SourceId }).ToListAsync(ct);
        var usable = accounts
            .Where(a => TransactionEditRules.Check(db.Scope, a.SourceId, a.Id, a.Provider) == EditBlock.None)
            .ToDictionary(a => a.Id, a => a.Name ?? "");
        if (usable.Count == 0) return [];

        var ids = usable.Keys.ToList();
        var from = row.Timestamp - TransferWindowDays * 86400L;
        var to = row.Timestamp + TransferWindowDays * 86400L;
        var target = -row.Amount;
        var rows = await db.Transactions.AsNoTracking()
            .Where(t => t.AccountId.HasValue && ids.Contains(t.AccountId.Value) && t.Amount == target && t.Timestamp >= from && t.Timestamp <= to)
            .ToListAsync(ct);
        return rows
            .Where(c => IsTransferMatch(row, c))
            .OrderBy(c => Math.Abs(c.Timestamp - row.Timestamp)).ThenBy(c => c.Id)
            .Select(c => new TransferCandidate(c.Id, c.AccountId!.Value, usable[c.AccountId!.Value],
                DateTimeOffset.FromUnixTimeSeconds(c.Timestamp).UtcDateTime, c.Amount, c.Name))
            .ToList();
    }

    /// <summary>Links two bookings as a transfer between own accounts.</summary>
    public async Task LinkTransferAsync(long id, long peerId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.Transactions.FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw new SplitTransferException(SplitTransferProblem.NotFound);
        var peer = await db.Transactions.FirstOrDefaultAsync(t => t.Id == peerId, ct) ?? throw new SplitTransferException(SplitTransferProblem.NotFound);
        await RequireEditableAsync(db, [row, peer], ct);
        RequireTransferable(row);
        if (row.IsTransfer || !IsTransferMatch(row, peer)) throw new SplitTransferException(SplitTransferProblem.NotACandidate);
        row.IsTransfer = peer.IsTransfer = true;
        row.TransferPeerId = peer.Id;
        peer.TransferPeerId = row.Id;
        row.IsNew = peer.IsNew = false;
        await db.SaveChangesAsync(ct);
        Publish(row.SourceId);
    }

    /// <summary>Marks a booking as a transfer to/from an account that is not tracked here.</summary>
    public async Task MarkOneSidedTransferAsync(long id, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.Transactions.FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw new SplitTransferException(SplitTransferProblem.NotFound);
        await RequireEditableAsync(db, [row], ct);
        RequireTransferable(row);
        if (row.IsTransfer) return;
        row.IsTransfer = true;
        row.TransferPeerId = null;
        row.IsNew = false;
        await db.SaveChangesAsync(ct);
        Publish(row.SourceId);
    }

    /// <summary>Ends a transfer: the booking (and its linked counterpart, if any) counts as income/spending again.</summary>
    public async Task UnlinkTransferAsync(long id, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.Transactions.FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw new SplitTransferException(SplitTransferProblem.NotFound);
        var rows = new List<Transaction> { row };
        if (row.TransferPeerId is { } peerId
            && await db.Transactions.FirstOrDefaultAsync(t => t.Id == peerId && t.TransferPeerId == row.Id, ct) is { } peer)
            rows.Add(peer);
        await RequireEditableAsync(db, rows, ct);
        if (!row.IsTransfer && row.TransferPeerId == null) return;
        foreach (var t in rows)
        {
            t.IsTransfer = false;
            t.TransferPeerId = null;
        }
        await db.SaveChangesAsync(ct);
        Publish(row.SourceId);
    }

    // ---- helpers ----

    private static void RequireTransferable(Transaction row)
    {
        if (!row.Booked) throw new SplitTransferException(SplitTransferProblem.Pending);
        if ((row.Splitted ?? SplitState.Normal) != SplitState.Normal) throw new SplitTransferException(SplitTransferProblem.IsSplit);
    }

    private static async Task<Transaction> LoadParentAsync(BudgetDbContext db, long id, CancellationToken ct)
    {
        var row = await db.Transactions.FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw new SplitTransferException(SplitTransferProblem.NotFound);
        if (row.Splitted == SplitState.Child)
        {
            if (row.SplitParentId is not { } pid) throw new SplitTransferException(SplitTransferProblem.Invalid);
            row = await db.Transactions.FirstOrDefaultAsync(t => t.Id == pid, ct) ?? throw new SplitTransferException(SplitTransferProblem.NotFound);
        }
        return row;
    }

    /// <summary>Throws unless the user may edit every one of <paramref name="rows"/> (provider rule + role).</summary>
    private static async Task RequireEditableAsync(BudgetDbContext db, IReadOnlyList<Transaction> rows, CancellationToken ct)
    {
        var accountIds = rows.Where(r => r.AccountId.HasValue).Select(r => r.AccountId!.Value).Distinct().ToList();
        var providers = await db.Accounts.AsNoTracking().Where(a => accountIds.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, a => a.Provider, ct);
        foreach (var r in rows)
        {
            var block = TransactionEditRules.Check(db.Scope, r.SourceId, r.AccountId,
                r.AccountId is { } aid && providers.TryGetValue(aid, out var p) ? p : null);
            if (block != EditBlock.None)
                throw new AccessDeniedException(block == EditBlock.MoneyMoney
                    ? "Transactions of accounts still owned by MoneyMoney are read-only."
                    : "Not allowed to edit these transactions.");
        }
    }

    private void Publish(int sourceId) => _notifier.Publish(new DataChange(DataArea.ImportedData, SourceId: sourceId), _origin.Id);
}
