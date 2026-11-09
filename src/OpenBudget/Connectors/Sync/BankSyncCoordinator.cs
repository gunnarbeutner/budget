using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.Connectors.Sync;

public sealed class BankSyncOptions
{
    /// <summary>How long an unanswered challenge stays open before the sync fails.</summary>
    public TimeSpan ChallengeTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Delay between polls of a decoupled approval when the connector names none.</summary>
    public TimeSpan DefaultPollInterval { get; set; } = TimeSpan.FromSeconds(3);
}

public enum SyncPhase
{
    Running,
    WaitingForInput,
    WaitingForApproval,
    Finished,
}

/// <summary>Immutable snapshot of a sync job, as shown to the login's owner.</summary>
public sealed record BankSyncStatus(
    int LoginId,
    string OwnerUserId,
    SyncPhase Phase,
    string? Message,
    DateTime StartedAt,
    DateTime? FinishedAt,
    bool? Succeeded,
    string? Error,
    ConnectorChallenge? Challenge,
    Guid ChallengeId,
    DateTime? ChallengeExpiresAt,
    string? WebLoginSessionId = null);

public enum StartResult
{
    Started,
    AlreadyRunning,
    NotFound,
    Disabled,
    NothingToDo,
}

/// <summary>
/// Owns the sync jobs: at most one per bank login, running server-side as background tasks so a page
/// navigation (or closing the tab) does not kill them. Every entry point takes the acting user id and
/// only ever touches that user's logins. Progress, challenges and results are published through
/// <see cref="ChangeNotificationService"/> as user-scoped <see cref="DataArea.BankSync"/> changes.
/// There is deliberately no timer: syncs start only when the owner asks or has OpenBudget open.
/// </summary>
public sealed class BankSyncCoordinator : IHostedService, IDisposable
{
    private readonly SystemDbContextFactory _system;
    private readonly BankSyncRunner _runner;
    private readonly SecretsProtector _secrets;
    private readonly ChangeNotificationService _notifier;
    private readonly BankSyncOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILogger<BankSyncCoordinator> _logger;
    private readonly Lock _lock = new();
    private readonly Dictionary<int, Job> _jobs = [];
    private readonly CancellationTokenSource _shutdown = new();

    public BankSyncCoordinator(
        SystemDbContextFactory system, BankSyncRunner runner, SecretsProtector secrets, ChangeNotificationService notifier,
        BankSyncOptions options, TimeProvider clock, ILogger<BankSyncCoordinator> logger)
    {
        _system = system;
        _runner = runner;
        _secrets = secrets;
        _notifier = notifier;
        _options = options;
        _clock = clock;
        _logger = logger;
    }

    public bool IsEnabled => _secrets.IsConfigured;

    // ---- Starting ----

    public async Task<StartResult> StartAsync(string userId, SyncRequest request, CancellationToken ct = default)
    {
        if (!IsEnabled) return StartResult.Disabled;
        await using var db = await _system.CreateDbContextAsync(ct);
        if (!await db.BankLogins.AnyAsync(l => l.Id == request.LoginId && l.OwnerUserId == userId, ct))
            return StartResult.NotFound;
        return Launch(userId, request);
    }

    /// <summary>"Sync now": transactions and the statements of every account that has them switched on.</summary>
    public Task<StartResult> SyncNowAsync(string userId, int loginId, CancellationToken ct = default) =>
        StartAsync(userId, new SyncRequest(loginId, Transactions: true, Statements: true), ct);

    /// <summary>"Download now" for one account's statements (a <see cref="BankLoginAccount.Id"/>).</summary>
    public async Task<StartResult> DownloadStatementsAsync(string userId, int loginId, int loginAccountId, CancellationToken ct = default)
    {
        if (!IsEnabled) return StartResult.Disabled;
        await using var db = await _system.CreateDbContextAsync(ct);
        var ok = await db.BankLoginAccounts.AnyAsync(a => a.Id == loginAccountId && a.BankLoginId == loginId && a.BankLogin.OwnerUserId == userId, ct);
        return !ok
            ? StartResult.NotFound
            : Launch(userId, new SyncRequest(loginId, Transactions: false, Statements: true, [loginAccountId]));
    }

    /// <summary>
    /// "Sync all": transactions of every login except those set to Never (manual-only ones are included);
    /// statements only for accounts whose own schedule says they are due (manual-interval accounts use "Download now").
    /// </summary>
    public async Task<int> SyncAllAsync(string userId, CancellationToken ct = default)
    {
        if (!IsEnabled) return 0;
        var now = _clock.GetUtcNow().UtcDateTime;
        var started = 0;
        foreach (var login in await LoadLoginsAsync(userId, ct))
        {
            var transactions = RefreshPolicy.IncludedInSyncAll(login.RefreshInterval);
            var statements = login.Accounts.Where(a => RefreshPolicy.StatementsDue(a, now)).Select(a => a.Id).ToList();
            if (!transactions && statements.Count == 0) continue;
            if (Launch(userId, new SyncRequest(login.Id, transactions, statements.Count > 0, statements)) == StartResult.Started) started++;
        }
        return started;
    }

