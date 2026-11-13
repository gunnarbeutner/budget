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
    int SourceId,
    string SourceName,
    bool Switched,
    DateTime? SwitchedAt,
    DateTime? BankDataFetchedAt);

/// <summary>
/// What switching one linked account would do. <see cref="HistoryEnd"/> is the day of MoneyMoney's newest booking (the
/// cutover); <see cref="HistoryRows"/> MoneyMoney rows stay as the account's history (editable from then on). The other
/// counts are only known once a sync fetched the bank's rows (<see cref="BankDataFetchedAt"/>): bank rows that are the
/// same booking as a MoneyMoney row (<see cref="Matched"/>, merged into one row), new bank rows, MoneyMoney rows in the
/// compared window without a bank counterpart (kept), and MoneyMoney's pending rows the bank's replace.
/// </summary>
public sealed record AccountSwitchPreview(
    AccountSwitchLink Link,
    DateOnly? HistoryEnd,
    DateOnly? ComparedFrom,
    int HistoryRows,
    DateTime? BankDataFetchedAt,
    int Matched,
    int NewFromBank,
    int UnmatchedMoneyMoney,
    int PendingReplaced,
    decimal? BankBalance);

/// <summary>
/// The account settings' view: who updates the account, and (for the source owner) its bank link and whether it can be
/// kept as a manual account (<see cref="CanKeepManual"/>) or returned to MoneyMoney (<see cref="CanReturnToMoneyMoney"/>;
/// refused while <see cref="ImportFinished"/>).
/// </summary>
public sealed record AccountSwitchState(
    string Provider, bool CanManage, AccountSwitchLink? Link, bool CanKeepManual = false, bool CanReturnToMoneyMoney = false, bool ImportFinished = false,
    bool Closed = false);

/// <summary>An account about to be kept as a manual account.</summary>
public sealed record KeepManualPreview(SourceAccountItem Account, string SourceName);

/// <summary>An account of a MoneyMoney source without a bank link, as the switch overview lists it.</summary>
/// <param name="GroupPath">The account's groups from the top down, joined with " › " (null when ungrouped).</param>
public sealed record SourceAccountItem(long AccountId, string Name, int Bookings, DateOnly? LastBooking, decimal? Balance, string? Currency,
    string Provider = AccountProviders.MoneyMoney, string? GroupPath = null);

/// <summary>A source's switch progress and one-time import setting (owner only).</summary>
public sealed record SourceSwitchState(
    int SourceId,
    string SourceName,
    DateTime? ImportFinishedAt,
    IReadOnlyList<AccountSwitchLink> Links,
    int MoneyMoneyAccountsWithoutLink)
{
    /// <summary>The accounts counted in <see cref="MoneyMoneyAccountsWithoutLink"/>: still MoneyMoney's, no bank link.</summary>
    public IReadOnlyList<SourceAccountItem> MoneyMoneyAccounts { get; init; } = [];

    /// <summary>Accounts of this source kept as manual accounts (they can return to MoneyMoney).</summary>
    public IReadOnlyList<SourceAccountItem> ManualAccounts { get; init; } = [];

    /// <summary>Accounts of this source marked closed (they count as handled, whoever updates them).</summary>
    public IReadOnlyList<SourceAccountItem> ClosedAccounts { get; init; } = [];
}

