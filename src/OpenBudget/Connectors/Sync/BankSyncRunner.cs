using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenBudget.Browser;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.Connectors.Sync;

/// <summary>What one sync should do. Statement accounts are <see cref="BankLoginAccount.Id"/>s; null = every account with statements switched on.</summary>
public sealed record SyncRequest(int LoginId, bool Transactions, bool Statements, IReadOnlyCollection<int>? StatementAccounts = null);

/// <summary>Where the runner reports progress and asks the user for input.</summary>
public interface ISyncSink
{
    void Report(string message);

    /// <summary>Asks the user; null when nobody answered in time (the step then fails).</summary>
    Task<ChallengeAnswer?> AskAsync(ConnectorChallenge challenge, CancellationToken ct);

    /// <summary>A web login is open: show the remote browser of <paramref name="sessionId"/> to the owner until <see cref="HideWebLogin"/>.</summary>
    void ShowWebLogin(ConnectorChallenge challenge, string sessionId, DateTime expiresAt, WebLoginMode mode = WebLoginMode.Interactive)
    {
    }

    void HideWebLogin()
    {
    }
}

public sealed record SyncOutcome(bool Success, string? Error, IReadOnlyCollection<int> AffectedSourceIds);

/// <summary>
/// Runs one sync of one bank login: login (with challenges), list accounts, refresh linked accounts,
/// fetch statements, log out and persist the session. Uses an unrestricted context (a sync runs in
/// the background, outside any circuit) and therefore re-checks that every account it writes to
/// lives in a source owned by the login's owner.
/// </summary>
public sealed class BankSyncRunner
{
    public const string CredentialsPurpose = "bank-login-credentials";
    public const string SessionPurpose = "bank-login-session";

    private readonly SystemDbContextFactory _system;
    private readonly ConnectorRegistry _registry;
    private readonly SecretsProtector _secrets;
    private readonly BankTransactionStore _transactions;
    private readonly BankStatementStore _statements;
    private readonly TimeProvider _clock;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger<BankSyncRunner> _logger;
    private readonly Func<BankHttpClient> _httpFactory;
    private readonly IWebLoginService? _webLogins;

    /// <summary>The login-wide state a challenge may need: whose browser to open and which cookie jar to fill.</summary>
    private sealed record SyncContext(string OwnerUserId, BankHttpClient Http);

    /// <summary>Test seam: <paramref name="httpFactory"/> creates the per-sync HTTP client (default: a real one).</summary>
    public BankSyncRunner(
        SystemDbContextFactory system, ConnectorRegistry registry, SecretsProtector secrets,
        BankTransactionStore transactions, BankStatementStore statements, TimeProvider clock, ILoggerFactory loggers,
        Func<BankHttpClient>? httpFactory = null, IWebLoginService? webLogins = null)
    {
        _webLogins = webLogins;
        _httpFactory = httpFactory ?? (() => new BankHttpClient());
        _system = system;
        _registry = registry;
        _secrets = secrets;
        _transactions = transactions;
        _statements = statements;
        _clock = clock;
        _loggers = loggers;
        _logger = loggers.CreateLogger<BankSyncRunner>();
    }

    public static string EncodeCredentials(SecretsProtector secrets, string username, string secret) =>
        secrets.Encrypt(JsonSerializer.Serialize(new CredentialsDto(username, secret)), CredentialsPurpose);

