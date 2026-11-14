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
    string? Username,
    IReadOnlyList<string>? StoredExtraCredentials = null)
{
    /// <summary>What background syncs can do for this login (None: the switch is not offered).</summary>
    public SilentCapability Silent { get; init; }

    public bool BackgroundEnabled { get; init; }

    /// <summary>Why a background run stopped for the owner (a manual sync clears it), or null.</summary>
    public string? NeedsManualSync { get; init; }

    public DateTime? LastBackgroundSyncAt { get; init; }

    /// <summary>The bank code of a FinTS login, and whether background runs are allowed for that bank.</summary>
    public string? FinTsBlz { get; init; }

    public bool FinTsBankAllowed { get; init; }

    /// <summary>An order source (Amazon): its accounts are payment methods mapped to existing accounts.</summary>
    public bool OrderSource { get; init; }
}
// StoredExtraCredentials: keys of the extra credential values that are stored (the values themselves never leave the server).

/// <summary>
/// Of a bank's recent logged FinTS dialog inits: how many there were, how many went without a TAN request, and when the
/// bank last asked for a TAN (codes 0030/3955; 3076 or no SCA code at all count as no TAN).
/// </summary>
public sealed record FinTsEvidence(int Runs, int WithoutTan, DateTime? LastTanAskedAt)
{
    public const int Recent = 10;

    public static FinTsEvidence From(IEnumerable<(DateTime StartedAt, string? Codes)> runs)
    {
        int count = 0, without = 0;
        DateTime? lastAsked = null;
        foreach (var (at, codes) in runs)
        {
            count++;
            if (AsksForTan(codes)) { if (lastAsked == null || at > lastAsked) lastAsked = at; }
            else without++;
        }
        return new FinTsEvidence(count, without, lastAsked);
    }

    public static bool AsksForTan(string? codes) =>
        (codes ?? "").Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries).Any(c => c is "0030" or "3955");
}

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
    DateTime? LastStatementAt,
    DateTime? SwitchedAt = null);

