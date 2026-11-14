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
public sealed record SplitInfo(long ParentId, string OwnerUserId, long TotalCents, string? Currency, IReadOnlyList<SplitPartView> Parts);

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

/// <summary>Raised for a split request that is not possible for this booking.</summary>
public sealed class SplitException : InvalidOperationException
{
    public SplitException() : this(SplitProblem.Invalid) { }
    public SplitException(string message) : base(message) => Problem = SplitProblem.Invalid;
    public SplitException(string message, Exception innerException) : base(message, innerException) => Problem = SplitProblem.Invalid;
    public SplitException(SplitProblem problem) : base(problem.ToString()) => Problem = problem;

    public SplitProblem Problem { get; }
}

public enum SplitProblem
{
    NotFound,
    IsSplit,
    Invalid,
}

/// <summary>
/// Splits bookings into parts.
///
/// A split stores its parts as child rows (<see cref="SplitState.Child"/>, <see cref="Transaction.SplitParentId"/>) and
/// marks the booking itself as a split parent, exactly the shape MoneyMoney imports have, so every list and total that
/// already skips split parents counts the parts instead. Part ids come from the connector row range of the source.
///
/// Everything goes through the user-scoped context: only rows the user may edit are touched (see
/// <see cref="TransactionEditRules"/>). Pending bookings can be split too: the sync moves the split to the booking that replaces
/// them (see <see cref="PendingCarryOver"/>).
/// </summary>
public sealed class SplitService
{
    private readonly IDbContextFactory<BudgetDbContext> _dbFactory;
    private readonly ChangeNotificationService _notifier;
    private readonly ChangeOrigin _origin;

    public SplitService(
        IDbContextFactory<BudgetDbContext> dbFactory, ChangeNotificationService notifier, ChangeOrigin origin)
    {
        _dbFactory = dbFactory;
        _notifier = notifier;
        _origin = origin;
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
        return new SplitInfo(parent.Id, parent.OwnerUserId, parent.SplitOriginalAmount ?? parent.Amount, parent.Currency, parts);
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
        if (parent.Splitted is SplitState.Remainder) throw new SplitException(SplitProblem.Invalid);
        var total = parent.SplitOriginalAmount ?? parent.Amount;
        if (Validate(total, parts) != SplitError.None) throw new SplitException(SplitProblem.Invalid);

        var categoryIds = parts.Where(p => p.CategoryId != null).Select(p => p.CategoryId!.Value).Distinct().ToList();
        if (categoryIds.Count > 0)
        {
            var valid = await db.Categories.AsNoTracking()
                .Where(c => categoryIds.Contains(c.Id) && c.OwnerUserId == parent.OwnerUserId && c.Name != null && c.Name != "")
                .CountAsync(ct);
            if (valid != categoryIds.Count) throw new InvalidOperationException("Unknown category.");
        }

        await BankTransactionStore.Gate.WaitAsync(ct);
        try
        {
            var old = await db.Transactions.Where(t => t.SplitParentId == parent.Id && t.Splitted == SplitState.Child).ToListAsync(ct);
            db.Transactions.RemoveRange(old);

            // Ids are allocated over all of the owner's rows (not just what this user sees), in the connector range.
            var rangeStart = RowIds.Namespace((await RowIds.PeekStateAsync(db, parent.OwnerUserId, ct)).IdSpace, BankTransactionStore.ConnectorRowBase);
            var nextId = await db.Transactions.IgnoreQueryFilters()
                .Where(t => t.OwnerUserId == parent.OwnerUserId && t.Id >= rangeStart)
                .MaxAsync(t => (long?)t.Id, ct) ?? rangeStart;

            foreach (var p in parts)
            {
                db.Transactions.Add(new Transaction
                {
                    Id = ++nextId,
                    OwnerUserId = parent.OwnerUserId,
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
                    Note = string.IsNullOrWhiteSpace(p.Note) ? null : p.Note.Trim(),
                });
            }
            if (parent.Splitted != SplitState.Parent)
            {
                parent.Splitted = SplitState.Parent;
                parent.SplitOriginalAmount = parent.Amount;
            }
            parent.IsNew = false;
            parent.SplitCheck = false;
            await db.SaveChangesAsync(ct);
        }
        finally
        {
            BankTransactionStore.Gate.Release();
        }
        Publish(parent.OwnerUserId);
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
        parent.SplitCheck = false;
        await db.SaveChangesAsync(ct);
        Publish(parent.OwnerUserId);
    }

    // ---- helpers ----

    private static async Task<Transaction> LoadParentAsync(BudgetDbContext db, long id, CancellationToken ct)
    {
        var row = await db.Transactions.FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw new SplitException(SplitProblem.NotFound);
        if (row.Splitted == SplitState.Child)
        {
            if (row.SplitParentId is not { } pid) throw new SplitException(SplitProblem.Invalid);
            row = await db.Transactions.FirstOrDefaultAsync(t => t.Id == pid, ct) ?? throw new SplitException(SplitProblem.NotFound);
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
            var block = TransactionEditRules.Check(db.Scope, r.OwnerUserId, r.AccountId,
                r.AccountId is { } aid && providers.TryGetValue(aid, out var p) ? p : null);
            if (block != EditBlock.None)
                throw new AccessDeniedException("Not allowed to edit these transactions.");
        }
    }

    private void Publish(string ownerId) => _notifier.Publish(new DataChange(DataArea.ImportedData, OwnerUserId: ownerId), _origin.Id);
}