/// <summary>
/// Moves accounts from MoneyMoney to a bank connection ("switch") and back. Linking an account MoneyMoney owns only
/// prepares it: syncs fetch the bank's rows for a preview but write nothing. The switch makes the connector the
/// account's provider, so MoneyMoney uploads stop touching it (rows, balance, structure); MoneyMoney's rows stay as
/// editable history; the fetched bank rows are applied with <see cref="BankTransactionStore"/> (a bank row that is the
/// same booking as a MoneyMoney row takes that row over: the bank's texts win, category, note, check mark and splits stay;
/// nothing it adds is marked new). Switching needs the source's structure to be OpenBudget's (otherwise uploads would
/// still rewrite the account's name and grouping and recreate categories); it can be taken over as part of the switch.
///
/// Switching back returns the account to MoneyMoney: the next upload replaces its rows, balance and bank details with
/// MoneyMoney's, so what was edited or synced in OpenBudget meanwhile (categories, notes, check marks, splits, rows the
/// bank delivered) is not kept. The structure takeover and the one-time import setting are not undone by it.
///
/// An account without a bank link can instead be kept as a manual account (<see cref="KeepManualAsync"/>): the same
/// rules apply (structure taken over, MoneyMoney's rows stay as editable history, uploads stop touching it), but nobody
/// refreshes it; its bookings are entered by hand. <see cref="ReturnToMoneyMoneyAsync"/> undoes it like switching back.
///
/// Only the owner of the account's source (who is also the bank login's owner) can switch or change the import setting.
/// </summary>
public sealed class AccountSwitchService
{
    private readonly IDbContextFactory<BudgetDbContext> _dbFactory;
    private readonly SystemDbContextFactory _system;
    private readonly AccessScopeProvider _access;
    private readonly BankTransactionStore _store;
    private readonly MoneyMoneyImportService? _importer;
    private readonly ChangeNotificationService _notifier;
    private readonly ChangeOrigin _origin;
    private readonly TimeProvider _clock;

    public AccountSwitchService(
        IDbContextFactory<BudgetDbContext> dbFactory, SystemDbContextFactory system, AccessScopeProvider access, BankTransactionStore store,
        ChangeNotificationService notifier, ChangeOrigin origin, TimeProvider clock,
        MoneyMoneyImportService? importer = null)
    {
        _dbFactory = dbFactory;
        _system = system;
        _access = access;
        _store = store;
        _importer = importer;
        _notifier = notifier;
        _origin = origin;
        _clock = clock;
    }