    public static ConnectorCredentials? DecodeCredentials(SecretsProtector secrets, string? payload)
    {
        if (!secrets.TryDecrypt(payload, CredentialsPurpose, out var json)) return null;
        try
        {
            var dto = JsonSerializer.Deserialize<CredentialsDto>(json);
            return dto == null ? null : new ConnectorCredentials(dto.Username, dto.Secret);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record CredentialsDto(string Username, string Secret);

    public async Task<SyncOutcome> RunAsync(SyncRequest request, ISyncSink sink, CancellationToken ct)
    {
        var affected = new HashSet<int>();
        await using var db = await _system.CreateDbContextAsync(ct);
        var login = await db.BankLogins.Include(l => l.Accounts).FirstOrDefaultAsync(l => l.Id == request.LoginId, ct);
        if (login == null) return new SyncOutcome(false, "Bank login not found.", affected);

        var errors = new List<string>();
        var attempted = _clock.GetUtcNow().UtcDateTime;
        bool loggedIn = false;
        bool credentialsInvalid = false;
        IBankConnector? connector = null;
        using var http = _httpFactory();
        var context = new SyncContext(login.OwnerUserId, http);

        try
        {
            if (!_secrets.IsConfigured) throw new ConnectorException("The secrets key is not configured.");
            var credentials = DecodeCredentials(_secrets, login.EncryptedCredentials);
            if (credentials == null)
            {
                credentialsInvalid = true;
                throw new ConnectorException("The stored credentials cannot be read (the secrets key changed?). Please re-enter them.");
            }
            var registration = _registry.Find(login.ConnectorType)
                ?? throw new ConnectorException($"The connector '{login.ConnectorType}' is not available.");

            ConnectorSession? stored = null;
            if (_secrets.TryDecrypt(login.EncryptedSession, SessionPurpose, out var sessionJson))
            {
                try { stored = JsonSerializer.Deserialize<ConnectorSession>(sessionJson); }
                catch (JsonException) { stored = null; }
            }
            if (stored != null) http.ImportCookies(stored.Cookies);

            connector = registration.Create(new ConnectorContext(http, _clock, _loggers.CreateLogger(registration.ConnectorType)));

            sink.Report("Logging in");
            var loginStep = await DriveAsync(a => connector.LoginAsync(credentials, stored, a, ct), sink, context, ct);
            if (loginStep.Status != StepStatus.Done)
            {
                credentialsInvalid = loginStep.CredentialsRejected;
                throw new ConnectorException(loginStep.FailureReason ?? "Login failed.");
            }
            loggedIn = true;

            sink.Report("Listing accounts");
            var remoteAccounts = await connector.ListAccountsAsync(ct);
            _logger.LogInformation("Bank login {LoginId}: the bank lists {Count} accounts.", login.Id, remoteAccounts.Count);
            var vanished = UpsertAccounts(login, remoteAccounts);
            foreach (var link in vanished)
            {
                // a linked account the bank stopped listing: say so instead of silently skipping it
                _logger.LogWarning("Bank login {LoginId}: the linked account {LinkId} is no longer listed by the bank.", login.Id, link.Id);
                await RecordAccountErrorAsync(db, link, "The bank no longer lists this account.", errors, ct);
            }
            await db.SaveChangesAsync(ct);

            if (request.Transactions)
                await RefreshAccountsAsync(db, login, connector, remoteAccounts, sink, context, errors, affected, ct);
            if (request.Statements)
                await FetchStatementsAsync(db, login, connector, remoteAccounts, request, sink, context, errors, affected, ct);
        }
        catch (ConnectorException ex)
        {
            _logger.LogWarning(ex, "Sync of bank login {LoginId} failed: {Error}", login.Id, ex.Message);
            errors.Add(ex.Message);
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            _logger.LogInformation(ex, "Sync of bank login {LoginId} was cancelled.", login.Id);
            errors.Add("The sync was cancelled.");
        }
        catch (Exception ex)
        {
            // Never echo exception text to the user: it could carry bank responses. Details go to the log (no secrets there either).
            _logger.LogError(ex, "Sync of bank login {LoginId} failed unexpectedly.", login.Id);
            errors.Add("Unexpected error while syncing; see the server log.");
        }
        finally
        {
            ConnectorSession? finalSession = null;
            if (connector != null && loggedIn)
            {
                try { finalSession = await connector.LogoutAsync(CancellationToken.None); }
                catch (Exception ex) { _logger.LogWarning(ex, "Logout of bank login {LoginId} failed.", login.Id); }
            }
            await FinishAsync(db, login, request, finalSession, http, errors, credentialsInvalid, attempted);
        }

        if (errors.Count == 0) _logger.LogInformation("Sync of bank login {LoginId} finished.", login.Id);
        else _logger.LogWarning("Sync of bank login {LoginId} finished with errors: {Errors}", login.Id, string.Join(" ", errors));
        return new SyncOutcome(errors.Count == 0, errors.Count == 0 ? null : string.Join(" ", errors), affected);
    }

    private async Task FinishAsync(
        BudgetDbContext db, BankLogin login, SyncRequest request, ConnectorSession? session, BankHttpClient http,
        List<string> errors, bool credentialsInvalid, DateTime attempted)
    {
        try
        {
            if (session != null && _secrets.IsConfigured)
            {
                session.Cookies = http.ExportCookies().ToList();
                login.EncryptedSession = _secrets.Encrypt(JsonSerializer.Serialize(session), SessionPurpose);
            }
            if (request.Transactions) login.LastSyncAt = attempted;
            if (request.Transactions || errors.Count > 0)
                login.LastError = errors.Count == 0 ? null : Truncate(string.Join(" ", errors), 1000);
            if (credentialsInvalid) login.CredentialsInvalid = true;
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not record the result of the sync of bank login {LoginId}.", login.Id);
        }
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];

    private async Task<ConnectorStep<T>> DriveAsync<T>(
        Func<ChallengeAnswer?, Task<ConnectorStep<T>>> call, ISyncSink sink, SyncContext context, CancellationToken ct)
    {
        ChallengeAnswer? answer = null;
        while (true)
        {
            var step = await call(answer);
            if (step.Status != StepStatus.Challenge) return step;
            if (step.Challenge!.Kind == ChallengeKind.WebLogin)
            {
                var (webAnswer, rejected) = await RunWebLoginAsync(step.Challenge, sink, context, ct);
                if (rejected != null) return ConnectorStep.Fail<T>(rejected, credentialsRejected: true);
                answer = webAnswer;
            }
            else
            {
                answer = await sink.AskAsync(step.Challenge, ct);
            }
            if (answer == null) return ConnectorStep.Fail<T>("No answer was given in time; the request expired.");
        }
    }

    /// <summary>
    /// Lets the owner sign in on the bank's page in an isolated browser that starts with this login's stored cookies
    /// (so "remember this device" cookies can spare the 2FA) and hands every cookie of the connector's domains back
    /// into the login's jar. The browser is destroyed afterwards; the jar (stored encrypted) is the only copy.
    /// </summary>
    /// <returns>The answer, or (null, message) when the bank's page rejected the stored credentials.</returns>
    private async Task<(ChallengeAnswer? Answer, string? Rejected)> RunWebLoginAsync(ConnectorChallenge challenge, ISyncSink sink, SyncContext context, CancellationToken ct)
    {
        var spec = challenge.WebLoginSpec!;
        if (_webLogins is not { IsAvailable: true })
            throw new ConnectorException("Signing in on the bank's web page needs a browser, which is not available on this server (Chromium is not installed).");
        WebLoginSession session;
        try
        {
            session = await _webLogins.StartAsync(context.OwnerUserId, spec, context.Http.ExportCookies(), ct);
        }
        catch (BrowserActionException ex)
        {
            throw new ConnectorException(ex.Message, ex);
        }
        await using (session)
        {
            void OnMode(WebLoginMode mode) => sink.ShowWebLogin(challenge, session.Id, session.ExpiresAt, mode);
            session.ModeChanged += OnMode;
            sink.ShowWebLogin(challenge, session.Id, session.ExpiresAt, session.Mode);
            try
            {
                var result = await session.Completion.WaitAsync(ct);
                if (!result.Success)
                {
                    if (result.CredentialsRejected) return (null, result.Error ?? "The bank rejected the credentials.");
                    throw new ConnectorException(result.Error ?? "The web login failed.");
                }
                context.Http.ReplaceCookies(spec, result.Cookies);
            }
            finally
            {
                session.ModeChanged -= OnMode;
                sink.HideWebLogin();
            }
        }
        return (new ChallengeAnswer(challenge, null), null);
    }

    /// <summary>Updates the login's accounts; returns the linked, previously open ones the bank no longer lists.</summary>
    private static List<BankLoginAccount> UpsertAccounts(BankLogin login, IReadOnlyList<RemoteAccount> remote)
    {
        var vanished = new List<BankLoginAccount>();
        foreach (var r in remote)
        {
            var row = login.Accounts.FirstOrDefault(a => string.Equals(a.RemoteId, r.RemoteId, StringComparison.Ordinal));
            if (row == null)
            {
                row = new BankLoginAccount { BankLoginId = login.Id, RemoteId = r.RemoteId };
                login.Accounts.Add(row);
            }
            row.Name = r.Name;
            row.Type = r.Type;
            row.Iban = r.Iban;
            row.Currency = r.Currency;
            row.Closed = false;
        }
        foreach (var gone in login.Accounts.Where(a => remote.All(r => !string.Equals(r.RemoteId, a.RemoteId, StringComparison.Ordinal))))
        {
            if (gone.LinkedAccountId != null && !gone.Closed) vanished.Add(gone);
            gone.Closed = true;
            gone.StatementsEnabled = false;
        }
        return vanished;
    }

    private async Task RefreshAccountsAsync(
        BudgetDbContext db, BankLogin login, IBankConnector connector, IReadOnlyList<RemoteAccount> remote,
        ISyncSink sink, SyncContext context, List<string> errors, HashSet<int> affected, CancellationToken ct)
    {
        var links = login.Accounts.Where(a => a.LinkedAccountId != null && !a.Closed).ToList();
        _logger.LogInformation("Bank login {LoginId}: refreshing {Count} linked accounts.", login.Id, links.Count);
        foreach (var link in links)
        {
            var remoteAccount = remote.First(r => string.Equals(r.RemoteId, link.RemoteId, StringComparison.Ordinal));
            sink.Report($"Refreshing {link.Name}");
            try
            {
                var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == link.LinkedAccountId, ct);
                var ownerOk = account != null && await db.ImportSources.AnyAsync(s => s.Id == account.SourceId && s.OwnerUserId == login.OwnerUserId, ct);
                if (account == null || !ownerOk || !string.Equals(account.Provider, login.ConnectorType, StringComparison.Ordinal))
                    throw new ConnectorException("The linked account is no longer available for this login.");

                var since = await ComputeSinceAsync(db, link, account, ct);
                var step = await DriveAsync(a => connector.RefreshAsync(remoteAccount, since, a, ct), sink, context, ct);
                if (step.Status != StepStatus.Done) throw new ConnectorException(step.FailureReason ?? "Refresh failed.");

                var now = _clock.GetUtcNow().UtcDateTime;
                if (link.SyncFloorUtc == null && link.LastSuccessAt == null)
                    link.SyncFloorUtc = since is { } s ? s.ToDateTime(TimeOnly.MinValue) : null;
                _logger.LogInformation("Bank login {LoginId}: account link {LinkId} returned {Count} transactions.", login.Id, link.Id, step.Value!.Transactions.Count);
                await _transactions.ApplyAsync(db, account.Id, step.Value!, since, now, ct);
                link.LastSuccessAt = now;
                link.LastError = null;
                affected.Add(account.SourceId);
            }
            catch (ConnectorException ex)
            {
                _logger.LogWarning(ex, "Refreshing account link {LinkId} of bank login {LoginId} failed: {Error}", link.Id, login.Id, ex.Message);
                await RecordAccountErrorAsync(db, link, ex.Message, errors, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Refreshing account link {LinkId} of bank login {LoginId} failed.", link.Id, login.Id);
                await RecordAccountErrorAsync(db, link, "Unexpected error; see the server log.", errors, ct);
            }
            await db.SaveChangesAsync(ct);
        }
    }

    private static async Task RecordAccountErrorAsync(BudgetDbContext db, BankLoginAccount link, string message, List<string> errors, CancellationToken ct)
    {
        link.LastError = Truncate(message, 1000);
        errors.Add($"{link.Name}: {message}");
        if (link.LinkedAccountId is { } id)
            await db.Accounts.Where(a => a.Id == id).ExecuteUpdateAsync(s => s.SetProperty(a => a.Error, link.LastError), ct);
    }

    /// <summary>
    /// First sync after linking: the day of the newest BOOKED transaction (pending rows ignored); null when the
    /// account has none (full history). Later syncs reach back a week, but never before that first boundary.
    /// </summary>
    private static async Task<DateOnly?> ComputeSinceAsync(BudgetDbContext db, BankLoginAccount link, Account account, CancellationToken ct)
    {
        var newest = await db.Transactions.Where(t => t.AccountId == account.Id && t.Booked).MaxAsync(t => (long?)t.Timestamp, ct);
        DateOnly? newestDay = newest is { } n ? DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(n).UtcDateTime) : null;
        var initial = link.LastSuccessAt == null;
        if (initial) return newestDay;

        var floor = link.SyncFloorUtc is { } f ? DateOnly.FromDateTime(f) : (DateOnly?)null;
        var overlap = newestDay?.AddDays(-7);
        if (floor == null) return overlap;
        return overlap is { } o && o > floor ? o : floor;
    }

    private async Task FetchStatementsAsync(
        BudgetDbContext db, BankLogin login, IBankConnector connector, IReadOnlyList<RemoteAccount> remote, SyncRequest request,
        ISyncSink sink, SyncContext context, List<string> errors, HashSet<int> affected, CancellationToken ct)
    {
        var targets = login.Accounts
            .Where(a => a.StatementsEnabled && !a.Closed && a.LinkedAccountId != null
                        && (request.StatementAccounts == null || request.StatementAccounts.Contains(a.Id)))
            .ToList();
        if (targets.Count == 0) return;

        sink.Report("Fetching statements");
        var now = _clock.GetUtcNow().UtcDateTime;
        var known = await BankStatementStore.KnownIdentifiersAsync(db, login.Id, ct);
        var remoteTargets = targets.Select(t => remote.First(r => string.Equals(r.RemoteId, t.RemoteId, StringComparison.Ordinal))).ToList();
        try
        {
            var stored = 0;
            for (var batch = 0; batch < 200; batch++)
            {
                var step = await DriveAsync(a => connector.FetchStatementsAsync(remoteTargets, known, a, ct), sink, context, ct);
                if (step.Status != StepStatus.Done) throw new ConnectorException(step.FailureReason ?? "Fetching statements failed.");

                foreach (var statement in step.Value!)
                {
                    if (known.Contains(statement.Identifier)) continue;
                    Account? account = null;
                    if (statement.AccountRemoteId != null)
                    {
                        var target = targets.FirstOrDefault(t => string.Equals(t.RemoteId, statement.AccountRemoteId, StringComparison.Ordinal));
                        if (target == null) continue; // an account whose statements are off: not wanted
                        account = await db.Accounts.FirstAsync(a => a.Id == target.LinkedAccountId, ct);
                        if (!await db.ImportSources.AnyAsync(s => s.Id == account.SourceId && s.OwnerUserId == login.OwnerUserId, ct)) continue;
                        target.LastStatementAt = now;
                        affected.Add(account.SourceId);
                    }
                    await _statements.StoreAsync(db, login, account, statement, ct);
                    known.Add(statement.Identifier);
                    stored++;
                }
                // each batch is kept before the next one is asked for
                await db.SaveChangesAsync(ct);
                if (connector is not IBatchedStatements { MoreStatements: true } batched || step.Value!.Count == 0) break;
                sink.Report(batched.StatementsTotal is { } total
                    ? $"Fetching documents: {stored} of {stored + total - step.Value!.Count}"
                    : $"Fetching documents: {stored}");
            }
        }
        catch (ConnectorException ex)
        {
            errors.Add($"Statements: {ex.Message}");
        }
        finally
        {
            foreach (var t in targets) t.LastStatementsAttemptAt = now;
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }
}
