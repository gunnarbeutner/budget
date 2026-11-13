using Microsoft.EntityFrameworkCore;

namespace OpenBudget.Services;

/// <summary>
/// Small internal helpers around per-account provider ownership, used when bank logins link and unlink accounts
/// (switching a MoneyMoney account to its bank connection is <see cref="AccountSwitchService"/>).
/// </summary>
public class AccountService
{
    private readonly IDbContextFactory<BudgetDbContext> _dbFactory;

    public AccountService(IDbContextFactory<BudgetDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    /// <summary>Sets the provider owning an account. Returns false when the account doesn't exist.</summary>
    public async Task<bool> SetProviderAsync(long accountId, string provider, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(provider)) throw new ArgumentException("Provider is required.", nameof(provider));
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        // ExecuteUpdate bypasses the context's write guard, so check ownership of the account.
        var ownerId = await db.Accounts.Where(a => a.Id == accountId).Select(a => a.OwnerUserId).FirstOrDefaultAsync(ct);
        if (ownerId == null) return false;
        db.Scope?.RequireOwner(ownerId);
        var rows = await db.Accounts
            .Where(a => a.Id == accountId)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.Provider, provider), ct);
        return rows > 0;
    }

    /// <summary>
    /// Timestamp (UTC) of the newest booked transaction of an account, ignoring pending rows —
    /// the point a native connector resumes fetching from. Null when the account has no booked
    /// transactions.
    /// </summary>
    public async Task<DateTime?> GetNewestBookedDateAsync(long accountId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var newest = await db.Transactions
            .Where(t => t.AccountId == accountId && t.Booked)
            .MaxAsync(t => (long?)t.Timestamp, ct);
        return newest is { } s ? DateTimeOffset.FromUnixTimeSeconds(s).UtcDateTime : null;
    }
}
