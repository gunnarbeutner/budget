using Microsoft.EntityFrameworkCore;
using OpenBudget.Connectors.Sync;
using OpenBudget.Importers.MoneyMoney;
using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>A linked account as the switch sees it: still MoneyMoney's (<see cref="Switched"/> false) or already the connector's.</summary>
public sealed record AccountSwitchLink(
    int LoginAccountId,
    string LoginName,
    long AccountId,
    string AccountName,
    string OwnerUserId,
    bool Switched,
    DateTime? SwitchedAt,
    DateTime? BankDataFetchedAt);

/// <summary>
/// The account settings' view: who updates the account, and (for the owner) its bank link and whether it can be
/// kept as a manual account (<see cref="CanKeepManual"/>) or returned to MoneyMoney (<see cref="CanReturnToMoneyMoney"/>;
/// refused while <see cref="ImportFinished"/>).
/// </summary>
public sealed record AccountSwitchState(
    string Provider, bool CanManage, AccountSwitchLink? Link, bool CanKeepManual = false, bool CanReturnToMoneyMoney = false, bool ImportFinished = false,
    bool Closed = false);

/// <summary>An account about to be kept as a manual account.</summary>
public sealed record KeepManualPreview(SourceAccountItem Account);

/// <summary>A MoneyMoney account without a bank link, as the switch overview lists it.</summary>
/// <param name="GroupPath">The account's groups from the top down, joined with " › " (null when ungrouped).</param>
public sealed record SourceAccountItem(long AccountId, string Name, int Bookings, DateOnly? LastBooking, decimal? Balance, string? Currency,
    string Provider = AccountProviders.MoneyMoney, string? GroupPath = null);

/// <summary>The signed-in user's switch progress and one-time import setting.</summary>
public sealed record SourceSwitchState(
    string OwnerUserId,
    DateTime? ImportFinishedAt,
    IReadOnlyList<AccountSwitchLink> Links,
    int MoneyMoneyAccountsWithoutLink)
{
    /// <summary>The accounts counted in <see cref="MoneyMoneyAccountsWithoutLink"/>: still MoneyMoney's, no bank link.</summary>
    public IReadOnlyList<SourceAccountItem> MoneyMoneyAccounts { get; init; } = [];

    /// <summary>Accounts of the user kept as manual accounts (they can return to MoneyMoney).</summary>
    public IReadOnlyList<SourceAccountItem> ManualAccounts { get; init; } = [];

    /// <summary>Accounts of the user marked closed (they count as handled, whoever updates them).</summary>
    public IReadOnlyList<SourceAccountItem> ClosedAccounts { get; init; } = [];
}

/// <summary>
/// What becomes of accounts imported from MoneyMoney. Linking one to a bank login makes it the connector's at once
/// (<see cref="BankLoginService.LinkAsync"/>); <see cref="SwitchLinkedMoneyMoneyAccountsAsync"/> does the same at
/// startup for links that left the account MoneyMoney's. An account without a bank link can be kept as a manual account
/// (<see cref="KeepManualAsync"/>): its imported rows stay as editable history and uploads stop touching it;
/// <see cref="ReturnToMoneyMoneyAsync"/> undoes that. The one-time import setting refuses further uploads.
///
/// Only the account's owner can change these or the import setting.
/// </summary>
public sealed class AccountSwitchService
{
    private readonly IDbContextFactory<BudgetDbContext> _dbFactory;
    private readonly AccessScopeProvider _access;
    private readonly MoneyMoneyImportService? _importer;
    private readonly ChangeNotificationService _notifier;
    private readonly ChangeOrigin _origin;
    private readonly TimeProvider _clock;

    public AccountSwitchService(
        IDbContextFactory<BudgetDbContext> dbFactory, AccessScopeProvider access,
        ChangeNotificationService notifier, ChangeOrigin origin, TimeProvider clock,
        MoneyMoneyImportService? importer = null)
    {
        _dbFactory = dbFactory;
        _access = access;
        _importer = importer;
        _notifier = notifier;
        _origin = origin;
        _clock = clock;
    }

