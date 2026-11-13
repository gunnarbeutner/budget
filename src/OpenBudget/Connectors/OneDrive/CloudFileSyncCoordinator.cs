using Microsoft.EntityFrameworkCore;
using OpenBudget.Services;

namespace OpenBudget.Connectors.OneDrive;

/// <summary>
/// Checks a user's linked cloud files for new versions and hands each new version to the registered
/// <see cref="ICloudFileConsumer"/>s. Like bank syncs there is no timer: a check starts when the user has
/// OpenBudget open (<see cref="StartDue"/> from the page-load host, at most every
/// <see cref="OneDriveOptions.CheckInterval"/>) or asks for one (<see cref="CheckNowAsync"/>). A file whose content
/// tag is unchanged is not downloaded. At most one check runs per user.
/// </summary>
public sealed class CloudFileSyncCoordinator : IDisposable
{
    private readonly SystemDbContextFactory _system;
    private readonly OneDriveService _oneDrive;
    private readonly ICloudFileReader _reader;
    private readonly IReadOnlyList<ICloudFileConsumer> _consumers;
    private readonly OneDriveOptions _options;
    private readonly TimeProvider _clock;
    private readonly ChangeNotificationService _notifier;
    private readonly ILogger<CloudFileSyncCoordinator> _logger;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Task> _running = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTime> _lastStarted = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();

    public CloudFileSyncCoordinator(
        SystemDbContextFactory system, OneDriveService oneDrive, ICloudFileReader reader, IEnumerable<ICloudFileConsumer> consumers, OneDriveOptions options,
        TimeProvider clock, ChangeNotificationService notifier, ILogger<CloudFileSyncCoordinator> logger)
    {
        _system = system;
        _oneDrive = oneDrive;
        _reader = reader;
        _consumers = consumers.ToList();
        _options = options;
        _clock = clock;
        _notifier = notifier;
        _logger = logger;
    }

    public bool IsEnabled => _oneDrive.IsEnabled;

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    /// <summary>
    /// The page-load trigger: starts a background check of the user's files that were not checked within the
    /// check interval, unless one ran (or is running) for the user within that interval. True when one started.
    /// </summary>
    public bool StartDue(string userId)
    {
        if (!IsEnabled) return false;
        lock (_lock)
        {
            if (_running.TryGetValue(userId, out var running) && !running.IsCompleted) return false;
            if (_lastStarted.TryGetValue(userId, out var last) && Now - last < _options.CheckInterval) return false;
            _lastStarted[userId] = Now;
            _running[userId] = Task.Run(() => RunAsync(userId, force: false, null, _shutdown.Token));
            return true;
        }
    }

    /// <summary>Checks the user's linked files now (or only <paramref name="linkId"/>), regardless of when they were last checked.</summary>
    public async Task CheckNowAsync(string userId, int? linkId = null, CancellationToken ct = default)
    {
        if (!IsEnabled) return;
        Task task;
        lock (_lock)
        {
            var previous = _running.TryGetValue(userId, out var r) ? r : Task.CompletedTask;
            _lastStarted[userId] = Now;
            task = _running[userId] = previous.ContinueWith(
                _ => RunAsync(userId, force: true, linkId, _shutdown.Token), CancellationToken.None,
                TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        }
        await task.WaitAsync(ct);
    }

    /// <summary>Completes when no check of the user is running (tests).</summary>
    public Task WhenIdleAsync(string userId)
    {
        lock (_lock) return _running.TryGetValue(userId, out var t) ? t : Task.CompletedTask;
    }

    private async Task RunAsync(string userId, bool force, int? onlyLinkId, CancellationToken ct)
    {
        try
        {
            List<Models.CloudFileLink> links;
            await using (var db = await _system.CreateDbContextAsync(ct))
            {
                var dueBefore = Now - _options.CheckInterval;
                links = await db.CloudFileLinks.AsNoTracking().Include(f => f.Connection)
                    .Where(f => f.OwnerUserId == userId && !f.Connection.NeedsReconnect)
                    .Where(f => onlyLinkId == null || f.Id == onlyLinkId)
                    .Where(f => force || f.LastCheckedAt == null || f.LastCheckedAt <= dueBefore)
                    .OrderBy(f => f.Id)
                    .ToListAsync(ct);
            }

            foreach (var link in links)
                if (!await CheckLinkAsync(userId, link, ct)) break;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Checking linked cloud files of user {UserId} failed.", userId);
        }
    }

    /// <summary>Checks one file; false when the connection needs a reconnect (no point in trying the others).</summary>
    private async Task<bool> CheckLinkAsync(string userId, Models.CloudFileLink link, CancellationToken ct)
    {
        string? error = null;
        CloudFileContent? content = null;
        var reconnect = false;
        try
        {
            // A version that failed last time is processed again even though its tag is known.
            content = await _reader.ReadAsync(userId, link.Id, link.LastError == null ? link.LastCTag : null, ct);
            if (content.Changed)
            {
                var info = new CloudFileLinkInfo(link.Id, link.OwnerUserId, link.DisplayPath);
                foreach (var consumer in _consumers) await consumer.ConsumeAsync(info, content, ct);
            }
        }
        catch (CloudReconnectRequiredException ex)
        {
            error = ex.Message;
            reconnect = true;
        }
        catch (Exception ex) when (ex is CloudException or InvalidDataException or KeyNotFoundException)
        {
            error = ex.Message;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Reading linked cloud file {LinkId} of user {UserId} failed.", link.Id, userId);
            error = "Unexpected error while reading the file; see the server log.";
        }

        await using var db = await _system.CreateDbContextAsync(ct);
        var row = await db.CloudFileLinks.FirstOrDefaultAsync(f => f.Id == link.Id && f.OwnerUserId == userId, ct);
        if (row != null)
        {
            row.LastCheckedAt = Now;
            row.LastError = error is { Length: > 1000 } ? error[..1000] : error;
            if (error == null && content != null)
            {
                row.LastCTag = content.Metadata.ContentTag;
                row.LastETag = content.Metadata.ETag;
                row.RemoteModifiedAt = content.Metadata.LastModifiedUtc;
                if (content.Changed) row.LastImportedAt = Now;
            }
            await db.SaveChangesAsync(ct);
        }
        _notifier.Publish(new DataChange(DataArea.CloudFiles, UserId: userId));
        return !reconnect;
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _shutdown.Dispose();
    }
}
