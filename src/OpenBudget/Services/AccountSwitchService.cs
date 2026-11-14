using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>The account settings' view: who updates the account, whether the viewer owns it, and whether it is closed.</summary>
public sealed record AccountSwitchState(string Provider, bool CanManage, bool Closed = false);

/// <summary>Who updates an account, and closing or reopening accounts. Only the account's owner can close one.</summary>
public sealed class AccountSwitchService
{
    private readonly IDbContextFactory<BudgetDbContext> _dbFactory;
    private readonly AccessScopeProvider _access;
    private readonly ChangeNotificationService _notifier;
    private readonly ChangeOrigin _origin;

    public AccountSwitchService(
        IDbContextFactory<BudgetDbContext> dbFactory, AccessScopeProvider access, ChangeNotificationService notifier, ChangeOrigin origin)
    {
        _dbFactory = dbFactory;
        _access = access;
        _notifier = notifier;
        _origin = origin;
    }

    /// <summary>Who updates the account and whether the viewer owns it.</summary>
    public async Task<AccountSwitchState?> GetAccountStateAsync(long accountId, CancellationToken ct = default)
    {
        var scope = await _access.GetAsync();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var account = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == accountId, ct);
        if (account == null) return null;
        return new AccountSwitchState(account.Provider, scope.Owns(account.OwnerUserId), account.Closed);
    }

    /// <summary>Account type code of an account group row (never closed).</summary>
    private const long GroupType = 8;

    /// <summary>
    /// Marks the given accounts closed (or reopens them). Closed accounts keep their history and provider; the sidebar
    /// hides them by default. Owner only. Returns how many changed.
    /// </summary>
    public async Task<int> SetClosedAsync(IReadOnlyCollection<long> accountIds, bool closed, CancellationToken ct = default)
    {
        var scope = await _access.GetAsync();
        if (scope.UserId == null) throw new AccessDeniedException("Not signed in.");
        var ids = accountIds.Distinct().ToList();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var accounts = await db.Accounts.Where(a => ids.Contains(a.Id)).ToListAsync(ct);
        if (accounts.Count < ids.Count) throw new AccessDeniedException("Account not found.");
        foreach (var a in accounts) scope.RequireOwner(a.OwnerUserId);
        var changed = accounts.Where(a => a.Closed != closed && a.Type != GroupType).ToList();
        foreach (var a in changed) a.Closed = closed;
        await db.SaveChangesAsync(ct);
        foreach (var ownerId in changed.Select(a => a.OwnerUserId).Distinct(StringComparer.Ordinal))
            _notifier.Publish(new DataChange(DataArea.ImportedData, OwnerUserId: ownerId), _origin.Id);
        if (changed.Count > 0) _notifier.Publish(new DataChange(DataArea.ImportState), _origin.Id);
        return changed.Count;
    }
}
