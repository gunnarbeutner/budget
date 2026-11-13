using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using OpenBudget.Connectors.Amazon;
using OpenBudget.Connectors.Sync;
using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>A payment instrument of an order source and the account it is mapped to.</summary>
public sealed record InstrumentView(int Id, string Name, bool IsPoints, long? AccountId, string? AccountName, int Charges, int Matched);

/// <summary>An account an instrument can be mapped to; <see cref="Suggested"/>: its IBAN or name ends with the card's last digits.</summary>
public sealed record InstrumentCandidate(long AccountId, string Name, bool Suggested);

/// <summary>A charge without a booking: <see cref="Ambiguous"/> when several bookings fit.</summary>
public sealed record OpenChargeView(DateTime Date, string OrderId, long AmountCents, string Instrument, bool Ambiguous, Uri OrderUrl);

/// <summary>An order a booking paid for.</summary>
public sealed record OrderLink(string OrderId, Uri Url);

/// <summary>The orders a booking paid for and the items its charges covered (<see cref="Unclear"/>: could not be told by shipment).</summary>
public sealed record BookingOrders(IReadOnlyList<OrderLink> Orders, string? Items, bool Unclear);

/// <summary>
/// The owner's view of an order source (Amazon): mapping its payment instruments to existing accounts, the charges
/// still without a booking, and the orders behind a booking. Reads and writes through the user-scoped context;
/// re-matching after a mapping change runs on a system context for the owner only.
/// </summary>
public sealed partial class OrderEnrichmentService
{
    private static readonly AmazonOptions Amazon = new();
    private readonly IDbContextFactory<BudgetDbContext> _dbFactory;
    private readonly SystemDbContextFactory _system;
    private readonly AmazonOrderStore _store;
    private readonly AccessScopeProvider _access;
    private readonly ChangeNotificationService _notifier;
    private readonly ChangeOrigin _origin;

    public OrderEnrichmentService(
        IDbContextFactory<BudgetDbContext> dbFactory, SystemDbContextFactory system, AmazonOrderStore store, AccessScopeProvider access,
        ChangeNotificationService notifier, ChangeOrigin origin)
    {
        _dbFactory = dbFactory;
        _system = system;
        _store = store;
        _access = access;
        _notifier = notifier;
        _origin = origin;
    }

    [GeneratedRegex(@"(?<d>\d{3,4})\s*$", RegexOptions.ExplicitCapture, 1000)]
    private static partial Regex LastDigits();

    public async Task<List<InstrumentView>> GetInstrumentsAsync(int loginId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.BankLoginAccounts.AsNoTracking().Where(a => a.BankLoginId == loginId).OrderBy(a => a.Name).ToListAsync(ct);
        var mapped = rows.Where(r => r.EnrichAccountId != null).Select(r => r.EnrichAccountId!.Value).ToList();
        var names = await db.Accounts.AsNoTracking().Where(a => mapped.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Name, ct);
        var counts = await db.AmazonCharges.AsNoTracking().Where(c => c.BankLoginId == loginId)
            .GroupBy(c => c.InstrumentId).Select(g => new { g.Key, All = g.Count(), Matched = g.Count(c => c.State == AmazonChargeState.Matched) })
            .ToDictionaryAsync(g => g.Key, ct);
        return rows.Select(r => new InstrumentView(r.Id, r.Name, AmazonPages.IsPointsLabel(r.Name), r.EnrichAccountId,
            r.EnrichAccountId is { } a ? names.GetValueOrDefault(a) : null,
            counts.TryGetValue(r.RemoteId, out var c) ? c.All : 0, counts.TryGetValue(r.RemoteId, out var m) ? m.Matched : 0)).ToList();
    }

