using Microsoft.EntityFrameworkCore;
using OpenBudget.Connectors;
using OpenBudget.Connectors.Sync;
using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>A bank login as the UI sees it. Never carries credentials or session data.</summary>
public sealed record BankLoginView(
    int Id,
    string ConnectorType,
    string ConnectorName,
    bool ConnectorAvailable,
    string DisplayName,
    RefreshInterval RefreshInterval,
    DateTime? LastSyncAt,
    string? LastError,
    bool CredentialsInvalid,
    string? Username);

public sealed record BankLoginAccountView(
    int Id,
    int BankLoginId,
    string RemoteId,
    string Name,
    RemoteAccountType Type,
    string? Iban,
    string? Currency,
    bool Closed,
    long? LinkedAccountId,
    string? LinkedAccountName,
    bool CreatedByConnector,
    DateTime? LastSuccessAt,
    string? LastError,
    bool StatementsEnabled,
    StatementsInterval StatementsInterval,
    DateTime? LastStatementAt);

/// <summary>An owned local account a remote account could be linked to.</summary>
public sealed record LinkCandidate(long AccountId, string Name, string? Iban, string SourceName, string Provider, bool SuggestedFor);

public sealed record StatementView(long Id, long? AccountId, string? AccountName, DateTime Date, string Name, long SizeBytes);

/// <summary>
/// Bank logins of the current user: create/edit/delete, link remote accounts to local ones, statements,
/// and starting syncs. All reads and writes go through the user-scoped context, so another user's
/// login is simply not found and cannot be changed (the context's filters and write guard enforce it);
/// sync entry points additionally pass the acting user to <see cref="BankSyncCoordinator"/>.
/// </summary>
public class BankLoginService
{
    private readonly IDbContextFactory<BudgetDbContext> _dbFactory;
    private readonly AccessScopeProvider _access;
    private readonly SourceService _sources;
    private readonly AccountService _accounts;
    private readonly BankSyncCoordinator _coordinator;
    private readonly ConnectorRegistry _registry;
    private readonly SecretsProtector _secrets;
    private readonly BankStatementStore _statementStore;
    private readonly ChangeNotificationService _notifier;
    private readonly ChangeOrigin _origin;

    public BankLoginService(
        IDbContextFactory<BudgetDbContext> dbFactory, AccessScopeProvider access, SourceService sources, AccountService accounts,
        BankSyncCoordinator coordinator, ConnectorRegistry registry, SecretsProtector secrets, BankStatementStore statementStore,
        ChangeNotificationService notifier, ChangeOrigin origin)
    {
        _dbFactory = dbFactory;
        _access = access;
        _sources = sources;
        _accounts = accounts;
        _coordinator = coordinator;
        _registry = registry;
        _secrets = secrets;
        _statementStore = statementStore;
        _notifier = notifier;
        _origin = origin;
    }

    /// <summary>False when no valid secrets key is configured: bank logins are then unavailable.</summary>
    public bool IsEnabled => _secrets.IsConfigured;

    public string? DisabledReason => _secrets.IsConfigured ? null : _secrets.ConfigurationProblem;

    public IReadOnlyList<ConnectorRegistration> GetConnectors() => _registry.GetAvailable();

    private async Task<string> RequireUserAsync()
    {
        var scope = await _access.GetAsync();
        return scope.UserId ?? throw new AccessDeniedException("Not signed in.");
    }

    private void RequireEnabled()
    {
        if (!IsEnabled) throw new InvalidOperationException("Bank logins are disabled: the secrets key is not configured.");
    }

    // ---- Logins ----