    /// <summary>Who updates the account; the bank link only for the owner (editors and viewers see the provider).</summary>
    public async Task<AccountSwitchState?> GetAccountStateAsync(long accountId, CancellationToken ct = default)
    {
        var scope = await _access.GetAsync();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var account = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == accountId, ct);
        if (account == null) return null;
        if (!scope.Owns(account.OwnerUserId)) return new AccountSwitchState(account.Provider, false, null, Closed: account.Closed);
        var link = (await LinksAsync(db, l => l.LinkedAccountId == accountId, ct)).Where(l => !l.Created).Select(l => l.Link).FirstOrDefault();
        var source = await RowIds.PeekStateAsync(db, account.OwnerUserId, ct);
        var linked = await db.BankLoginAccounts.AnyAsync(l => l.LinkedAccountId == accountId, ct);
        return new AccountSwitchState(account.Provider, true, link,
            CanKeepManual: account.Provider == AccountProviders.MoneyMoney && !linked && account.Type != GroupType,
            CanReturnToMoneyMoney: IsKeptManual(account, source) && !linked,
            ImportFinished: source.MoneyMoneyImportFinishedAt != null,
            Closed: account.Closed);
    }

    /// <summary>The switch state of <paramref name="ownerId"/>'s data: linked accounts and whether the MoneyMoney import is finished.</summary>
    public async Task<SourceSwitchState> GetSourceStateAsync(string ownerId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        db.Scope?.RequireOwner(ownerId);
        var source = await RowIds.PeekStateAsync(db, ownerId, ct);
        var accountIds = await db.Accounts.AsNoTracking().Where(a => a.OwnerUserId == ownerId).Select(a => a.Id).ToListAsync(ct);
        var links = (await LinksAsync(db, l => l.LinkedAccountId != null && accountIds.Contains(l.LinkedAccountId.Value), ct))
            .Where(l => !l.Created).Select(l => l.Link).ToList();
        var linked = links.Select(l => l.AccountId).ToHashSet();
        linked.UnionWith(await db.BankLoginAccounts.AsNoTracking()
            .Where(l => l.LinkedAccountId != null && accountIds.Contains(l.LinkedAccountId.Value)).Select(l => l.LinkedAccountId!.Value).ToListAsync(ct));
        var all = (await db.Accounts.AsNoTracking().Where(a => a.OwnerUserId == ownerId && a.Type != GroupType).ToListAsync(ct))
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var closed = all.Where(a => a.Closed).ToList();
        var candidates = all.Where(a => !a.Closed && !linked.Contains(a.Id)).ToList();
        var moneyMoney = candidates.Where(a => a.Provider == AccountProviders.MoneyMoney).ToList();
        var manual = candidates.Where(a => IsKeptManual(a, source)).ToList();
        var items = await ItemsAsync(db, [.. moneyMoney, .. manual, .. closed], ct);
        return new SourceSwitchState(ownerId, source.MoneyMoneyImportFinishedAt, links, moneyMoney.Count)
        {
            MoneyMoneyAccounts = ByGroup(moneyMoney.Select(a => items[a.Id])),
            ManualAccounts = ByGroup(manual.Select(a => items[a.Id])),
            ClosedAccounts = ByGroup(closed.Select(a => items[a.Id])),
        };
    }

    /// <summary>Account type code of an account group row (never kept as a manual account).</summary>
    private const long GroupType = 8;

    /// <summary>
    /// A manual account that MoneyMoney delivered: its owner imported from MoneyMoney and it has a MoneyMoney rowid
    /// (accounts OpenBudget or a connector creates live in the connector half of the id space).
    /// </summary>
    private static bool IsKeptManual(Account account, UserDataState source) =>
        account.Provider == AccountProviders.Manual
        && (source.LastUploadAt != null || source.MoneyMoneyImportFinishedAt != null)
        && (account.Id & ((1L << 40) - 1)) < BankTransactionStore.ConnectorRowBase;

    /// <summary>Accounts ordered by their group path, then by name (ungrouped ones last).</summary>
    private static List<SourceAccountItem> ByGroup(IEnumerable<SourceAccountItem> items) =>
        [.. items.OrderBy(i => i.GroupPath == null).ThenBy(i => i.GroupPath, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)];

    private static async Task<Dictionary<long, SourceAccountItem>> ItemsAsync(BudgetDbContext db, List<Account> accounts, CancellationToken ct)
    {
        var stats = new Dictionary<long, (int Count, long Last)>();
        foreach (var chunk in accounts.Select(a => a.Id).Chunk(500))
        {
            var part = chunk;
            foreach (var g in await db.Transactions.AsNoTracking()
                         .Where(t => t.AccountId != null && part.Contains(t.AccountId.Value))
                         .GroupBy(t => t.AccountId!.Value)
                         .Select(g => new { g.Key, Count = g.Count(), Last = g.Max(t => t.Timestamp) })
                         .ToListAsync(ct))
                stats[g.Key] = (g.Count, g.Last);
        }
        var ownerIds = accounts.Select(a => a.OwnerUserId).Distinct(StringComparer.Ordinal).ToList();
        var groups = await db.AccountGroups.AsNoTracking().Where(g => ownerIds.Contains(g.OwnerUserId))
            .ToDictionaryAsync(g => g.Id, ct);
        string? PathOf(long? groupId)
        {
            var names = new List<string>();
            for (var depth = 0; groupId is { } id && groups.TryGetValue(id, out var g) && depth < 20; depth++, groupId = g.ParentGroupId)
                names.Insert(0, g.Name ?? "?");
            return names.Count > 0 ? string.Join(" › ", names) : null;
        }
        return accounts.ToDictionary(a => a.Id, a =>
        {
            var has = stats.TryGetValue(a.Id, out var st);
            return new SourceAccountItem(a.Id, a.Name ?? "?", has ? st.Count : 0,
                has ? DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(st.Last).UtcDateTime) : null, a.CurrentBalance, a.Currency, a.Provider,
                PathOf(a.GroupId));
        });
    }

    /// <summary>The accounts <see cref="KeepManualAsync"/> would change (owned, still MoneyMoney's, without a bank link).</summary>
    public async Task<List<KeepManualPreview>> PreviewKeepManualAsync(IReadOnlyCollection<long> accountIds, CancellationToken ct = default)
    {
        var scope = await _access.GetAsync();
        var ids = accountIds.Distinct().ToList();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var linked = (await db.BankLoginAccounts.AsNoTracking().Where(l => l.LinkedAccountId != null && ids.Contains(l.LinkedAccountId.Value))
            .Select(l => l.LinkedAccountId!.Value).ToListAsync(ct)).ToHashSet();
        var accounts = (await db.Accounts.AsNoTracking().Where(a => ids.Contains(a.Id) && a.Provider == AccountProviders.MoneyMoney && a.Type != GroupType).ToListAsync(ct))
            .Where(a => scope.Owns(a.OwnerUserId) && !linked.Contains(a.Id))
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var items = await ItemsAsync(db, accounts, ct);
        return accounts.Select(a => new KeepManualPreview(items[a.Id])).ToList();
    }

    /// <summary>
    /// Keeps the given accounts (still MoneyMoney's, without a bank link; others are skipped) as manual accounts: MoneyMoney
    /// uploads stop touching them and their rows become editable history. Returns how many changed.
    /// </summary>
    public async Task<int> KeepManualAsync(IReadOnlyCollection<long> accountIds, CancellationToken ct = default)
    {
        var scope = await _access.GetAsync();
        if (scope.UserId == null) throw new AccessDeniedException("Not signed in.");
        var ids = accountIds.Distinct().ToList();
        List<Account> accounts;
        HashSet<long> linked;
        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
        {
            accounts = await db.Accounts.AsNoTracking().Where(a => ids.Contains(a.Id)).ToListAsync(ct);
            linked = (await db.BankLoginAccounts.AsNoTracking().Where(l => l.LinkedAccountId != null && ids.Contains(l.LinkedAccountId.Value))
                .Select(l => l.LinkedAccountId!.Value).ToListAsync(ct)).ToHashSet();
        }
        if (accounts.Count < ids.Count) throw new AccessDeniedException("Account not found.");
        foreach (var a in accounts) scope.RequireOwner(a.OwnerUserId);
        var todo = accounts.Where(a => a.Provider == AccountProviders.MoneyMoney && a.Type != GroupType && !linked.Contains(a.Id)).ToList();
        if (todo.Count == 0 && accounts.Any(a => linked.Contains(a.Id) && a.Provider == AccountProviders.MoneyMoney))
            throw new InvalidOperationException("An account linked to a bank login is switched to that login; unlink it first to keep it as a manual account.");

        var changed = 0;
        foreach (var group in todo.GroupBy(a => a.OwnerUserId, StringComparer.Ordinal))
        {
            var name = group.Key;
            var groupIds = group.Select(a => a.Id).ToList();
            async Task<int> ApplyAsync()
            {
                await using var db = await _dbFactory.CreateDbContextAsync(ct);
                var rows = await db.Accounts.Where(a => groupIds.Contains(a.Id) && a.Provider == AccountProviders.MoneyMoney).ToListAsync(ct);
                foreach (var a in rows)
                {
                    a.Provider = AccountProviders.Manual;
                    a.Error = null;
                }
                await db.SaveChangesAsync(ct);
                return rows.Count;
            }
            changed += _importer == null ? await ApplyAsync() : await _importer.WithOwnerLockAsync(name, ApplyAsync, ct);
            _notifier.Publish(new DataChange(DataArea.ImportedData, OwnerUserId: group.Key), _origin.Id);
        }
        if (todo.Count > 0) _notifier.Publish(new DataChange(DataArea.ImportState), _origin.Id);
        return changed;
    }

    /// <summary>
    /// Marks the given accounts closed (or reopens them). Closed accounts keep their history and provider; the sidebar
    /// hides them by default and they count as handled in the switch overview. Owner only. Returns how many changed.
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

    /// <summary>
    /// Returns a kept manual account to MoneyMoney; the next upload replaces its rows, balance and bank details (bookings
    /// entered or edited meanwhile are not kept). Refused while the owner's MoneyMoney import is finished.
    /// </summary>
    public async Task ReturnToMoneyMoneyAsync(long accountId, CancellationToken ct = default)
    {
        var scope = await _access.GetAsync();
        if (scope.UserId == null) throw new AccessDeniedException("Not signed in.");
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == accountId, ct)
            ?? throw new AccessDeniedException("Account not found.");
        scope.RequireOwner(account.OwnerUserId);
        if (account.Provider == AccountProviders.MoneyMoney) return;
        var source = await RowIds.PeekStateAsync(db, account.OwnerUserId, ct);
        if (!IsKeptManual(account, source) || await db.BankLoginAccounts.AnyAsync(l => l.LinkedAccountId == accountId, ct))
            throw new InvalidOperationException("Only a manual account that came from MoneyMoney can return to it.");
        if (source.MoneyMoneyImportFinishedAt != null)
            throw new InvalidOperationException("The MoneyMoney import is finished; resume it before returning an account to MoneyMoney.");
        account.Provider = AccountProviders.MoneyMoney;
        await db.SaveChangesAsync(ct);
        _notifier.Publish(new DataChange(DataArea.ImportedData, OwnerUserId: account.OwnerUserId), _origin.Id);
        _notifier.Publish(new DataChange(DataArea.ImportState), _origin.Id);
    }

    /// <summary>
    /// Makes every linked account that MoneyMoney still owns the connector's (linking does that right away; this
    /// covers links made while MoneyMoney kept updating linked accounts). A bank report a sync stored for the account
    /// is applied like a sync (a bank row that is the same booking as an imported row takes that row over; nothing is
    /// marked new); without one, the first sync matches the bank's rows from the newest imported booking on. Runs at
    /// startup; idempotent. Returns how many accounts it switched.
    /// </summary>
    public static async Task<int> SwitchLinkedMoneyMoneyAccountsAsync(SystemDbContextFactory system, BankTransactionStore store, DateTime now,
        CancellationToken ct = default)
    {
        await using var db = await system.CreateDbContextAsync(ct);
        var rows = await db.BankLoginAccounts.Include(l => l.BankLogin)
            .Where(l => l.LinkedAccountId != null && !l.CreatedByConnector)
            .Join(db.Accounts.Where(a => a.Provider == AccountProviders.MoneyMoney), l => l.LinkedAccountId, a => a.Id, (l, a) => new { Row = l, Account = a })
            .ToListAsync(ct);
        foreach (var r in rows)
        {
            var (row, account) = (r.Row, r.Account);
            if (PendingRefresh.Deserialize(row.PendingRefreshJson) is { } refresh)
            {
                DateOnly? since = row.PendingSinceUtc is { } s ? DateOnly.FromDateTime(s) : null;
                await store.ApplyAsync(db, account.Id, refresh, since, row.PendingRefreshAt ?? now, ct, markNew: false);
                row.LastSuccessAt = row.PendingRefreshAt ?? now;
                row.SyncFloorUtc = row.PendingSinceUtc;
            }
            else
            {
                row.LastSuccessAt = null;
                row.SyncFloorUtc = null;
            }
            account.Provider = row.BankLogin.ConnectorType;
            row.SwitchedAt = now;
            row.PendingRefreshJson = null;
            row.PendingRefreshAt = null;
            row.PendingSinceUtc = null;
            row.LastError = null;
            await db.SaveChangesAsync(ct);
        }
        return rows.Count;
    }

    /// <summary>Marks the user's MoneyMoney import finished (further imports are refused) or resumes it.</summary>
    public async Task SetImportFinishedAsync(string ownerId, bool finished, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        db.Scope?.RequireOwner(ownerId);
        var source = await RowIds.StateAsync(db, ownerId, ct);
        source.MoneyMoneyImportFinishedAt = finished ? source.MoneyMoneyImportFinishedAt ?? _clock.GetUtcNow().UtcDateTime : null;
        await db.SaveChangesAsync(ct);
        _notifier.Publish(new DataChange(DataArea.ImportState, OwnerUserId: ownerId), _origin.Id);
    }

    /// <summary>The current user's links matching <paramref name="filter"/> (other users' logins are filtered out by the context).</summary>
    private static async Task<List<(AccountSwitchLink Link, bool Created, BankLoginAccount Row)>> LinksAsync(
        BudgetDbContext db, System.Linq.Expressions.Expression<Func<BankLoginAccount, bool>> filter, CancellationToken ct)
    {
        var rows = await db.BankLoginAccounts.AsNoTracking().Include(l => l.BankLogin)
            .Where(l => l.LinkedAccountId != null).Where(filter).ToListAsync(ct);
        var accountIds = rows.Select(r => r.LinkedAccountId!.Value).ToList();
        var accounts = await db.Accounts.AsNoTracking().Where(a => accountIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);
        var result = new List<(AccountSwitchLink, bool, BankLoginAccount)>();
        foreach (var r in rows)
        {
            if (!accounts.TryGetValue(r.LinkedAccountId!.Value, out var a)) continue;
            var switched = a.Provider == r.BankLogin.ConnectorType;
            if (!switched && a.Provider != AccountProviders.MoneyMoney) continue;
            result.Add((new AccountSwitchLink(r.Id, r.BankLogin.DisplayName, a.Id, a.Name ?? r.Name, a.OwnerUserId,
                switched, r.SwitchedAt, r.PendingRefreshAt), r.CreatedByConnector, r));
        }
        return result;
    }
}