    /// <summary>The user's open, non-depot accounts; those whose IBAN or name ends with the instrument's last digits come first, marked.</summary>
    public async Task<List<InstrumentCandidate>> GetCandidatesAsync(int instrumentId, CancellationToken ct = default)
    {
        var scope = await _access.GetAsync();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var instrument = await db.BankLoginAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == instrumentId, ct);
        if (instrument == null) return [];
        var accounts = await db.Accounts.AsNoTracking()
            .Where(a => a.OwnerUserId == scope.UserId && !a.Closed && a.Type != 7 && a.Portfolio != 1).OrderBy(a => a.Name).ToListAsync(ct);
        var digits = LastDigits().Match(instrument.Name) is { Success: true } m ? m.Groups["d"].Value : null;
        bool Suggested(Account a) => digits != null
            && ((a.Iban != null && BankLoginService.NormalizeIban(a.Iban).EndsWith(digits, StringComparison.Ordinal))
                || (a.Name != null && LastDigits().Match(a.Name) is { Success: true } n && n.Groups["d"].Value.EndsWith(digits, StringComparison.Ordinal)));
        return accounts.Select(a => new InstrumentCandidate(a.Id, a.Name ?? "?", Suggested(a)))
            .OrderByDescending(c => c.Suggested).ToList();
    }

    /// <summary>Maps an instrument to one of the user's accounts (null: none), then matches the charges again.</summary>
    public async Task MapAsync(int instrumentId, long? accountId, CancellationToken ct = default)
    {
        var scope = await _access.GetAsync();
        var userId = scope.UserId ?? throw new AccessDeniedException("Not signed in.");
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var instrument = await db.BankLoginAccounts.FirstOrDefaultAsync(a => a.Id == instrumentId, ct)
            ?? throw new AccessDeniedException("Payment method not found.");
        if (accountId is { } id)
        {
            if (AmazonPages.IsPointsLabel(instrument.Name)) throw new InvalidOperationException("Points and gift cards are not booked on an account.");
            if (!await db.Accounts.AnyAsync(a => a.Id == id && a.OwnerUserId == userId, ct)) throw new AccessDeniedException("Account not found.");
        }
        instrument.EnrichAccountId = accountId;
        await db.SaveChangesAsync(ct);
        await using (var sys = await _system.CreateDbContextAsync(ct))
            await _store.MatchAsync(sys, userId, ct);
        _notifier.Publish(new DataChange(DataArea.BankSync), _origin.Id);
        _notifier.Publish(new DataChange(DataArea.ImportedData, OwnerUserId: userId), _origin.Id);
    }

    /// <summary>The login's charges without a booking (newest first) and how many there are.</summary>
    public async Task<(int Count, List<OpenChargeView> Latest)> GetOpenChargesAsync(int loginId, int take = 10, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var q = db.AmazonCharges.AsNoTracking().Where(c => c.BankLoginId == loginId && (c.State == AmazonChargeState.Unmatched || c.State == AmazonChargeState.Ambiguous));
        var count = await q.CountAsync(ct);
        var latest = await q.OrderByDescending(c => c.Date).ThenBy(c => c.Id).Take(take).ToListAsync(ct);
        return (count, latest.Select(c => new OpenChargeView(c.Date, c.OrderId, c.AmountCents, c.Instrument, c.State == AmazonChargeState.Ambiguous, Amazon.OrderUrl(c.OrderId))).ToList());
    }

    /// <summary>The orders behind a booking (only for its owner); null when it has none.</summary>
    public async Task<BookingOrders?> GetOrdersAsync(long transactionId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var charges = await db.AmazonCharges.AsNoTracking().Where(c => c.TransactionId == transactionId).ToListAsync(ct);
        if (charges.Count == 0) return null;
        var paid = (await AmazonOrderStore.ItemsByBookingAsync(db, charges[0].OwnerUserId, charges, ct)).GetValueOrDefault(transactionId);
        var orders = charges.Select(c => c.OrderId).Distinct(StringComparer.Ordinal).Select(id => new OrderLink(id, Amazon.OrderUrl(id))).ToList();
        return new BookingOrders(orders, paid == null ? null : AmazonSplit.Describe(paid.Items.Select(i => (i.Title, i.Quantity))), paid?.Unclear == true);
    }
}
