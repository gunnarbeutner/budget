using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>
/// Ids of imported rows (accounts, account/category groups, categories, transactions). Each user has an id space
/// (<see cref="UserDataState.IdSpace"/>, unique across users); a row the MoneyMoney import or a connector creates for
/// that user gets <see cref="Namespace"/>(idSpace, rowid), so two users' rows never collide and a user's own rows are
/// numbered by their rowid. Id space 1 maps identically. Ids carry no meaning beyond being unique: who owns a row is
/// its <c>OwnerUserId</c>.
/// </summary>
public static class RowIds
{
    /// <summary>Layout: 24 high bits = id space, 40 low bits = rowid. Id space 1 is identity.</summary>
    public static long Namespace(int idSpace, long rowid)
    {
        if (idSpace <= 0) throw new ArgumentOutOfRangeException(nameof(idSpace), "Id spaces are positive.");
        if (rowid < 0) throw new ArgumentOutOfRangeException(nameof(rowid), "Rowids are non-negative.");
        if (rowid >= (1L << 40)) throw new ArgumentOutOfRangeException(nameof(rowid), "Rowid exceeds 40-bit limit.");
        return idSpace == 1 ? rowid : ((long)idSpace << 40) | rowid;
    }

    /// <summary>
    /// MoneyMoney's sentinel rows (rowid 0 = "no category", rowid 1 = hidden root group) in an id space.
    /// </summary>
    public static IReadOnlyCollection<long> SentinelIds(int idSpace) => new[]
    {
        Namespace(idSpace, 0),
        Namespace(idSpace, 1)
    };

    public static long SentinelRoot(int idSpace) => Namespace(idSpace, 1);
    public static long SentinelZero(int idSpace) => Namespace(idSpace, 0);

    /// <summary>
    /// The state row of <paramref name="userId"/>, created (with the next free id space) when missing. Runs on a system
    /// or the user's own context.
    /// </summary>
    public static async Task<UserDataState> StateAsync(BudgetDbContext db, string userId, CancellationToken ct = default)
    {
        var state = await db.UserDataStates.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.UserId == userId, ct);
        if (state != null) return state;
        // Past every id space in use: assigned ones, and those of rows created before id spaces were per user.
        var assigned = await db.UserDataStates.IgnoreQueryFilters().MaxAsync(s => (int?)s.IdSpace, ct) ?? 0;
        var used = await db.Database.SqlQueryRaw<long>("""
            SELECT COALESCE(MAX(x), 0) AS "Value" FROM (
                SELECT MAX(Id) >> 40 AS x FROM accounts UNION ALL SELECT MAX(Id) >> 40 FROM account_groups
                UNION ALL SELECT MAX(Id) >> 40 FROM categories UNION ALL SELECT MAX(Id) >> 40 FROM category_groups
                UNION ALL SELECT MAX(Id) >> 40 FROM transactions)
            """).SingleAsync(ct);
        var next = (int)Math.Max(assigned, Math.Max(used, 1)) + 1;
        state = new UserDataState { UserId = userId, IdSpace = next };
        db.UserDataStates.Add(state);
        await db.SaveChangesAsync(ct);
        return state;
    }

    /// <summary>The state row of <paramref name="userId"/>, or an unsaved empty one when there is none (read only).</summary>
    public static async Task<UserDataState> PeekStateAsync(BudgetDbContext db, string userId, CancellationToken ct = default) =>
        await db.UserDataStates.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(s => s.UserId == userId, ct)
        ?? new UserDataState { UserId = userId };

    /// <summary>
    /// A fresh id for an account OpenBudget creates for <paramref name="userId"/> (a connector's or Portfolio Performance
    /// account): the next one in the connector half of the user's id space
    /// (<see cref="Connectors.Sync.BankTransactionStore.ConnectorRowBase"/> and up), counting accounts added to
    /// <paramref name="db"/> but not saved yet.
    /// </summary>
    public static async Task<long> NextConnectorAccountIdAsync(BudgetDbContext db, string userId, CancellationToken ct = default)
    {
        var idSpace = await IdSpaceAsync(db, userId, ct);
        var lo = Namespace(idSpace, Connectors.Sync.BankTransactionStore.ConnectorRowBase);
        var hi = Namespace(idSpace, (1L << 40) - 1);
        var saved = await db.Accounts.IgnoreQueryFilters().Where(a => a.Id > lo && a.Id <= hi).MaxAsync(a => (long?)a.Id, ct);
        var pending = db.Accounts.Local.Where(a => a.Id > lo && a.Id <= hi).Max(a => (long?)a.Id);
        return Math.Max(saved ?? lo, pending ?? lo) + 1;
    }

    /// <summary>MoneyMoney's sentinel rows of <paramref name="userId"/>'s data (none when the user never got an id space).</summary>
    public static async Task<IReadOnlyCollection<long>> SentinelIdsAsync(BudgetDbContext db, string userId, CancellationToken ct = default) =>
        (await PeekStateAsync(db, userId, ct)).IdSpace is > 0 and var space ? SentinelIds(space) : [];

    /// <summary>The hidden root category group of <paramref name="userId"/>'s data (see <see cref="SentinelIdsAsync"/>); 0 without an id space.</summary>
    public static async Task<long> SentinelRootAsync(BudgetDbContext db, string userId, CancellationToken ct = default) =>
        (await PeekStateAsync(db, userId, ct)).IdSpace is > 0 and var space ? SentinelRoot(space) : 0;

    /// <summary>The id space of <paramref name="userId"/> (see <see cref="StateAsync"/>).</summary>
    public static async Task<int> IdSpaceAsync(BudgetDbContext db, string userId, CancellationToken ct = default) =>
        (await StateAsync(db, userId, ct)).IdSpace;
}
