using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using OpenBudget.Connectors.Amazon;
using OpenBudget.Connectors.Receipts;
using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>A payment instrument of an order source and the account it is mapped to.</summary>
public sealed record InstrumentView(int Id, string Name, bool IsPoints, long? AccountId, string? AccountName, int Charges, int Matched);

/// <summary>An account an instrument can be mapped to; <see cref="Suggested"/>: its IBAN or name ends with the card's last digits.</summary>
/// <see cref="Shared"/>: an account of another user the user may edit bookings of through a shared project.
public sealed record InstrumentCandidate(long AccountId, string Name, bool Suggested, bool Closed = false, long? GroupId = null, long? Sort = null, string? Icon = null, bool Shared = false);

/// <summary>A charge without a booking: <see cref="Ambiguous"/> when several bookings fit.</summary>
public sealed record OpenChargeView(DateTime Date, string OrderId, long AmountCents, string Instrument, bool Ambiguous, Uri OrderUrl);

/// <summary>
/// The owner's view of an order source (Amazon): mapping its payment instruments to existing accounts and the charges
/// (receipts of the shop <see cref="ReceiptShops.Amazon"/>) still without a booking. Reads and writes through the user-scoped context;
/// re-matching after a mapping change runs on a system context for the owner only.
/// </summary>
public sealed partial class OrderEnrichmentService
{
    private static readonly AmazonOptions Amazon = new();
    private readonly IDbContextFactory<BudgetDbContext> _dbFactory;
    private readonly SystemDbContextFactory _system;
    private readonly ReceiptStore _store;
    private readonly AccessScopeProvider _access;
    private readonly ChangeNotificationService _notifier;
    private readonly ChangeOrigin _origin;

    public OrderEnrichmentService(
        IDbContextFactory<BudgetDbContext> dbFactory, SystemDbContextFactory system, ReceiptStore store, AccessScopeProvider access,
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
        var counts = await db.ReceiptInvoices.AsNoTracking().Where(c => c.BankLoginId == loginId && c.PaymentMethodId != null)
            .GroupBy(c => c.PaymentMethodId!).Select(g => new { g.Key, All = g.Count(), Matched = g.Count(c => c.TransactionId != null) })
            .ToDictionaryAsync(g => g.Key, ct);
        return rows.Select(r => new InstrumentView(r.Id, r.Name, AmazonPages.IsPointsLabel(r.Name), r.EnrichAccountId,
            r.EnrichAccountId is { } a ? names.GetValueOrDefault(a) : null,
            counts.TryGetValue(r.RemoteId, out var c) ? c.All : 0, counts.TryGetValue(r.RemoteId, out var m) ? m.Matched : 0)).ToList();
    }

