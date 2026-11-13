using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.Connectors.Sync;

public sealed class BackgroundSyncOptions
{
    public const string EnabledVariable = "OPENBUDGET_BACKGROUND_SYNC";

    /// <summary>False switches background syncs off for the whole server.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often the scheduler looks for due logins.</summary>
    public TimeSpan Tick { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>The shortest time between two runs of a login, whatever its refresh interval says.</summary>
    public TimeSpan MinimumInterval { get; set; } = TimeSpan.FromHours(6);

    /// <summary>Each due time moves by up to this share of the interval, either way (never below <see cref="MinimumInterval"/>).</summary>
    public double Jitter { get; set; } = 0.15;

    /// <summary>Server-local hours without background runs: from <see cref="QuietFrom"/> to <see cref="QuietUntil"/>.</summary>
    public TimeOnly QuietFrom { get; set; } = new(0, 0);

    public TimeOnly QuietUntil { get; set; } = new(6, 0);
}

/// <summary>
/// Starts background syncs: every <see cref="BackgroundSyncOptions.Tick"/> it picks the logins whose owners switched
/// background syncs on, that need nothing from the owner, whose bank allows it (FinTS banks: per bank code) and whose
/// interval (at least six hours, with jitter) has passed, outside the quiet hours. After a restart the first look waits
/// a random part of a tick, and each look starts at most as many runs as may run at once, so there is no catch-up burst.
/// </summary>
public sealed class BackgroundSyncService : BackgroundService
{
    private readonly SystemDbContextFactory _system;
    private readonly BankSyncCoordinator _coordinator;
    private readonly ConnectorRegistry _registry;
    private readonly BackgroundSyncOptions _options;
    private readonly BankSyncOptions _syncOptions;
    private readonly TimeProvider _clock;
    private readonly ILogger<BackgroundSyncService> _logger;

    public BackgroundSyncService(
        SystemDbContextFactory system, BankSyncCoordinator coordinator, ConnectorRegistry registry, BackgroundSyncOptions options,
        BankSyncOptions syncOptions, TimeProvider clock, ILogger<BackgroundSyncService> logger)
    {
        _system = system;
        _coordinator = coordinator;
        _registry = registry;
        _options = options;
        _syncOptions = syncOptions;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled) return;
        try
        {
            await Task.Delay(_options.Tick * (0.5 + System.Security.Cryptography.RandomNumberGenerator.GetInt32(1000) / 2000.0), _clock, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await TickAsync(stoppingToken); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "The background sync scheduler failed; trying again at the next tick.");
                }
                await Task.Delay(_options.Tick, _clock, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutting down
        }
    }

    /// <summary>One look: starts the due logins' runs; returns how many it started.</summary>
    public async Task<int> TickAsync(CancellationToken ct)
    {
        if (!_coordinator.IsEnabled) return 0;
        var now = _clock.GetUtcNow();
        if (InQuietHours(TimeOnly.FromDateTime(_clock.GetLocalNow().DateTime), _options)) return 0;

        await using var db = await _system.CreateDbContextAsync(ct);
        var revokedBanks = await OpenBudget.Services.BankLoginService.RevokedBanksAsync(db, ct);
        var candidates = await db.BankLogins.AsNoTracking().Include(l => l.Accounts)
            .Where(l => l.BackgroundEnabled && !l.CredentialsInvalid && l.NeedsManualSync == null && l.EncryptedCredentials != null)
            .OrderBy(l => l.Id)
            .ToListAsync(ct);

        var started = 0;
        var limit = Math.Max(1, _syncOptions.BackgroundConcurrency);
        foreach (var login in candidates)
        {
            if (started >= limit) break;
            if (_registry.Find(login.ConnectorType) is not { } registration || registration.Silent == SilentCapability.None) continue;
            if (registration.FinTsBlz is { } blz && revokedBanks.Contains(blz)) continue;
            if (!IsDue(login, now.UtcDateTime, _options)) continue;
            if (_coordinator.IsBusy(login.Id)) continue;
            var statements = login.Accounts.Where(a => RefreshPolicy.StatementsDue(a, now.UtcDateTime)).Select(a => a.Id).ToList();
            var request = new SyncRequest(login.Id, Transactions: true, statements.Count > 0, statements, Background: true);
            if (_coordinator.LaunchBackground(login.OwnerUserId, request, registration.FinTsBlz) == StartResult.Started)
            {
                _logger.LogInformation("Background sync of bank login {LoginId} started.", login.Id);
                started++;
            }
        }
        return started;
    }

    public static bool InQuietHours(TimeOnly local, BackgroundSyncOptions options) =>
        options.QuietFrom != options.QuietUntil && local.IsBetween(options.QuietFrom, options.QuietUntil);

    /// <summary>
    /// Whether a background run of <paramref name="login"/> is due: not in its error backoff, its interval is automatic,
    /// and the interval (at least <see cref="BackgroundSyncOptions.MinimumInterval"/>, moved by the login's jitter) has
    /// passed since its last sync of any kind.
    /// </summary>
    public static bool IsDue(BankLogin login, DateTime nowUtc, BackgroundSyncOptions options)
    {
        if (login.NextBackgroundAttemptAt is { } next && nowUtc < next) return false;
        if (RefreshPolicy.Period(login.RefreshInterval) is not { } period) return false;
        var last = login.LastSyncAt is { } a && login.LastBackgroundSyncAt is { } b ? (a > b ? a : b) : login.LastSyncAt ?? login.LastBackgroundSyncAt;
        if (last == null) return true;
        var interval = period < options.MinimumInterval ? options.MinimumInterval : period;
        var wait = interval * (1 + options.Jitter * JitterFactor(login.Id, last.Value));
        if (wait < options.MinimumInterval) wait = options.MinimumInterval;
        return nowUtc - last.Value >= wait;
    }

    /// <summary>A fixed value in [-1, 1) per login and last sync, so the due time does not move between ticks.</summary>
    internal static double JitterFactor(int loginId, DateTime last)
    {
        var x = unchecked((ulong)last.Ticks * 0x9E3779B97F4A7C15UL ^ (ulong)loginId * 0xC2B2AE3D27D4EB4FUL);
        x ^= x >> 31;
        x *= 0xBF58476D1CE4E5B9UL;
        x ^= x >> 29;
        return (x % 2_000_000UL) / 1_000_000.0 - 1.0;
    }
}