    /// <summary>
    /// The page-load trigger: starts syncs for this user's logins whose transactions and/or statements are due
    /// under their own intervals. Logins whose credentials were rejected are skipped (retrying could lock the account).
    /// </summary>
    public async Task<int> StartDueAsync(string userId, CancellationToken ct = default)
    {
        if (!IsEnabled) return 0;
        var now = _clock.GetUtcNow().UtcDateTime;
        var started = 0;
        foreach (var login in await LoadLoginsAsync(userId, ct))
        {
            if (login.CredentialsInvalid) continue;
            var transactions = RefreshPolicy.IsDue(login.RefreshInterval, login.LastSyncAt, now);
            var statements = login.Accounts.Where(a => RefreshPolicy.StatementsDue(a, now)).Select(a => a.Id).ToList();
            if (!transactions && statements.Count == 0) continue;
            if (Launch(userId, new SyncRequest(login.Id, transactions, statements.Count > 0, statements)) == StartResult.Started) started++;
        }
        return started;
    }

    private async Task<List<BankLogin>> LoadLoginsAsync(string userId, CancellationToken ct)
    {
        await using var db = await _system.CreateDbContextAsync(ct);
        return await db.BankLogins.AsNoTracking().Include(l => l.Accounts).Where(l => l.OwnerUserId == userId).ToListAsync(ct);
    }

    private StartResult Launch(string userId, SyncRequest request)
    {
        Job job;
        lock (_lock)
        {
            if (_jobs.TryGetValue(request.LoginId, out var existing) && existing.Status.Phase != SyncPhase.Finished)
                return StartResult.AlreadyRunning;
            job = new Job(this, userId, request, _clock.GetUtcNow().UtcDateTime);
            _jobs[request.LoginId] = job;
        }
        Publish(userId);
        job.Task = Task.Run(() => job.RunAsync(_shutdown.Token));
        return StartResult.Started;
    }

    // ---- Observing and answering ----

    public IReadOnlyList<BankSyncStatus> GetStatuses(string userId)
    {
        lock (_lock) return _jobs.Values.Where(j => j.UserId == userId).Select(j => j.Status).ToList();
    }

    public BankSyncStatus? GetStatus(string userId, int loginId)
    {
        lock (_lock) return _jobs.TryGetValue(loginId, out var j) && j.UserId == userId ? j.Status : null;
    }

    /// <summary>Delivers the user's answer to the open challenge; false when there is none (any more) or it is another user's.</summary>
    public bool SubmitAnswer(string userId, int loginId, Guid challengeId, string? value)
    {
        Job? job;
        lock (_lock) _jobs.TryGetValue(loginId, out job);
        return job != null && job.UserId == userId && job.TryAnswer(challengeId, value);
    }

    /// <summary>Cancels a running sync of the user's login.</summary>
    public bool Cancel(string userId, int loginId)
    {
        Job? job;
        lock (_lock) _jobs.TryGetValue(loginId, out job);
        if (job == null || job.UserId != userId) return false;
        job.Cancel();
        return true;
    }

    /// <summary>Completes when no job of that login is running (tests and shutdown).</summary>
    public Task WhenIdleAsync(int loginId)
    {
        Job? job;
        lock (_lock) _jobs.TryGetValue(loginId, out job);
        return job?.Task ?? Task.CompletedTask;
    }