    public async Task<List<BankLoginView>> GetLoginsAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var logins = await db.BankLogins.AsNoTracking().OrderBy(l => l.DisplayName).ThenBy(l => l.Id).ToListAsync(ct);
        return logins.Select(ToView).ToList();
    }

    public async Task<BankLoginView?> GetLoginAsync(int id, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var login = await db.BankLogins.AsNoTracking().FirstOrDefaultAsync(l => l.Id == id, ct);
        return login == null ? null : ToView(login);
    }

    private BankLoginView ToView(BankLogin l)
    {
        var registration = _registry.Find(l.ConnectorType);
        var username = BankSyncRunner.DecodeCredentials(_secrets, l.EncryptedCredentials)?.Username;
        return new BankLoginView(l.Id, l.ConnectorType, registration?.DisplayName ?? l.ConnectorType, registration != null,
            l.DisplayName, l.RefreshInterval, l.LastSyncAt, l.LastError, l.CredentialsInvalid, username);
    }

    public async Task<BankLoginView> CreateLoginAsync(
        string connectorType, string displayName, string username, string secret, RefreshInterval interval = RefreshInterval.EveryTwoHours, CancellationToken ct = default)
    {
        RequireEnabled();
        var userId = await RequireUserAsync();
        var registration = _registry.Find(connectorType) ?? throw new ArgumentException("Unknown bank connector.", nameof(connectorType));
        if (string.IsNullOrWhiteSpace(username)) throw new ArgumentException("A username is required.", nameof(username));
        if (string.IsNullOrEmpty(secret)) throw new ArgumentException("A PIN or password is required.", nameof(secret));

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var login = new BankLogin
        {
            OwnerUserId = userId,
            ConnectorType = registration.ConnectorType,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? registration.DisplayName : displayName.Trim(),
            EncryptedCredentials = BankSyncRunner.EncodeCredentials(_secrets, username.Trim(), secret),
            RefreshInterval = interval,
        };
        db.BankLogins.Add(login);
        await db.SaveChangesAsync(ct);
        Changed(userId);
        return ToView(login);
    }

    /// <summary>Updates name and interval; a non-empty <paramref name="secret"/> and/or username re-enters the credentials and clears the "re-enter credentials" state.</summary>
    public async Task UpdateLoginAsync(
        int id, string displayName, RefreshInterval interval, string? username = null, string? secret = null, CancellationToken ct = default)
    {
        var userId = await RequireUserAsync();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var login = await db.BankLogins.FirstOrDefaultAsync(l => l.Id == id, ct) ?? throw new AccessDeniedException("Bank login not found.");
        if (!string.IsNullOrWhiteSpace(displayName)) login.DisplayName = displayName.Trim();
        login.RefreshInterval = interval;
        if (!string.IsNullOrWhiteSpace(username) || !string.IsNullOrEmpty(secret))
        {
            RequireEnabled();
            var current = BankSyncRunner.DecodeCredentials(_secrets, login.EncryptedCredentials);
            var newUser = string.IsNullOrWhiteSpace(username) ? current?.Username : username.Trim();
            var newSecret = string.IsNullOrEmpty(secret) ? current?.Secret : secret;
            if (newUser == null || newSecret == null)
                throw new ArgumentException("The stored credentials cannot be read; enter both username and PIN again.", nameof(secret));
            login.EncryptedCredentials = BankSyncRunner.EncodeCredentials(_secrets, newUser, newSecret);
            login.EncryptedSession = null;
            login.CredentialsInvalid = false;
        }
        await db.SaveChangesAsync(ct);
        Changed(userId);
    }

    /// <summary>
    /// Deletes the login with its credentials, session, remote-account list and downloaded statements (files
    /// included). Accounts and transactions already imported are kept: accounts that were taken over from MoneyMoney
    /// return to MoneyMoney (its next upload refills them), accounts the connector created become "manual".
    /// </summary>
    public async Task DeleteLoginAsync(int id, CancellationToken ct = default)
    {
        var userId = await RequireUserAsync();
        _coordinator.Cancel(userId, id);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var login = await db.BankLogins.Include(l => l.Accounts).FirstOrDefaultAsync(l => l.Id == id, ct)
            ?? throw new AccessDeniedException("Bank login not found.");

        foreach (var link in login.Accounts.Where(a => a.LinkedAccountId != null))
            await _accounts.SetProviderAsync(link.LinkedAccountId!.Value, link.CreatedByConnector ? AccountProviders.Manual : AccountProviders.MoneyMoney, ct);

        var statements = await db.BankStatements.Where(s => s.BankLoginId == id).ToListAsync(ct);
        db.BankStatements.RemoveRange(statements);
        db.BankLogins.Remove(login);
        await db.SaveChangesAsync(ct);
        foreach (var s in statements) _statementStore.DeleteFile(s.FilePath);
        Changed(userId);
        _notifier.Publish(new DataChange(DataArea.Sources), _origin.Id);
    }

    // ---- Remote accounts and links ----

    public async Task<List<BankLoginAccountView>> GetAccountsAsync(int loginId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.BankLoginAccounts.AsNoTracking().Where(a => a.BankLoginId == loginId).OrderBy(a => a.Name).ToListAsync(ct);
        var linkedIds = rows.Where(r => r.LinkedAccountId != null).Select(r => r.LinkedAccountId!.Value).ToList();
        var names = await db.Accounts.AsNoTracking().Where(a => linkedIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Name, ct);
        return rows.Select(r => new BankLoginAccountView(
            r.Id, r.BankLoginId, r.RemoteId, r.Name, r.Type, r.Iban, r.Currency, r.Closed, r.LinkedAccountId,
            r.LinkedAccountId is { } l ? names.GetValueOrDefault(l) : null, r.CreatedByConnector, r.LastSuccessAt, r.LastError,
            r.StatementsEnabled, r.StatementsInterval, r.LastStatementAt)).ToList();
    }

    /// <summary>Owned accounts of the user's own sources that a remote account may be linked to (MoneyMoney or detached ones not yet linked).</summary>
    public async Task<List<LinkCandidate>> GetLinkCandidatesAsync(int loginAccountId, CancellationToken ct = default)
    {
        var scope = await _access.GetAsync();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var remote = await db.BankLoginAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == loginAccountId, ct);
        if (remote == null) return [];
        var owned = scope.OwnedSourceIds;
        var taken = await db.BankLoginAccounts.AsNoTracking().Where(a => a.LinkedAccountId != null).Select(a => a.LinkedAccountId!.Value).ToListAsync(ct);
        var sourceNames = await db.ImportSources.AsNoTracking().Where(s => owned.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        var accounts = await db.Accounts.AsNoTracking()
            .Where(a => owned.Contains(a.SourceId) && (a.Provider == AccountProviders.MoneyMoney || a.Provider == AccountProviders.Manual) && a.Type != 8)
            .OrderBy(a => a.Name).ToListAsync(ct);
        var wanted = NormalizeIban(remote.Iban);
        // A depot only links to a depot, and a cash account only to a non-depot account: positions and
        // transactions are stored differently, so mixing them would show nonsense on both sides.
        bool remoteIsDepot = remote.Type == RemoteAccountType.Depot;
        return accounts.Where(a => !taken.Contains(a.Id) && IsDepot(a) == remoteIsDepot)
            .Select(a => new LinkCandidate(a.Id, a.Name ?? "?", a.Iban, sourceNames.GetValueOrDefault(a.SourceId, "?"), a.Provider,
                wanted.Length > 0 && NormalizeIban(a.Iban) == wanted))
            .ToList();
    }

    private static bool IsDepot(Account a) => a.Portfolio == 1 || a.Type == 7;

    public static string NormalizeIban(string? iban) => (iban ?? string.Empty).Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();

    /// <summary>Links a remote account to an existing local account and switches the account's provider to the connector.</summary>
    public async Task LinkAsync(int loginAccountId, long accountId, CancellationToken ct = default)
    {
        var userId = await RequireUserAsync();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var remote = await db.BankLoginAccounts.Include(a => a.BankLogin).FirstOrDefaultAsync(a => a.Id == loginAccountId, ct)
            ?? throw new AccessDeniedException("Bank account not found.");
        if (remote.Closed) throw new InvalidOperationException("The bank no longer lists this account.");
        if (remote.LinkedAccountId != null) throw new InvalidOperationException("Already linked; unlink it first.");
        var candidates = await GetLinkCandidatesAsync(loginAccountId, ct);
        if (candidates.All(c => c.AccountId != accountId))
            throw new InvalidOperationException("That account cannot be linked (not yours, already linked, or owned by another provider).");

        if (!await _accounts.SetProviderAsync(accountId, remote.BankLogin.ConnectorType, ct))
            throw new AccessDeniedException("Account not found.");
        remote.LinkedAccountId = accountId;
        remote.CreatedByConnector = false;
        remote.SyncFloorUtc = null;
        remote.LastSuccessAt = null;
        remote.LastError = null;
        await db.SaveChangesAsync(ct);
        Changed(userId);
        await PublishAccountSourceAsync(accountId);
    }

    /// <summary>
    /// Creates a new local account for a remote one in a dedicated source of this login ("bank-&lt;id&gt;", owned by the user;
    /// kept apart because a MoneyMoney upload replaces every account of the source it targets).
    /// </summary>
    public async Task<long> CreateAccountAsync(int loginAccountId, CancellationToken ct = default)
    {
        var userId = await RequireUserAsync();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var remote = await db.BankLoginAccounts.Include(a => a.BankLogin).FirstOrDefaultAsync(a => a.Id == loginAccountId, ct)
            ?? throw new AccessDeniedException("Bank account not found.");
        if (remote.Closed) throw new InvalidOperationException("The bank no longer lists this account.");
        if (remote.LinkedAccountId != null) throw new InvalidOperationException("Already linked; unlink it first.");
        var login = remote.BankLogin;

        var sourceName = $"bank-{login.Id}";
        var source = (await _sources.GetOwnedSourcesAsync(ct: ct)).FirstOrDefault(s => string.Equals(s.Name, sourceName, StringComparison.Ordinal))
            ?? await CreateSourceWithFallbackAsync(sourceName, ct);

        // The scope snapshot must know the new source before an account may be written into it.
        await using var writeDb = await _dbFactory.CreateDbContextAsync(ct);
        var maxId = await writeDb.Accounts.Where(a => a.SourceId == source.Id).MaxAsync(a => (long?)a.Id, ct);
        var account = new Account
        {
            Id = maxId is { } m ? m + 1 : SourceScope.Namespace(source.Id, 1),
            SourceId = source.Id,
            Name = remote.Name,
            Iban = remote.Iban,
            Currency = remote.Currency,
            Type = remote.Type switch
            {
                RemoteAccountType.Giro => 1,
                RemoteAccountType.Savings => 2,
                RemoteAccountType.CreditCard => 3,
                RemoteAccountType.Depot => 7,
                RemoteAccountType.Other => 0,
                _ => 0,
            },
            Portfolio = remote.Type == RemoteAccountType.Depot ? 1 : 0,
            Provider = login.ConnectorType,
        };
        writeDb.Accounts.Add(account);
        var link = await writeDb.BankLoginAccounts.FirstAsync(a => a.Id == loginAccountId, ct);
        link.LinkedAccountId = account.Id;
        link.CreatedByConnector = true;
        link.SyncFloorUtc = null;
        link.LastSuccessAt = null;
        link.LastError = null;
        await writeDb.SaveChangesAsync(ct);
        Changed(userId);
        _notifier.Publish(new DataChange(DataArea.ImportedData, SourceId: source.Id), _origin.Id);
        return account.Id;
    }

    private async Task<ImportSource> CreateSourceWithFallbackAsync(string name, CancellationToken ct)
    {
        var attempt = 0;
        while (true)
        {
            try
            {
                return await _sources.CreateSourceAsync(attempt == 0 ? name : $"{name}-{attempt + 1}", ct);
            }
            catch (InvalidOperationException) when (attempt++ < 5)
            {
                // name taken by someone else's source: try the next suffix
            }
        }
    }

    /// <summary>Unlinks: a taken-over account returns to MoneyMoney, a connector-created one becomes "manual". Data stays.</summary>
    public async Task UnlinkAsync(int loginAccountId, CancellationToken ct = default)
    {
        var userId = await RequireUserAsync();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var remote = await db.BankLoginAccounts.FirstOrDefaultAsync(a => a.Id == loginAccountId, ct)
            ?? throw new AccessDeniedException("Bank account not found.");
        if (remote.LinkedAccountId is not { } accountId) return;
        await _accounts.SetProviderAsync(accountId, remote.CreatedByConnector ? AccountProviders.Manual : AccountProviders.MoneyMoney, ct);
        remote.LinkedAccountId = null;
        remote.CreatedByConnector = false;
        remote.StatementsEnabled = false;
        remote.SyncFloorUtc = null;
        remote.LastSuccessAt = null;
        await db.SaveChangesAsync(ct);
        Changed(userId);
        await PublishAccountSourceAsync(accountId);
    }

    /// <summary>Switches statement download for an account on or off and sets its interval. Closed or unlinked accounts cannot be switched on.</summary>
    public async Task SetStatementsAsync(int loginAccountId, bool enabled, StatementsInterval interval, CancellationToken ct = default)
    {
        var userId = await RequireUserAsync();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var remote = await db.BankLoginAccounts.FirstOrDefaultAsync(a => a.Id == loginAccountId, ct)
            ?? throw new AccessDeniedException("Bank account not found.");
        if (enabled && (remote.Closed || remote.LinkedAccountId == null))
            throw new InvalidOperationException("Statements can only be switched on for linked accounts the bank still lists.");
        remote.StatementsEnabled = enabled;
        remote.StatementsInterval = interval;
        await db.SaveChangesAsync(ct);
        Changed(userId);
    }

    // ---- Statements ----

    /// <summary>All documents of one login, including those that belong to no account (owner only).</summary>
    public async Task<List<StatementView>> GetLoginStatementsAsync(int loginId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        if (!await db.BankLogins.AnyAsync(l => l.Id == loginId, ct)) return [];
        return await ToViewsAsync(db, db.BankStatements.AsNoTracking().Where(s => s.BankLoginId == loginId), ct);
    }

    /// <summary>Statements of an account; visible to everyone who can see the account (owner and shared-project viewers).</summary>
    public async Task<List<StatementView>> GetAccountStatementsAsync(long accountId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await ToViewsAsync(db, db.BankStatements.AsNoTracking().Where(s => s.AccountId == accountId), ct);
    }

    private static async Task<List<StatementView>> ToViewsAsync(BudgetDbContext db, IQueryable<BankStatement> query, CancellationToken ct)
    {
        var rows = await query.OrderByDescending(s => s.Date).ThenBy(s => s.Name).ToListAsync(ct);
        var ids = rows.Where(r => r.AccountId != null).Select(r => r.AccountId!.Value).Distinct().ToList();
        var names = await db.Accounts.AsNoTracking().Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Name, ct);
        return rows.Select(r => new StatementView(r.Id, r.AccountId, r.AccountId is { } a ? names.GetValueOrDefault(a) : null, r.Date, r.Name, r.SizeBytes)).ToList();
    }

    /// <summary>The file of a statement the current user may see, or null.</summary>
    public async Task<(string Path, string DownloadName)?> OpenStatementAsync(long statementId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await OpenStatementAsync(db, _statementStore, statementId, ct);
    }

    /// <summary>Same, for a context the caller scoped itself (the download endpoint).</summary>
    public static async Task<(string Path, string DownloadName)?> OpenStatementAsync(BudgetDbContext db, BankStatementStore store, long statementId, CancellationToken ct)
    {
        var s = await db.BankStatements.AsNoTracking().FirstOrDefaultAsync(x => x.Id == statementId, ct);
        if (s == null) return null;
        var path = store.Resolve(s.FilePath);
        if (!File.Exists(path)) return null;
        var safe = string.Concat(s.Name.Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.')).Trim();
        return (path, (string.IsNullOrEmpty(safe) ? "statement" : safe) + ".pdf");
    }

    // ---- Syncing ----

    public async Task<StartResult> SyncNowAsync(int loginId, CancellationToken ct = default) =>
        await _coordinator.SyncNowAsync(await RequireUserAsync(), loginId, ct);

    public async Task<StartResult> DownloadStatementsAsync(int loginId, int loginAccountId, CancellationToken ct = default) =>
        await _coordinator.DownloadStatementsAsync(await RequireUserAsync(), loginId, loginAccountId, ct);

    public async Task<int> SyncAllAsync(CancellationToken ct = default) =>
        await _coordinator.SyncAllAsync(await RequireUserAsync(), ct);

    /// <summary>Called when the owner loads a page: starts what is due under each login's own intervals.</summary>
    public async Task<int> StartDueAsync(CancellationToken ct = default)
    {
        var scope = await _access.GetAsync();
        return scope.UserId == null ? 0 : await _coordinator.StartDueAsync(scope.UserId, ct);
    }

    public async Task<IReadOnlyList<BankSyncStatus>> GetStatusesAsync()
    {
        var scope = await _access.GetAsync();
        return scope.UserId == null ? [] : _coordinator.GetStatuses(scope.UserId);
    }

    public async Task<bool> SubmitAnswerAsync(int loginId, Guid challengeId, string? value)
    {
        var scope = await _access.GetAsync();
        return scope.UserId != null && _coordinator.SubmitAnswer(scope.UserId, loginId, challengeId, value);
    }

    public async Task<bool> CancelSyncAsync(int loginId)
    {
        var scope = await _access.GetAsync();
        return scope.UserId != null && _coordinator.Cancel(scope.UserId, loginId);
    }

    private void Changed(string userId) => _notifier.Publish(new DataChange(DataArea.BankSync, UserId: userId), _origin.Id);

    private async Task PublishAccountSourceAsync(long accountId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var source = await db.Accounts.AsNoTracking().Where(a => a.Id == accountId).Select(a => (int?)a.SourceId).FirstOrDefaultAsync();
        if (source is { } s) _notifier.Publish(new DataChange(DataArea.ImportedData, SourceId: s), _origin.Id);
    }
}
