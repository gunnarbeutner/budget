using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;

namespace OpenBudget.Browser;

/// <summary>What <see cref="RemoteScreen"/> needs from the browser sidecar.</summary>
public interface IRemoteScreenBackend
{
    /// <summary>Shows only the windows of browser <paramref name="screenId"/>; null shows none.</summary>
    Task ShowAsync(string? screenId, CancellationToken ct);

    /// <summary>Makes <paramref name="token"/> the only token selkies accepts (with keyboard and mouse); null: none.</summary>
    Task SetViewerTokenAsync(string? token, CancellationToken ct);
}

/// <summary>The browser sidecar: its launcher shows windows, selkies' control API holds the viewer tokens.</summary>
public sealed class SidecarScreenBackend : IRemoteScreenBackend
{
    private readonly IBrowserLauncher _launcher;
    private readonly HttpClient _http;
    private readonly RemoteBrowserOptions _options;

    public SidecarScreenBackend(IBrowserLauncher launcher, HttpClient http, RemoteBrowserOptions options)
    {
        _launcher = launcher;
        _http = http;
        _options = options;
    }

    public Task ShowAsync(string? screenId, CancellationToken ct) => _launcher.ShowAsync(screenId, ct);

    public async Task SetViewerTokenAsync(string? token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            new Uri(_options.StreamUrl!, RemoteBrowserOptions.StreamPath + "api/tokens"))
        {
            Content = JsonContent.Create(TokenTable(token)),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.Token);
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new BrowserActionException($"The browser stream refused the viewer token ({(int)response.StatusCode}).");
    }

    /// <summary>
    /// selkies' token table (POST /api/tokens replaces it as a whole): <paramref name="token"/> as the one viewer with
    /// keyboard and mouse, or empty, which disconnects every viewer.
    /// </summary>
    public static Dictionary<string, SelkiesTokenEntry> TokenTable(string? token)
    {
        var table = new Dictionary<string, SelkiesTokenEntry>(StringComparer.Ordinal);
        if (token != null) table[token] = new SelkiesTokenEntry("viewer", null, true);
        return table;
    }
}

/// <summary>One entry of selkies' token table: the viewer's role, display slot and whether it has keyboard and mouse.</summary>
public sealed record SelkiesTokenEntry(
    [property: System.Text.Json.Serialization.JsonPropertyName("role")] string Role,
    [property: System.Text.Json.Serialization.JsonPropertyName("slot")] int? Slot,
    [property: System.Text.Json.Serialization.JsonPropertyName("mk_control")] bool MouseKeyboard);

/// <summary>A web login shown on the sidecar's screen: the stream token of its owner, valid until disposed.</summary>
public sealed class ScreenView : IAsyncDisposable
{
    private readonly Func<ValueTask> _release;
    private int _released;

    internal ScreenView(string token, Func<ValueTask> release, CancellationToken ended)
    {
        Token = token;
        Ended = ended;
        _release = release;
    }

    /// <summary>The selkies viewer token (goes into the stream page's URL fragment, never to a log).</summary>
    public string Token { get; }

    /// <summary>Cancelled when the view ended (released, the web login ended, or replaced).</summary>
    public CancellationToken Ended { get; }

    public ValueTask DisposeAsync() => Interlocked.Exchange(ref _released, 1) == 0 ? _release() : ValueTask.CompletedTask;
}

/// <summary>
/// The browser sidecar has one screen for all web logins, so it shows one at a time: web logins that need their owner
/// queue here in order. The first is brought to the front (every other browser's windows are hidden) and its owner gets a
/// fresh selkies token; when its last view is released, or the web login ends, the token is revoked, the stream proxy
/// drops its connections, and the next one is shown. Nobody else can use the token: the stream proxy only serves the
/// owner of the web login on screen (<see cref="Authorize"/>).
/// </summary>
public sealed class RemoteScreen : IAsyncDisposable
{
    private readonly IRemoteScreenBackend _backend;
    private readonly ILogger<RemoteScreen> _logger;
    private readonly SemaphoreSlim _switch = new(1, 1);
    private readonly Lock _gate = new();
    private readonly List<Slot> _queue = [];

    public RemoteScreen(IRemoteScreenBackend backend, ILogger<RemoteScreen> logger)
    {
        _backend = backend;
        _logger = logger;
    }