    /// <summary>Who updates the account; the bank link only for the source owner (editors and viewers see the provider).</summary>
    public async Task<AccountSwitchState?> GetAccountStateAsync(long accountId, CancellationToken ct = default)
    {
        var scope = await _access.GetAsync();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var account = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == accountId, ct);
        if (account == null) return null;
        if (!scope.OwnsSource(account.SourceId)) return new AccountSwitchState(account.Provider, false, null, Closed: account.Closed);
        var link = (await LinksAsync(db, l => l.LinkedAccountId == accountId, ct)).Where(l => !l.Created).Select(l => l.Link).FirstOrDefault();
        var source = await db.ImportSources.AsNoTracking().FirstAsync(s => s.Id == account.SourceId, ct);
        var linked = await db.BankLoginAccounts.AnyAsync(l => l.LinkedAccountId == accountId, ct);
        return new AccountSwitchState(account.Provider, true, link,
            CanKeepManual: account.Provider == AccountProviders.MoneyMoney && !linked && account.Type != GroupType,
            CanReturnToMoneyMoney: IsKeptManual(account, source) && !linked,
            ImportFinished: source.MoneyMoneyImportFinishedAt != null,
            Closed: account.Closed);
    }

    /// <summary>The switch state of an owned source: its linked accounts and whether the MoneyMoney import is finished.</summary>
    public async Task<SourceSwitchState> GetSourceStateAsync(int sourceId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        db.Scope?.RequireSourceOwner(sourceId);
        var source = await db.ImportSources.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sourceId, ct)
            ?? throw new AccessDeniedException("Source not found.");
        var accountIds = await db.Accounts.AsNoTracking().Where(a => a.SourceId == sourceId).Select(a => a.Id).ToListAsync(ct);
        var links = (await LinksAsync(db, l => l.LinkedAccountId != null && accountIds.Contains(l.LinkedAccountId.Value), ct))
            .Where(l => !l.Created).Select(l => l.Link).ToList();
        var linked = links.Select(l => l.AccountId).ToHashSet();
        linked.UnionWith(await db.BankLoginAccounts.AsNoTracking()
            .Where(l => l.LinkedAccountId != null && accountIds.Contains(l.LinkedAccountId.Value)).Select(l => l.LinkedAccountId!.Value).ToListAsync(ct));
        var all = (await db.Accounts.AsNoTracking().Where(a => a.SourceId == sourceId && a.Type != GroupType).ToListAsync(ct))
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var closed = all.Where(a => a.Closed).ToList();
        var candidates = all.Where(a => !a.Closed && !linked.Contains(a.Id)).ToList();
        var moneyMoney = candidates.Where(a => a.Provider == AccountProviders.MoneyMoney).ToList();
        var manual = candidates.Where(a => IsKeptManual(a, source)).ToList();
        var items = await ItemsAsync(db, [.. moneyMoney, .. manual, .. closed], ct);
        return new SourceSwitchState(sourceId, source.Name, source.MoneyMoneyImportFinishedAt, links, moneyMoney.Count)
        {
            MoneyMoneyAccounts = ByGroup(moneyMoney.Select(a => items[a.Id])),
            ManualAccounts = ByGroup(manual.Select(a => items[a.Id])),
            ClosedAccounts = ByGroup(closed.Select(a => items[a.Id])),
        };
    }

    /// <summary>Account type code of an account group row (never kept as a manual account).</summary>
    private const long GroupType = 8;

    /// <summary>
    /// A manual account that MoneyMoney delivered: it sits in a source MoneyMoney uploads to and has a MoneyMoney rowid
    /// (accounts OpenBudget or a connector creates live in other sources or in the connector half of the id space).
    /// </summary>
    private static bool IsKeptManual(Account account, ImportSource source) =>
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
        var sourceIds = accounts.Select(a => a.SourceId).Distinct().ToList();
        var groups = await db.AccountGroups.AsNoTracking().Where(g => sourceIds.Contains(g.SourceId))
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
            .Where(a => scope.OwnsSource(a.SourceId) && !linked.Contains(a.Id))
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var sourceIds = accounts.Select(a => a.SourceId).Distinct().ToList();
        var sources = await db.ImportSources.AsNoTracking().Where(s => sourceIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct);
        var items = await ItemsAsync(db, accounts, ct);
        return accounts.Select(a => new KeepManualPreview(items[a.Id], sources[a.SourceId].Name)).ToList();
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
        foreach (var a in accounts) scope.RequireSourceOwner(a.SourceId);
        var todo = accounts.Where(a => a.Provider == AccountProviders.MoneyMoney && a.Type != GroupType && !linked.Contains(a.Id)).ToList();
        if (todo.Count == 0 && accounts.Any(a => linked.Contains(a.Id) && a.Provider == AccountProviders.MoneyMoney))
            throw new InvalidOperationException("An account linked to a bank login is switched to that login; unlink it first to keep it as a manual account.");

        var changed = 0;
        foreach (var group in todo.GroupBy(a => a.SourceId))
        {
            string name;
            await using (var db = await _dbFactory.CreateDbContextAsync(ct))
                name = await db.ImportSources.Where(s => s.Id == group.Key).Select(s => s.Name).FirstAsync(ct);
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
            changed += _importer == null ? await ApplyAsync() : await _importer.WithSourceLockAsync(name, ApplyAsync, ct);
            _notifier.Publish(new DataChange(DataArea.ImportedData, SourceId: group.Key), _origin.Id);
        }
        if (todo.Count > 0) _notifier.Publish(new DataChange(DataArea.Sources), _origin.Id);
        return changed;
    }

    /// <summary>
    /// Marks the given accounts closed (or reopens them). Closed accounts keep their history and provider; the sidebar
    /// hides them by default and they count as handled in the switch overview. Source owner only. Returns how many changed.
    /// </summary>
    public async Task<int> SetClosedAsync(IReadOnlyCollection<long> accountIds, bool closed, CancellationToken ct = default)
    {
        var scope = await _access.GetAsync();
        if (scope.UserId == null) throw new AccessDeniedException("Not signed in.");
        var ids = accountIds.Distinct().ToList();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var accounts = await db.Accounts.Where(a => ids.Contains(a.Id)).ToListAsync(ct);
        if (accounts.Count < ids.Count) throw new AccessDeniedException("Account not found.");
        foreach (var a in accounts) scope.RequireSourceOwner(a.SourceId);
        var changed = accounts.Where(a => a.Closed != closed && a.Type != GroupType).ToList();
        foreach (var a in changed) a.Closed = closed;
        await db.SaveChangesAsync(ct);
        foreach (var sourceId in changed.Select(a => a.SourceId).Distinct())
            _notifier.Publish(new DataChange(DataArea.ImportedData, SourceId: sourceId), _origin.Id);
        if (changed.Count > 0) _notifier.Publish(new DataChange(DataArea.Sources), _origin.Id);
        return changed.Count;
    }

    /// <summary>
    /// Returns a kept manual account to MoneyMoney; the next upload replaces its rows, balance and bank details (bookings
    /// entered or edited meanwhile are not kept). Refused while the source's MoneyMoney import is finished.
    /// </summary>
    public async Task ReturnToMoneyMoneyAsync(long accountId, CancellationToken ct = default)
    {
        var scope = await _access.GetAsync();
        if (scope.UserId == null) throw new AccessDeniedException("Not signed in.");
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == accountId, ct)
            ?? throw new AccessDeniedException("Account not found.");
        scope.RequireSourceOwner(account.SourceId);
        if (account.Provider == AccountProviders.MoneyMoney) return;
        var source = await db.ImportSources.AsNoTracking().FirstAsync(s => s.Id == account.SourceId, ct);
        if (!IsKeptManual(account, source) || await db.BankLoginAccounts.AnyAsync(l => l.LinkedAccountId == accountId, ct))
            throw new InvalidOperationException("Only a manual account that came from MoneyMoney can return to it.");
        if (source.MoneyMoneyImportFinishedAt != null)
            throw new InvalidOperationException("The MoneyMoney import of this source is finished; resume it before returning an account to MoneyMoney.");
        account.Provider = AccountProviders.MoneyMoney;
        await db.SaveChangesAsync(ct);
        _notifier.Publish(new DataChange(DataArea.ImportedData, SourceId: account.SourceId), _origin.Id);
        _notifier.Publish(new DataChange(DataArea.Sources), _origin.Id);
    }

    /// <summary>Previews switching the given links (bank login accounts of the current user).</summary>
    public async Task<List<AccountSwitchPreview>> PreviewAsync(IEnumerable<int> loginAccountIds, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var ids = loginAccountIds.ToList();
        var result = new List<AccountSwitchPreview>();
        foreach (var (link, _, row) in await LinksAsync(db, l => ids.Contains(l.Id), ct))
        {
            db.Scope?.RequireSourceOwner(link.SourceId);
            var historyEnd = await PendingRefresh.NewestBookedDayAsync(db, link.AccountId, ct);
            var booked = await db.Transactions.CountAsync(t => t.AccountId == link.AccountId && t.Booked, ct);
            var refresh = link.Switched ? null : PendingRefresh.Deserialize(row.PendingRefreshJson);
            DateOnly? since = row.PendingSinceUtc is { } s ? DateOnly.FromDateTime(s) : null;
            if (refresh == null)
            {
                result.Add(new AccountSwitchPreview(link, historyEnd, null, booked, null, 0, 0, 0, 0, null));
                continue;
            }
            var p = await BankTransactionStore.PreviewAsync(db, link.AccountId, refresh, since, ct);
            result.Add(new AccountSwitchPreview(link, historyEnd, since, booked - p.Adopted, row.PendingRefreshAt,
                p.Adopted, p.Inserted, p.UnmatchedLocal, p.LocalPendingReplaced, refresh.Balance));
        }
        return result.OrderBy(r => r.Link.SourceName, StringComparer.Ordinal).ThenBy(r => r.Link.AccountName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>
    /// Switches the given links (those still MoneyMoney's; others are skipped). Returns how many accounts
    /// were switched.
    /// </summary>
    public async Task<int> SwitchAsync(IReadOnlyCollection<int> loginAccountIds, CancellationToken ct = default)
    {
        var scope = await _access.GetAsync();
        var userId = scope.UserId ?? throw new AccessDeniedException("Not signed in.");
        List<(AccountSwitchLink Link, bool Created, BankLoginAccount Row)> links;
        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
            links = (await LinksAsync(db, l => loginAccountIds.Contains(l.Id), ct)).Where(l => !l.Created && !l.Link.Switched).ToList();
        foreach (var sourceId in links.Select(l => l.Link.SourceId).Distinct())
            scope.RequireSourceOwner(sourceId);

        var switched = 0;
        foreach (var (link, _, _) in links)
        {
            var name = links.First(l => l.Link.SourceId == link.SourceId).Link.SourceName;
            var done = _importer == null
                ? await SwitchOneAsync(link, ct)
                : await _importer.WithSourceLockAsync(name, () => SwitchOneAsync(link, ct), ct);
            if (done) switched++;
            _notifier.Publish(new DataChange(DataArea.ImportedData, SourceId: link.SourceId), _origin.Id);
        }
        if (links.Count > 0)
        {
            _notifier.Publish(new DataChange(DataArea.Sources), _origin.Id);
            _notifier.Publish(new DataChange(DataArea.BankSync, UserId: userId), _origin.Id);
        }
        return switched;
    }

    private async Task<bool> SwitchOneAsync(AccountSwitchLink link, CancellationToken ct)
    {
        // The scoped checks ran above (owned source, own login); writes of bank rows need the unrestricted context, like a sync.
        await using var db = await _system.CreateDbContextAsync(ct);
        var row = await db.BankLoginAccounts.Include(l => l.BankLogin).FirstAsync(l => l.Id == link.LoginAccountId, ct);
        var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == link.AccountId, ct);
        if (account == null || row.LinkedAccountId != account.Id || account.Provider != AccountProviders.MoneyMoney) return false;

        var now = _clock.GetUtcNow().UtcDateTime;
        if (PendingRefresh.Deserialize(row.PendingRefreshJson) is { } refresh)
        {
            DateOnly? since = row.PendingSinceUtc is { } s ? DateOnly.FromDateTime(s) : null;
            await _store.ApplyAsync(db, account.Id, refresh, since, row.PendingRefreshAt ?? now, ct, markNew: false);
            row.LastSuccessAt = row.PendingRefreshAt ?? now;
            row.SyncFloorUtc = row.PendingSinceUtc;
        }
        else
        {
            // Nothing fetched yet: the first sync matches the bank's rows from MoneyMoney's newest booking on.
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
        return true;
    }

    /// <summary>Marks the source's MoneyMoney import finished (uploads are refused) or resumes it.</summary>
    public async Task SetImportFinishedAsync(int sourceId, bool finished, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        db.Scope?.RequireSourceOwner(sourceId);
        var source = await db.ImportSources.FirstOrDefaultAsync(s => s.Id == sourceId, ct)
            ?? throw new AccessDeniedException("Source not found.");
        source.MoneyMoneyImportFinishedAt = finished ? source.MoneyMoneyImportFinishedAt ?? _clock.GetUtcNow().UtcDateTime : null;
        await db.SaveChangesAsync(ct);
        _notifier.Publish(new DataChange(DataArea.Sources, SourceId: sourceId), _origin.Id);
    }

    /// <summary>The current user's links matching <paramref name="filter"/> (other users' logins are filtered out by the context).</summary>
    private static async Task<List<(AccountSwitchLink Link, bool Created, BankLoginAccount Row)>> LinksAsync(
        BudgetDbContext db, System.Linq.Expressions.Expression<Func<BankLoginAccount, bool>> filter, CancellationToken ct)
    {
        var rows = await db.BankLoginAccounts.AsNoTracking().Include(l => l.BankLogin)
            .Where(l => l.LinkedAccountId != null).Where(filter).ToListAsync(ct);
        var accountIds = rows.Select(r => r.LinkedAccountId!.Value).ToList();
        var accounts = await db.Accounts.AsNoTracking().Where(a => accountIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);
        var sourceIds = accounts.Values.Select(a => a.SourceId).Distinct().ToList();
        var sources = await db.ImportSources.AsNoTracking().Where(s => sourceIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        var result = new List<(AccountSwitchLink, bool, BankLoginAccount)>();
        foreach (var r in rows)
        {
            if (!accounts.TryGetValue(r.LinkedAccountId!.Value, out var a)) continue;
            var switched = a.Provider == r.BankLogin.ConnectorType;
            if (!switched && a.Provider != AccountProviders.MoneyMoney) continue;
            result.Add((new AccountSwitchLink(r.Id, r.BankLogin.DisplayName, a.Id, a.Name ?? r.Name, a.SourceId, sources.GetValueOrDefault(a.SourceId, "?"),
                switched, r.SwitchedAt, r.PendingRefreshAt), r.CreatedByConnector, r));
        }
        return result;
    }
}