    /// <summary>
    /// The user's non-depot accounts and those of other users whose bookings the user may edit through a project shared
    /// with them as editor (see <see cref="AccessScope.CanEditTransactionsOf"/>), closed ones last (old payment methods
    /// paid from them); those whose IBAN or name ends with the instrument's last digits come first, marked.
    /// </summary>
    public async Task<List<InstrumentCandidate>> GetCandidatesAsync(int instrumentId, CancellationToken ct = default)
    {
        var scope = await _access.GetAsync();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var instrument = await db.BankLoginAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == instrumentId, ct);
        if (instrument == null) return [];
        var accounts = await db.Accounts.AsNoTracking()
            .Where(a => a.Type != 7 && a.Portfolio != 1).OrderBy(a => a.Name).ToListAsync(ct);
        accounts = accounts.Where(a => scope.CanEditTransactionsOf(a.OwnerUserId, a.Id)).ToList();
        var digits = LastDigits().Match(instrument.Name) is { Success: true } m ? m.Groups["d"].Value : null;
        bool Suggested(Account a) => digits != null
            && ((a.Iban != null && BankLoginService.NormalizeIban(a.Iban).EndsWith(digits, StringComparison.Ordinal))
                || (a.Name != null && LastDigits().Match(a.Name) is { Success: true } n && n.Groups["d"].Value.EndsWith(digits, StringComparison.Ordinal)));
        return accounts.Select(a => scope.Owns(a.OwnerUserId)
                ? new InstrumentCandidate(a.Id, a.Name ?? "?", Suggested(a), a.Closed, a.GroupId, a.Sort, AccountIcons.ForAccount(a.Icon, a.Type, false))
                : new InstrumentCandidate(a.Id, a.Name ?? "?", Suggested(a), a.Closed, null, null, AccountIcons.ForAccount(a.Icon, a.Type, false), Shared: true))
            .OrderBy(c => c.Closed).ThenByDescending(c => c.Suggested).ToList();
    }

    /// <summary>The user's account groups, for arranging the candidates as the sidebar does.</summary>
    public async Task<List<AccountGroupInfo>> GetAccountGroupsAsync(CancellationToken ct = default)
    {
        var scope = await _access.GetAsync();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.AccountGroups.AsNoTracking().Where(g => g.OwnerUserId == scope.UserId).ToListAsync(ct);
        return rows.Select(g => new AccountGroupInfo(g.Id, g.ParentGroupId, g.Name ?? "", g.Sort, Icon: g.Icon, Color: g.Color)).ToList();
    }

    /// <summary>
    /// Maps an instrument to one of the candidate accounts (see <see cref="GetCandidatesAsync"/>; null: none), then matches
    /// the charges again.
    /// </summary>
    public async Task MapAsync(int instrumentId, long? accountId, CancellationToken ct = default)
    {
        var scope = await _access.GetAsync();
        var userId = scope.UserId ?? throw new AccessDeniedException("Not signed in.");
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var instrument = await db.BankLoginAccounts.FirstOrDefaultAsync(a => a.Id == instrumentId, ct)
            ?? throw new AccessDeniedException("Payment method not found.");
        string? sharedOwner = null;
        if (accountId is { } id)
        {
            if (AmazonPages.IsPointsLabel(instrument.Name)) throw new InvalidOperationException("Points and gift cards are not booked on an account.");
            var account = await db.Accounts.AsNoTracking().Where(a => a.Id == id).Select(a => new { a.OwnerUserId }).FirstOrDefaultAsync(ct);
            if (account == null || !scope.CanEditTransactionsOf(account.OwnerUserId, id)) throw new AccessDeniedException("Account not found.");
            if (!scope.Owns(account.OwnerUserId)) sharedOwner = account.OwnerUserId;
        }
        instrument.EnrichAccountId = accountId;
        await db.SaveChangesAsync(ct);
        await using (var sys = await _system.CreateDbContextAsync(ct))
            await _store.MatchAsync(sys, userId, ct);
        _notifier.Publish(new DataChange(DataArea.BankSync), _origin.Id);
        _notifier.Publish(new DataChange(DataArea.ImportedData, OwnerUserId: userId), _origin.Id);
        if (sharedOwner != null) _notifier.Publish(new DataChange(DataArea.ImportedData, OwnerUserId: sharedOwner), _origin.Id);
    }

    /// <summary>The login's charges without a booking (newest first) and how many there are.</summary>
    public async Task<(int Count, List<OpenChargeView> Latest)> GetOpenChargesAsync(int loginId, int take = 10, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var q = db.ReceiptInvoices.AsNoTracking().Where(c => c.BankLoginId == loginId && (c.State == ReceiptMatchState.Unmatched || c.State == ReceiptMatchState.Ambiguous));
        var count = await q.CountAsync(ct);
        var latest = await q.OrderByDescending(c => c.DeliveryDate).ThenBy(c => c.Id).Take(take).ToListAsync(ct);
        return (count, latest.Select(c => new OpenChargeView(c.DeliveryDate ?? c.ReadAt, c.OrderNumber ?? "", -c.PaymentCents, c.PaymentMethod ?? "",
            c.State == ReceiptMatchState.Ambiguous, Amazon.OrderUrl(c.OrderNumber ?? ""))).ToList());
    }
}
