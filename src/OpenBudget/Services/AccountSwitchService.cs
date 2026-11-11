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
    decimal? BankBalance,
    bool StructureTakenOver);

/// <summary>The account settings' view: who updates the account, and (for the source owner) its bank link.</summary>
public sealed record AccountSwitchState(string Provider, bool CanManage, AccountSwitchLink? Link);

/// <summary>A source's switch progress and one-time import setting (owner only).</summary>
public sealed record SourceSwitchState(
    int SourceId,
    string SourceName,
    bool StructureTakenOver,
    DateTime? ImportFinishedAt,
    IReadOnlyList<AccountSwitchLink> Links,
    int MoneyMoneyAccountsWithoutLink);

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
/// Only the owner of the account's source (who is also the bank login's owner) can switch or change the import setting.
/// </summary>
public sealed class AccountSwitchService
{
    private readonly IDbContextFactory<BudgetDbContext> _dbFactory;
    private readonly SystemDbContextFactory _system;
    private readonly AccessScopeProvider _access;
    private readonly BankTransactionStore _store;
    private readonly StructureTakeoverService _takeover;
    private readonly MoneyMoneyImportService? _importer;
    private readonly ChangeNotificationService _notifier;
    private readonly ChangeOrigin _origin;
    private readonly TimeProvider _clock;

    public AccountSwitchService(
        IDbContextFactory<BudgetDbContext> dbFactory, SystemDbContextFactory system, AccessScopeProvider access, BankTransactionStore store,
        StructureTakeoverService takeover, ChangeNotificationService notifier, ChangeOrigin origin, TimeProvider clock,
        MoneyMoneyImportService? importer = null)
    {
        _dbFactory = dbFactory;
        _system = system;
        _access = access;
        _store = store;
        _takeover = takeover;
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
        if (!scope.OwnsSource(account.SourceId)) return new AccountSwitchState(account.Provider, false, null);
        var link = (await LinksAsync(db, l => l.LinkedAccountId == accountId, ct)).Where(l => !l.Created).Select(l => l.Link).FirstOrDefault();
        return new AccountSwitchState(account.Provider, true, link);
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
        var unlinked = await db.Accounts.AsNoTracking()
            .CountAsync(a => a.SourceId == sourceId && a.Provider == AccountProviders.MoneyMoney && !linked.Contains(a.Id), ct);
        return new SourceSwitchState(sourceId, source.Name, source.StructureTakenOverAt != null, source.MoneyMoneyImportFinishedAt, links, unlinked);
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
            var takenOver = await db.ImportSources.AnyAsync(x => x.Id == link.SourceId && x.StructureTakenOverAt != null, ct);
            var booked = await db.Transactions.CountAsync(t => t.AccountId == link.AccountId && t.Booked, ct);
            var refresh = link.Switched ? null : PendingRefresh.Deserialize(row.PendingRefreshJson);
            DateOnly? since = row.PendingSinceUtc is { } s ? DateOnly.FromDateTime(s) : null;
            if (refresh == null)
            {
                result.Add(new AccountSwitchPreview(link, historyEnd, null, booked, null, 0, 0, 0, 0, null, takenOver));
                continue;
            }
            var p = await BankTransactionStore.PreviewAsync(db, link.AccountId, refresh, since, ct);
            result.Add(new AccountSwitchPreview(link, historyEnd, since, booked - p.Adopted, row.PendingRefreshAt,
                p.Adopted, p.Inserted, p.UnmatchedLocal, p.LocalPendingReplaced, refresh.Balance, takenOver));
        }
        return result.OrderBy(r => r.Link.SourceName, StringComparer.Ordinal).ThenBy(r => r.Link.AccountName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>
    /// Switches the given links (those still MoneyMoney's; others are skipped). Refused while the structure of an
    /// affected source is MoneyMoney's unless <paramref name="takeOverStructure"/> takes it over first. Returns how many
    /// accounts were switched.
    /// </summary>
    public async Task<int> SwitchAsync(IReadOnlyCollection<int> loginAccountIds, bool takeOverStructure, CancellationToken ct = default)
    {
        var scope = await _access.GetAsync();
        var userId = scope.UserId ?? throw new AccessDeniedException("Not signed in.");
        List<(AccountSwitchLink Link, bool Created, BankLoginAccount Row)> links;
        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
            links = (await LinksAsync(db, l => loginAccountIds.Contains(l.Id), ct)).Where(l => !l.Created && !l.Link.Switched).ToList();
        foreach (var sourceId in links.Select(l => l.Link.SourceId).Distinct())
        {
            scope.RequireSourceOwner(sourceId);
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var source = await db.ImportSources.AsNoTracking().FirstAsync(s => s.Id == sourceId, ct);
            if (source.StructureTakenOverAt != null) continue;
            if (!takeOverStructure)
                throw new InvalidOperationException($"The structure of source '{source.Name}' is still MoneyMoney's; take it over to switch its accounts.");
            await _takeover.TakeOverAsync(sourceId, ct);
        }

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

    /// <summary>
    /// Returns a switched account to MoneyMoney; the next upload replaces its rows. Refused while the source's MoneyMoney
    /// import is finished (no upload would come).
    /// </summary>
    public async Task SwitchBackAsync(int loginAccountId, CancellationToken ct = default)
    {
        var scope = await _access.GetAsync();
        var userId = scope.UserId ?? throw new AccessDeniedException("Not signed in.");
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var (link, created, _) = (await LinksAsync(db, l => l.Id == loginAccountId, ct)).FirstOrDefault();
        if (link == null) throw new AccessDeniedException("Bank account not found.");
        scope.RequireSourceOwner(link.SourceId);
        if (created) throw new InvalidOperationException("This account was created by the bank connection; it has no MoneyMoney counterpart.");
        if (!link.Switched) return;
        if (await db.ImportSources.AnyAsync(s => s.Id == link.SourceId && s.MoneyMoneyImportFinishedAt != null, ct))
            throw new InvalidOperationException("The MoneyMoney import of this source is finished; resume it before switching an account back.");

        await using var sys = await _system.CreateDbContextAsync(ct);
        var row = await sys.BankLoginAccounts.FirstAsync(l => l.Id == loginAccountId, ct);
        var account = await sys.Accounts.FirstAsync(a => a.Id == link.AccountId, ct);
        account.Provider = AccountProviders.MoneyMoney;
        row.SwitchedAt = null;
        row.LastSuccessAt = null;
        row.SyncFloorUtc = null;
        row.PendingRefreshJson = null;
        row.PendingRefreshAt = null;
        row.PendingSinceUtc = null;
        await sys.SaveChangesAsync(ct);
        _notifier.Publish(new DataChange(DataArea.ImportedData, SourceId: link.SourceId), _origin.Id);
        _notifier.Publish(new DataChange(DataArea.Sources), _origin.Id);
        _notifier.Publish(new DataChange(DataArea.BankSync, UserId: userId), _origin.Id);
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
