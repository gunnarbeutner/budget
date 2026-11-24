using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>The other side of a paired booking, as the booking details show it.</summary>
public sealed record TransferCounterpart(long Id, long AccountId, string AccountName, DateTime Date, decimal Amount, string? Currency, string? Name, bool Manual);

/// <summary>
/// Shows, removes and makes transfer pairs (<see cref="Transaction.TransferPeerId"/>) for the current user. Only the
/// owner of the bookings changes their pairing. Removing a pair marks both bookings so automatic pairing leaves them
/// alone (<see cref="TransferPairState.Declined"/>).
/// </summary>
public sealed class TransferPairService
{
    /// <summary>The most days apart a booking offered for manual pairing may be.</summary>
    public const int ManualWindowDays = 14;

    private readonly IDbContextFactory<BudgetDbContext> _dbFactory;
    private readonly ChangeNotificationService _notifier;

    public TransferPairService(IDbContextFactory<BudgetDbContext> dbFactory, ChangeNotificationService notifier)
    {
        _dbFactory = dbFactory;
        _notifier = notifier;
    }

    /// <summary>The booking's counterpart, or null when it is not paired (or the user cannot see the other side).</summary>
    public async Task<TransferCounterpart?> GetCounterpartAsync(long transactionId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.Transactions.AsNoTracking().Where(t => t.Id == transactionId)
            .Select(t => new { t.TransferPeerId, t.TransferState }).FirstOrDefaultAsync(ct);
        if (row?.TransferPeerId is not { } peerId) return null;
        return (await DescribeAsync(db, db.Transactions.Where(t => t.Id == peerId), row.TransferState == TransferPairState.Manual, ct)).FirstOrDefault();
    }

    /// <summary>
    /// Bookings the user could pair <paramref name="transactionId"/> with: unpaired, booked and whole, on another
    /// account of the same owner, with the opposite amount of the same size, at most <see cref="ManualWindowDays"/> days
    /// apart; nearest first.
    /// </summary>
    public async Task<IReadOnlyList<TransferCounterpart>> GetCandidatesAsync(long transactionId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.Transactions.AsNoTracking().FirstOrDefaultAsync(t => t.Id == transactionId, ct);
        if (row == null || !Pairable(row) || row.TransferPeerId != null) return [];
        var window = ManualWindowDays * 86400L;
        var query = db.Transactions.Where(t => t.OwnerUserId == row.OwnerUserId && t.AccountId != null && t.AccountId != row.AccountId
            && t.Amount == -row.Amount && t.Booked && (t.Splitted == null || t.Splitted == SplitState.Normal) && t.TransferPeerId == null
            && t.Timestamp >= row.Timestamp - window && t.Timestamp <= row.Timestamp + window);
        return (await DescribeAsync(db, query, false, ct)).OrderBy(c => Math.Abs((c.Date - row.Date).TotalDays)).Take(10).ToList();
    }

    /// <summary>Removes the booking's pair; neither side is paired automatically again.</summary>
    public async Task UnpairAsync(long transactionId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.Transactions.FirstOrDefaultAsync(t => t.Id == transactionId, ct) ?? throw new InvalidOperationException("Unknown booking.");
        RequireOwner(db, row);
        var peer = row.TransferPeerId is { } peerId ? await db.Transactions.FirstOrDefaultAsync(t => t.Id == peerId, ct) : null;
        foreach (var t in new[] { row, peer }.OfType<Transaction>())
        {
            t.TransferPeerId = null;
            t.TransferState = TransferPairState.Declined;
        }
        await db.SaveChangesAsync(ct);
        Publish(row.OwnerUserId);
    }

    /// <summary>Pairs two bookings by hand (see <see cref="GetCandidatesAsync"/> for which ones fit).</summary>
    public async Task PairAsync(long transactionId, long peerId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.Transactions.Where(t => t.Id == transactionId || t.Id == peerId).ToListAsync(ct);
        var row = rows.FirstOrDefault(t => t.Id == transactionId);
        var peer = rows.FirstOrDefault(t => t.Id == peerId);
        if (row == null || peer == null) throw new InvalidOperationException("Unknown booking.");
        RequireOwner(db, row);
        if (peer.OwnerUserId != row.OwnerUserId || peer.AccountId == row.AccountId || !Pairable(row) || !Pairable(peer)
            || row.TransferPeerId != null || peer.TransferPeerId != null || Math.Sign(row.Amount) == Math.Sign(peer.Amount))
            throw new InvalidOperationException("These bookings cannot be paired.");
        row.TransferPeerId = peer.Id;
        peer.TransferPeerId = row.Id;
        row.TransferState = peer.TransferState = TransferPairState.Manual;
        await db.SaveChangesAsync(ct);
        Publish(row.OwnerUserId);
    }

    private static bool Pairable(Transaction t) =>
        t.AccountId != null && t.Booked && t.Amount != 0 && (t.Splitted == null || t.Splitted == SplitState.Normal);

    private static void RequireOwner(BudgetDbContext db, Transaction row)
    {
        if (db.Scope != null && !db.Scope.Owns(row.OwnerUserId)) throw new AccessDeniedException("Only the owner pairs transfers.");
    }

    private static async Task<List<TransferCounterpart>> DescribeAsync(BudgetDbContext db, IQueryable<Transaction> rows, bool manual, CancellationToken ct)
    {
        var found = await rows.AsNoTracking().Where(t => t.AccountId != null)
            .Select(t => new { t.Id, AccountId = t.AccountId!.Value, t.Timestamp, t.Amount, t.Currency, t.Name }).ToListAsync(ct);
        var accountIds = found.Select(f => f.AccountId).Distinct().ToList();
        var names = await db.Accounts.AsNoTracking().Where(a => accountIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Name ?? "", ct);
        return found.Where(f => names.ContainsKey(f.AccountId))
            .Select(f => new TransferCounterpart(f.Id, f.AccountId, names[f.AccountId], DateTimeOffset.FromUnixTimeSeconds(f.Timestamp).UtcDateTime,
                f.Amount / 100m, f.Currency, f.Name, manual))
            .ToList();
    }

    private void Publish(string ownerUserId) => _notifier.Publish(new DataChange(DataArea.ImportedData, OwnerUserId: ownerUserId));
}