    private sealed class Slot(string sessionId, string userId, string screenId)
    {
        public string SessionId { get; } = sessionId;
        public string UserId { get; } = userId;
        public string ScreenId { get; } = screenId;
        public int Views { get; set; }
        public string? Token { get; set; }
        public CancellationTokenSource Ended { get; } = new();
        public TaskCompletionSource Shown { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>
    /// Waits until web login <paramref name="sessionId"/> (owned by <paramref name="userId"/>, in sidecar browser
    /// <paramref name="screenId"/>) is on screen and returns its view. Several views of the same login share one slot.
    /// <paramref name="sessionEnded"/> completes when the web login ends, which ends its slot.
    /// </summary>
    public async Task<ScreenView> ShowAsync(string sessionId, string userId, string screenId, Task sessionEnded, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sessionEnded);
        Slot slot;
        bool first;
        lock (_gate)
        {
            slot = _queue.Find(s => s.SessionId == sessionId) ?? new Slot(sessionId, userId, screenId);
            if (slot.UserId != userId) throw new InvalidOperationException("The web login belongs to someone else.");
            first = slot.Views == 0 && !_queue.Contains(slot);
            if (first) _queue.Add(slot);
            slot.Views++;
        }
        if (first) _ = sessionEnded.ContinueWith(_ => EndAsync(slot), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        try
        {
            await PromoteAsync();
            await slot.Shown.Task.WaitAsync(ct);
        }
        catch
        {
            await ReleaseAsync(slot);
            throw;
        }
        return new ScreenView(slot.Token!, () => new ValueTask(ReleaseAsync(slot)), slot.Ended.Token);
    }

    /// <summary>Web logins waiting before <paramref name="sessionId"/> (0: on screen or next; -1: not queued).</summary>
    public int Position(string sessionId)
    {
        lock (_gate) return _queue.FindIndex(s => s.SessionId == sessionId);
    }

    /// <summary>
    /// Whether <paramref name="userId"/> may use the stream now: they own the web login on screen and
    /// <paramref name="isOpen"/> confirms that login (by id and owner) is still open. <paramref name="ended"/> is
    /// cancelled when it leaves the screen.
    /// </summary>
    public bool Authorize(string userId, Func<string, bool> isOpen, out CancellationToken ended)
    {
        ArgumentNullException.ThrowIfNull(isOpen);
        Slot? shown;
        lock (_gate) shown = _queue.Count > 0 && _queue[0].Token != null ? _queue[0] : null;
        if (shown != null && string.Equals(shown.UserId, userId, StringComparison.Ordinal) && isOpen(shown.SessionId)
            && !shown.Ended.IsCancellationRequested)
        {
            ended = shown.Ended.Token;
            return true;
        }
        ended = CancellationToken.None;
        return false;
    }

    private async Task ReleaseAsync(Slot slot)
    {
        bool last;
        lock (_gate) last = --slot.Views <= 0;
        if (last) await EndAsync(slot);
    }

    private async Task EndAsync(Slot slot)
    {
        bool wasShown;
        lock (_gate)
        {
            var index = _queue.IndexOf(slot);
            if (index < 0) return;
            wasShown = index == 0 && slot.Token != null;
            _queue.RemoveAt(index);
        }
        await slot.Ended.CancelAsync();
        slot.Shown.TrySetCanceled();
        if (wasShown)
        {
            await _switch.WaitAsync();
            try { await HideAsync(); }
            finally { _switch.Release(); }
        }
        await PromoteAsync();
    }

    /// <summary>Revokes the viewer token and hides every browser (callers hold <see cref="_switch"/>).</summary>
    private async Task HideAsync()
    {
        try
        {
            await _backend.SetViewerTokenAsync(null, CancellationToken.None);
            await _backend.ShowAsync(null, CancellationToken.None);
        }
        catch (Exception ex) when (ex is BrowserActionException or HttpRequestException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Revoking the browser stream's viewer token failed.");
        }
    }

    /// <summary>Puts the first waiting web login on screen when nothing is shown.</summary>
    private async Task PromoteAsync()
    {
        await _switch.WaitAsync();
        try
        {
            while (true)
            {
                Slot next;
                lock (_gate)
                {
                    if (_queue.Count == 0 || _queue[0].Token != null) return;
                    next = _queue[0];
                }
                var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
                try
                {
                    await _backend.ShowAsync(next.ScreenId, CancellationToken.None);
                    await _backend.SetViewerTokenAsync(token, CancellationToken.None);
                }
                catch (Exception ex) when (ex is BrowserActionException or HttpRequestException or TaskCanceledException)
                {
                    _logger.LogWarning(ex, "Showing a web login on the browser sidecar failed.");
                    lock (_gate) _queue.Remove(next);
                    next.Shown.TrySetException(new BrowserActionException("The browser could not be shown.", ex));
                    await next.Ended.CancelAsync();
                    continue;
                }
                bool ended;
                lock (_gate)
                {
                    ended = !_queue.Contains(next);
                    if (!ended) next.Token = token;
                }
                if (ended)
                {
                    // it ended while being shown: take it off the screen again
                    await HideAsync();
                    continue;
                }
                next.Shown.TrySetResult();
                return;
            }
        }
        finally { _switch.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        Slot[] slots;
        lock (_gate)
        {
            slots = [.. _queue];
            _queue.Clear();
        }
        foreach (var slot in slots)
        {
            await slot.Ended.CancelAsync();
            slot.Shown.TrySetCanceled();
        }
        _switch.Dispose();
    }
}