/// <summary>An owned local account a remote account could be linked to.</summary>
public sealed record LinkCandidate(long AccountId, string Name, string? Iban, string Provider, bool SuggestedFor);

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
    private readonly AccountService _accounts;
    private readonly BankSyncCoordinator _coordinator;
    private readonly ConnectorRegistry _registry;
    private readonly SecretsProtector _secrets;
    private readonly BankStatementStore _statementStore;
    private readonly ChangeNotificationService _notifier;
    private readonly ChangeOrigin _origin;

    public BankLoginService(
        IDbContextFactory<BudgetDbContext> dbFactory, AccessScopeProvider access, AccountService accounts,
        BankSyncCoordinator coordinator, ConnectorRegistry registry, SecretsProtector secrets, BankStatementStore statementStore,
        ChangeNotificationService notifier, ChangeOrigin origin)
    {
        _dbFactory = dbFactory;
        _access = access;
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
        if (!IsEnabled) throw new InvalidOperationException("Connections are disabled: the secrets key is not configured.");
    }

    // ---- Logins ----

    public async Task<List<BankLoginView>> GetLoginsAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var logins = await db.BankLogins.AsNoTracking().OrderBy(l => l.DisplayName).ThenBy(l => l.Id).ToListAsync(ct);
        var revoked = await RevokedBanksAsync(db, ct);
        return logins.Select(l => ToView(l, revoked)).ToList();
    }

    /// <summary>The FinTS banks an admin revoked background syncs for (all others are allowed).</summary>
    internal static async Task<HashSet<string>> RevokedBanksAsync(BudgetDbContext db, CancellationToken ct) =>
        (await db.FinTsBankSettings.AsNoTracking().Where(b => !b.BackgroundEnabled).Select(b => b.Blz).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);

    public async Task<BankLoginView?> GetLoginAsync(int id, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var login = await db.BankLogins.AsNoTracking().FirstOrDefaultAsync(l => l.Id == id, ct);
        return login == null ? null : ToView(login, await RevokedBanksAsync(db, ct));
    }

    private BankLoginView ToView(BankLogin l, HashSet<string> revokedBanks)
    {
        var registration = _registry.Find(l.ConnectorType);
        var credentials = BankSyncRunner.DecodeCredentials(_secrets, l.EncryptedCredentials);
        return new BankLoginView(l.Id, l.ConnectorType, registration?.DisplayName ?? l.ConnectorType, registration != null,
            l.DisplayName, l.RefreshInterval, l.LastSyncAt, l.LastError, l.CredentialsInvalid, credentials?.Username,
            credentials?.Extra.Where(e => e.Value.Length > 0).Select(e => e.Key).ToList() ?? [])
        {
            Silent = registration?.SilentCapabilityFor(credentials) ?? SilentCapability.None,
            BackgroundEnabled = l.BackgroundEnabled,
            NeedsManualSync = l.NeedsManualSync,
            LastBackgroundSyncAt = l.LastBackgroundSyncAt,
            FinTsBlz = registration?.FinTsBlz,
            FinTsBankAllowed = registration?.FinTsBlz is { } blz && !revokedBanks.Contains(blz),
            OrderSource = registration?.OrderSource == true,
        };
    }

    // ---- Background syncs ----

    /// <summary>Switches background syncs of the user's login on or off.</summary>
    public async Task SetBackgroundAsync(int id, bool enabled, CancellationToken ct = default)
    {
        var userId = await RequireUserAsync();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var login = await db.BankLogins.FirstOrDefaultAsync(l => l.Id == id, ct) ?? throw new AccessDeniedException("Connection not found.");
        if (enabled && _registry.Find(login.ConnectorType)?.Silent is null or SilentCapability.None)
            throw new InvalidOperationException("This bank cannot be synced in the background.");
        login.BackgroundEnabled = enabled;
        login.BackgroundDefaulted = true;
        login.BackgroundFailures = 0;
        login.NextBackgroundAttemptAt = null;
        await db.SaveChangesAsync(ct);
        Changed(userId);
    }

    /// <summary>The most recent entries of the login's sync log (background runs and FinTS dialog outcomes), newest first.</summary>
    public async Task<List<BackgroundSyncRun>> GetRunsAsync(int loginId, int count = 20, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.BackgroundSyncRuns.AsNoTracking().Where(r => r.BankLoginId == loginId)
            .OrderByDescending(r => r.StartedAt).ThenByDescending(r => r.Id).Take(count).ToListAsync(ct);
    }

    /// <summary>Whether any of the user's logins waits for a manual sync after a background run stopped.</summary>
    public async Task<bool> AnyNeedsManualSyncAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.BankLogins.AnyAsync(l => l.BackgroundEnabled && l.NeedsManualSync != null, ct);
    }

    /// <summary>Whether the signed-in user may change the per-bank FinTS background setting.</summary>
    public async Task<bool> IsAdminAsync() => (await _access.GetAsync()).IsAdmin;

    /// <summary>
    /// What the logged dialog inits of a bank's logins (of all users; only counts and dates leave this method) say about
    /// whether the bank asks for a TAN in the background. Admins only.
    /// </summary>
    public async Task<FinTsEvidence> GetFinTsEvidenceAsync(string blz, CancellationToken ct = default)
    {
        if (!await IsAdminAsync()) throw new AccessDeniedException("Only an admin can see this.");
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var runs = await db.BackgroundSyncRuns.IgnoreQueryFilters().AsNoTracking().Where(r => r.Blz == blz && r.FinTsCodes != null)
            .OrderByDescending(r => r.StartedAt).Take(FinTsEvidence.Recent).Select(r => new { r.StartedAt, r.FinTsCodes }).ToListAsync(ct);
        return FinTsEvidence.From(runs.Select(r => (r.StartedAt, r.FinTsCodes)));
    }

    /// <summary>Allows or forbids background FinTS runs for a bank (admins only).</summary>
    public async Task SetFinTsBankAsync(string blz, bool enabled, CancellationToken ct = default)
    {
        var userId = await RequireUserAsync();
        if (!await IsAdminAsync()) throw new AccessDeniedException("Only an admin can change this.");
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var row = await db.FinTsBankSettings.FirstOrDefaultAsync(b => b.Blz == blz, ct);
        if (row == null)
        {
            row = new FinTsBankSetting { Blz = blz };
            db.FinTsBankSettings.Add(row);
        }
        row.BackgroundEnabled = enabled;
        await db.SaveChangesAsync(ct);
        Changed(userId);
    }

    public async Task<BankLoginView> CreateLoginAsync(
        string connectorType, string displayName, string username, string secret, RefreshInterval interval = RefreshInterval.EveryTwoHours,
        IReadOnlyDictionary<string, string>? extra = null, CancellationToken ct = default)
    {
        RequireEnabled();
        var userId = await RequireUserAsync();
        var registration = _registry.Find(connectorType) ?? throw new ArgumentException("Unknown bank connector.", nameof(connectorType));
        if (registration.Credentials == CredentialRequirement.None)
        {
            username = "";
            secret = "";
        }
        else
        {
            if (string.IsNullOrWhiteSpace(username)) throw new ArgumentException("A username is required.", nameof(username));
            if (string.IsNullOrEmpty(secret)) throw new ArgumentException("A PIN or password is required.", nameof(secret));
        }
        var extras = ApplyExtras(registration, new Dictionary<string, string>(StringComparer.Ordinal), extra);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var login = new BankLogin
        {
            OwnerUserId = userId,
            ConnectorType = registration.ConnectorType,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? registration.DisplayName : displayName.Trim(),
            EncryptedCredentials = BankSyncRunner.EncodeCredentials(_secrets, username.Trim(), secret, extras),
            RefreshInterval = interval,
            BackgroundEnabled = registration.SilentCapabilityFor(new ConnectorCredentials(username.Trim(), secret, extras)) == SilentCapability.Full,
            BackgroundDefaulted = true,
        };
        db.BankLogins.Add(login);
        await db.SaveChangesAsync(ct);
        Changed(userId);
        return ToView(login, []);
    }

    /// <summary>
    /// Updates name and interval; a non-empty <paramref name="secret"/>, username and/or extra value re-enters the
    /// credentials and clears the "re-enter credentials" state. In <paramref name="extra"/> a non-empty value replaces the
    /// stored one and an empty value removes it; keys left out keep theirs.
    /// </summary>
    public async Task UpdateLoginAsync(
        int id, string displayName, RefreshInterval interval, string? username = null, string? secret = null,
        IReadOnlyDictionary<string, string>? extra = null, CancellationToken ct = default)
    {
        var userId = await RequireUserAsync();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var login = await db.BankLogins.FirstOrDefaultAsync(l => l.Id == id, ct) ?? throw new AccessDeniedException("Connection not found.");
        if (!string.IsNullOrWhiteSpace(displayName)) login.DisplayName = displayName.Trim();
        login.RefreshInterval = interval;
        var registration = _registry.Find(login.ConnectorType);
        var needsCredentials = registration?.Credentials != CredentialRequirement.None;
        if (needsCredentials && (!string.IsNullOrWhiteSpace(username) || !string.IsNullOrEmpty(secret) || extra is { Count: > 0 }))
        {
            RequireEnabled();
            var current = BankSyncRunner.DecodeCredentials(_secrets, login.EncryptedCredentials);
            var newUser = string.IsNullOrWhiteSpace(username) ? current?.Username : username.Trim();
            var newSecret = string.IsNullOrEmpty(secret) ? current?.Secret : secret;
            if (newUser == null || newSecret == null)
                throw new ArgumentException("The stored credentials cannot be read; enter both username and PIN again.", nameof(secret));
            var extras = new Dictionary<string, string>(current?.Extra ?? new Dictionary<string, string>(StringComparer.Ordinal), StringComparer.Ordinal);
            if (registration != null) ApplyExtras(registration, extras, extra);
            login.EncryptedCredentials = BankSyncRunner.EncodeCredentials(_secrets, newUser, newSecret, extras);
            login.EncryptedSession = null;
            login.CredentialsInvalid = false;
        }
        await db.SaveChangesAsync(ct);
        Changed(userId);
    }

    /// <summary>Checks and applies entered extra credential values: unknown keys are ignored, empty values remove the stored one.</summary>
    private static Dictionary<string, string> ApplyExtras(
        ConnectorRegistration registration, Dictionary<string, string> stored, IReadOnlyDictionary<string, string>? entered)
    {
        if (entered == null || registration.Credentials == CredentialRequirement.None) return stored;
        foreach (var field in registration.ExtraCredentials)
        {
            if (!entered.TryGetValue(field.Key, out var value)) continue;
            if (string.IsNullOrWhiteSpace(value))
            {
                stored.Remove(field.Key);
                continue;
            }
            try
            {
                stored[field.Key] = field.Accept(value);
            }
            catch (FormatException ex)
            {
                throw new ArgumentException(field.Label + ": " + ex.Message, nameof(entered), ex);
            }
        }
        return stored;
    }

    /// <summary>
    /// Deletes the login with its credentials, session, remote-account list and downloaded statements (files
    /// included). Accounts and transactions already imported are kept; the linked accounts become manual accounts.
    /// </summary>
    public async Task DeleteLoginAsync(int id, CancellationToken ct = default)
    {
        var userId = await RequireUserAsync();
        _coordinator.Cancel(userId, id);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var login = await db.BankLogins.Include(l => l.Accounts).FirstOrDefaultAsync(l => l.Id == id, ct)
            ?? throw new AccessDeniedException("Connection not found.");

        foreach (var link in login.Accounts.Where(a => a.LinkedAccountId != null))
            await _accounts.SetProviderAsync(link.LinkedAccountId!.Value, AccountProviders.Manual, ct);

        var statements = await db.BankStatements.Where(s => s.BankLoginId == id).ToListAsync(ct);
        db.BankStatements.RemoveRange(statements);
        db.BankStatementAliases.RemoveRange(await db.BankStatementAliases.Where(a => a.BankLoginId == id).ToListAsync(ct));
        db.BankLogins.Remove(login);
        await db.SaveChangesAsync(ct);
        foreach (var s in statements) _statementStore.DeleteFile(s.FilePath);
        Changed(userId);
        _notifier.Publish(new DataChange(DataArea.ImportState), _origin.Id);
    }

    // ---- Remote accounts and links ----

    public async Task<List<BankLoginAccountView>> GetAccountsAsync(int loginId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.BankLoginAccounts.AsNoTracking().Where(a => a.BankLoginId == loginId).OrderBy(a => a.Name).ToListAsync(ct);
        var linkedIds = rows.Where(r => r.LinkedAccountId != null).Select(r => r.LinkedAccountId!.Value).ToList();
        var linked = await db.Accounts.AsNoTracking().Where(a => linkedIds.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, a => new { a.Name }, ct);
        return rows.Select(r =>
        {
            var account = r.LinkedAccountId is { } l ? linked.GetValueOrDefault(l) : null;
            return new BankLoginAccountView(
                r.Id, r.BankLoginId, r.RemoteId, r.Name, r.Type, r.Iban, r.Currency, r.Closed, r.LinkedAccountId,
                account?.Name, r.CreatedByConnector, r.LastSuccessAt, r.LastError,
                r.StatementsEnabled, r.StatementsInterval, r.LastStatementAt,
                r.SwitchedAt);
        }).ToList();
    }

    /// <summary>The user's own accounts that a remote account may be linked to (manual ones not yet linked, imported ones included).</summary>
    public async Task<List<LinkCandidate>> GetLinkCandidatesAsync(int loginAccountId, CancellationToken ct = default)
    {
        var scope = await _access.GetAsync();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var remote = await db.BankLoginAccounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == loginAccountId, ct);
        if (remote == null) return [];
        var owner = scope.UserId;
        var taken = await db.BankLoginAccounts.AsNoTracking().Where(a => a.LinkedAccountId != null).Select(a => a.LinkedAccountId!.Value).ToListAsync(ct);
        var accounts = await db.Accounts.AsNoTracking()
            .Where(a => a.OwnerUserId == owner && a.Provider == AccountProviders.Manual && a.Type != 8 && !a.Closed)
            .OrderBy(a => a.Name).ToListAsync(ct);
        var wanted = NormalizeIban(remote.Iban);
        // A depot only links to a depot, and a cash account only to a non-depot account: positions and
        // transactions are stored differently, so mixing them would show nonsense on both sides.
        bool remoteIsDepot = remote.Type == RemoteAccountType.Depot;
        var eligible = accounts.Where(a => !taken.Contains(a.Id) && IsDepot(a) == remoteIsDepot).ToList();
        // The suggestion is the account with the same IBAN; without one, the account with the same account number
        // (an IBAN's embedded account number counts, a subaccount suffix does not, leading zeros are ignored).
        Func<Account, bool> suggested = a => wanted.Length > 0 && NormalizeIban(a.Iban) == wanted;
        if (!eligible.Any(suggested))
        {
            var number = AccountNumber(string.IsNullOrEmpty(remote.Iban) ? remote.RemoteId : remote.Iban);
            suggested = a => number.Length > 0 && AccountNumber(a.Iban) == number;
        }
        return eligible.Select(a => new LinkCandidate(a.Id, a.Name ?? "?", a.Iban, a.Provider, suggested(a))).ToList();
    }

    /// <summary>
    /// The account number in an IBAN (a German IBAN's last ten digits) or in a plain account number (without a
    /// "-subaccount" suffix), without spaces and leading zeros; empty when there is none.
    /// </summary>
    public static string AccountNumber(string? value)
    {
        var v = NormalizeIban(value);
        if (v.Length == 22 && v.StartsWith("DE", StringComparison.Ordinal) && v[2..].All(char.IsAsciiDigit)) v = v[^10..];
        else if (v.IndexOf('-', StringComparison.Ordinal) is > 0 and var dash) v = v[..dash];
        if (v.Length == 0 || !v.All(char.IsAsciiDigit)) return string.Empty;
        return v.TrimStart('0');
    }

    private static bool IsDepot(Account a) => a.Portfolio == 1 || a.Type == 7;

    public static string NormalizeIban(string? iban) => (iban ?? string.Empty).Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();

    /// <summary>
    /// Links a remote account to an existing manual account, which is the connector's from then on. The account keeps its
    /// bookings (an imported account its MoneyMoney history); the first sync matches the bank's rows from its newest booking on.
    /// </summary>
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
        remote.SwitchedAt = DateTime.UtcNow;
        remote.CreatedByConnector = false;
        remote.SyncFloorUtc = null;
        remote.LastSuccessAt = null;
        remote.LastError = null;
        await db.SaveChangesAsync(ct);
        Changed(userId);
        await PublishAccountSourceAsync(accountId);
    }

    /// <summary>
    /// Creates a new local account of the user for a remote one, synced by the login's connector and assigned to the
    /// default project for new accounts. Its id comes from the connector half of the user's id space (see <see cref="RowIds"/>).
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

        await using var writeDb = await _dbFactory.CreateDbContextAsync(ct);
        var account = new Account
        {
            Id = await RowIds.NextConnectorAccountIdAsync(writeDb, userId, ct),
            OwnerUserId = userId,
            Name = remote.Name,
            Iban = remote.Iban,
            Currency = remote.Currency,
            Type = remote.Type switch
            {
                RemoteAccountType.Giro => 1,
                RemoteAccountType.Savings => 2,
                RemoteAccountType.CreditCard => 3,
                RemoteAccountType.Depot => 7,
                RemoteAccountType.Loan => 4,
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
        // A new account lands in the user's default project for new accounts, if they set one.
        if (await DefaultProjectAssigner.AssignNewAccountsAsync(writeDb, userId, [account.Id], ct) > 0)
            _notifier.Publish(new DataChange(DataArea.Projects), _origin.Id);
        Changed(userId);
        _notifier.Publish(new DataChange(DataArea.ImportedData, OwnerUserId: userId), _origin.Id);
        return account.Id;
    }

    /// <summary>Unlinks: the account becomes a manual account. Data stays.</summary>
    public async Task UnlinkAsync(int loginAccountId, CancellationToken ct = default)
    {
        var userId = await RequireUserAsync();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var remote = await db.BankLoginAccounts.FirstOrDefaultAsync(a => a.Id == loginAccountId, ct)
            ?? throw new AccessDeniedException("Bank account not found.");
        if (remote.LinkedAccountId is not { } accountId) return;
        await _accounts.SetProviderAsync(accountId, AccountProviders.Manual, ct);
        remote.LinkedAccountId = null;
        remote.CreatedByConnector = false;
        remote.StatementsEnabled = false;
        remote.SyncFloorUtc = null;
        remote.LastSuccessAt = null;
        remote.SwitchedAt = null;
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

    /// <summary>
    /// Documents of the bank logins this account is linked to that belong to no account (a tax certificate from a
    /// broker's mailbox, say). Owner only: the logins are looked up through the user's scoped context.
    /// </summary>
    public async Task<List<StatementView>> GetAccountLoginDocumentsAsync(long accountId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var loginIds = await db.BankLoginAccounts.AsNoTracking()
            .Where(a => a.LinkedAccountId == accountId)
            .Select(a => a.BankLoginId)
            .Where(id => db.BankLogins.Any(l => l.Id == id))
            .Distinct()
            .ToListAsync(ct);
        if (loginIds.Count == 0) return [];
        return await ToViewsAsync(db, db.BankStatements.AsNoTracking().Where(s => s.AccountId == null && s.BankLoginId != null && loginIds.Contains(s.BankLoginId.Value)), ct);
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

    /// <summary>Refreshes only the given linked accounts (<see cref="BankLoginAccount.Id"/>s) of one of the user's logins.</summary>
    public async Task<StartResult> RefreshAccountsAsync(int loginId, IReadOnlyCollection<int> loginAccountIds, CancellationToken ct = default) =>
        await _coordinator.RefreshAccountsAsync(await RequireUserAsync(), loginId, loginAccountIds, ct);

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
        var source = await db.Accounts.AsNoTracking().Where(a => a.Id == accountId).Select(a => a.OwnerUserId).FirstOrDefaultAsync();
        if (source is { } s) _notifier.Publish(new DataChange(DataArea.ImportedData, OwnerUserId: s), _origin.Id);
    }
}