    public async Task StartAsync(CancellationToken cancellationToken) => await Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _shutdown.CancelAsync();
        List<Task> running;
        lock (_lock) running = _jobs.Values.Select(j => j.Task).Where(t => t != null).Select(t => t!).ToList();
        try { await Task.WhenAll(running).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken); }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { /* shutting down anyway */ }
    }

    private void Publish(string userId) => _notifier.Publish(new DataChange(DataArea.BankSync, UserId: userId));

    public void Dispose() => _shutdown.Dispose();

    private sealed class Job : ISyncSink, IDisposable
    {
        private readonly BankSyncCoordinator _owner;
        private readonly SyncRequest _request;
        private readonly CancellationTokenSource _cts = new();
        private TaskCompletionSource<string?>? _answer;
        private BankSyncStatus _status;

        public Job(BankSyncCoordinator owner, string userId, SyncRequest request, DateTime started)
        {
            _owner = owner;
            _request = request;
            UserId = userId;
            _status = new BankSyncStatus(request.LoginId, userId, SyncPhase.Running, null, started, null, null, null, null, Guid.Empty, null);
        }

        public string UserId { get; }

        public Task? Task { get; set; }

        public BankSyncStatus Status
        {
            get { lock (_owner._lock) return _status; }
        }

        public void Cancel()
        {
            try { _cts.Cancel(); }
            catch (ObjectDisposedException) { /* already finished */ }
        }

        public void Dispose() => _cts.Dispose();

        private void Update(Func<BankSyncStatus, BankSyncStatus> change)
        {
            lock (_owner._lock) _status = change(_status);
            _owner.Publish(UserId);
        }

        public void Report(string message) => Update(s => s with { Message = message });

        public void ShowWebLogin(ConnectorChallenge challenge, string sessionId, DateTime expiresAt) =>
            Update(s => s with
            {
                Phase = SyncPhase.WaitingForInput,
                Challenge = challenge,
                ChallengeId = Guid.NewGuid(),
                ChallengeExpiresAt = expiresAt,
                WebLoginSessionId = sessionId,
            });

        public void HideWebLogin() =>
            Update(s => s with { Phase = SyncPhase.Running, Challenge = null, ChallengeExpiresAt = null, WebLoginSessionId = null });

        public async Task RunAsync(CancellationToken shutdown)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(shutdown, _cts.Token);
            SyncOutcome outcome;
            try
            {
                outcome = await _owner._runner.RunAsync(_request, this, linked.Token);
            }
            catch (Exception ex)
            {
                _owner._logger.LogError(ex, "Sync job for bank login {LoginId} crashed.", _request.LoginId);
                outcome = new SyncOutcome(false, "Unexpected error while syncing; see the server log.", []);
            }

            var finished = _owner._clock.GetUtcNow().UtcDateTime;
            Update(s => s with
            {
                Phase = SyncPhase.Finished,
                FinishedAt = finished,
                Succeeded = outcome.Success,
                Error = outcome.Error,
                Challenge = null,
                ChallengeExpiresAt = null,
                WebLoginSessionId = null,
                Message = null,
            });
            foreach (var sourceId in outcome.AffectedSourceIds)
                _owner._notifier.Publish(new DataChange(DataArea.ImportedData, SourceId: sourceId));
            Dispose();
        }

        public bool TryAnswer(Guid challengeId, string? value)
        {
            TaskCompletionSource<string?>? tcs;
            ConnectorChallenge? challenge;
            lock (_owner._lock)
            {
                tcs = _answer;
                challenge = _status.Challenge;
                if (tcs == null || challenge == null || _status.ChallengeId != challengeId || challenge.Kind is ChallengeKind.Approval or ChallengeKind.WebLogin) return false;
            }
            if (challenge.Kind == ChallengeKind.Choice && !challenge.Options.Any(o => string.Equals(o.Id, value, StringComparison.Ordinal))) return false;
            if (challenge.Kind is ChallengeKind.Code or ChallengeKind.Image && string.IsNullOrWhiteSpace(value)) return false;
            return tcs.TrySetResult(value);
        }

        public async Task<ChallengeAnswer?> AskAsync(ConnectorChallenge challenge, CancellationToken ct)
        {
            var now = _owner._clock.GetUtcNow().UtcDateTime;
            var keep = _status.Challenge != null && _status.Challenge.IsSameQuestion(challenge);
            var expires = keep ? _status.ChallengeExpiresAt!.Value : now + _owner._options.ChallengeTimeout;
            var id = keep ? _status.ChallengeId : Guid.NewGuid();
            var approval = challenge.Kind == ChallengeKind.Approval;
            var tcs = approval ? null : new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_owner._lock) _answer = tcs;
            Update(s => s with
            {
                Phase = approval ? SyncPhase.WaitingForApproval : SyncPhase.WaitingForInput,
                Challenge = challenge,
                ChallengeId = id,
                ChallengeExpiresAt = expires,
            });

            try
            {
                if (approval)
                {
                    await Task.Delay(challenge.PollInterval ?? _owner._options.DefaultPollInterval, _owner._clock, ct);
                    return _owner._clock.GetUtcNow().UtcDateTime >= expires ? null : new ChallengeAnswer(challenge, null);
                }

                var remaining = expires - _owner._clock.GetUtcNow().UtcDateTime;
                if (remaining <= TimeSpan.Zero) return null;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(remaining);
                try
                {
                    var value = await tcs!.Task.WaitAsync(timeout.Token);
                    return new ChallengeAnswer(challenge, value);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return null;
                }
            }
            finally
            {
                lock (_owner._lock) _answer = null;
                // The next question (or the end of the job) replaces the challenge; clear a stale one right away
                // unless the same approval is about to be asked again.
                if (!approval || _owner._clock.GetUtcNow().UtcDateTime >= expires)
                    Update(s => s with { Phase = SyncPhase.Running, Challenge = null, ChallengeExpiresAt = null });
            }
        }
    }
}
