using PuppeteerSharp;

namespace OpenBudget.Browser;

/// <summary>
/// Singleton handing out <see cref="IBrowserSession"/>s for web logins. Every session runs in a Chrome of its own that
/// the browser sidecar's launcher (<see cref="IBrowserLauncher"/>) starts on its shared screen, on an empty user-data
/// directory or on a login's long-lived profile (restored from <see cref="BrowserProfileStore"/> and stored back when
/// the session ends; one such session per profile runs at a time); this service connects to it over CDP. Without the
/// sidecar configured (<see cref="RemoteBrowserOptions"/>) no session can be opened and web logins say so.
/// </summary>
public sealed class BrowserService : IBrowserHost, IAsyncDisposable
{
    /// <summary>What a web login reports when the browser sidecar is not configured.</summary>
    public const string NotConfiguredMessage =
        "Web logins need the browser sidecar, which is not configured on this server (Browser__Remote__LauncherUrl, StreamUrl and Token).";

    private readonly ILogger<BrowserService> _logger;
    private readonly SemaphoreSlim _slots;
    private readonly BrowserProfileStore? _profiles;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _profileGates = new(StringComparer.Ordinal);
    private readonly IBrowserLauncher? _launcher;
    private readonly SemaphoreSlim _resetLock = new(1, 1);
    private bool _launcherReset;
    private int _open;
    private bool _disposed;

    public BrowserService(ILogger<BrowserService> logger, IBrowserLauncher? launcher, BrowserOptions? options = null, BrowserProfileStore? profiles = null)
    {
        _logger = logger;
        _launcher = launcher;
        _profiles = profiles;
        Options = options ?? new BrowserOptions();
        _slots = new SemaphoreSlim(Math.Max(1, Options.MaxConcurrentSessions));
    }

    public BrowserOptions Options { get; }

    /// <summary>Browser sessions open right now (web logins and kept browsers).</summary>
    internal int OpenSessions => Volatile.Read(ref _open);

    public bool IsAvailable => _launcher != null;

    public Task<IBrowserSession> OpenSessionAsync(CancellationToken ct) => OpenSessionAsync(null, ct);

    public async Task<IBrowserSession> OpenSessionAsync(string? profile, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var launcher = _launcher ?? throw new BrowserActionException(NotConfiguredMessage);
        if (_profiles is not { IsEnabled: true }) profile = null;
        var gate = profile == null ? null : _profileGates.GetOrAdd(profile, _ => new SemaphoreSlim(1, 1));
        if (gate != null && !await gate.WaitAsync(Options.ProfileWait, ct))
        {
            _logger.LogInformation("The browser profile {Profile} is in use; this session runs in an ephemeral profile.", profile);
            gate = null;
            profile = null;
        }
        var gateHeld = gate != null ? 1 : 0;
        void ReleaseGate()
        {
            if (Interlocked.Exchange(ref gateHeld, 0) == 1) gate!.Release();
        }
        try
        {
            // web logins beyond the limit queue for a slot: a sync of several banks opens more of them at once
            if (!await _slots.WaitAsync(Options.SlotWait, ct))
                throw new BrowserActionException("Too many web logins are open at the moment; try again in a few minutes.");
        }
        catch
        {
            ReleaseGate();
            throw;
        }
        Interlocked.Increment(ref _open);
        try
        {
            return await OpenRemoteAsync(launcher, profile, ReleaseGate, ct);
        }
        catch
        {
            ReleaseGate();
            OnSessionClosed();
            throw;
        }
    }

    /// <summary>
    /// A session in a Chrome the sidecar starts on <paramref name="profile"/>'s stored profile (or an empty one). Closing
    /// it ends Chrome, stores the profile back and then runs <paramref name="release"/>. On failure the caller releases.
    /// </summary>
    private async Task<IBrowserSession> OpenRemoteAsync(IBrowserLauncher launcher, string? profile, Action release, CancellationToken ct)
    {
        await ResetLauncherOnceAsync(launcher, ct);
        var archive = profile != null ? _profiles!.ReadArchive(profile) : null;
        var chrome = await launcher.StartAsync(archive, ct);
        IBrowser? browser = null;
        try
        {
            var endpoint = await launcher.DevToolsEndpointAsync(chrome, ct);
            browser = await Puppeteer.ConnectAsync(new ConnectOptions { BrowserWSEndpoint = endpoint.ToString(), DefaultViewport = null });
            var page = (await browser.PagesAsync()).FirstOrDefault() ?? await browser.NewPageAsync();
            var connected = browser;
            async Task CloseAsync()
            {
                try
                {
                    // closing over CDP lets Chrome write its profile before the launcher packs it
                    await CloseProcessAsync(connected);
                    var saved = await launcher.StopAsync(chrome.Id, returnProfile: profile != null, CancellationToken.None);
                    if (profile != null && saved != null) _profiles!.SaveArchive(profile, saved);
                }
                catch (BrowserActionException ex) { _logger.LogWarning(ex, "Stopping the sidecar's browser failed."); }
                finally { release(); }
            }
            _logger.LogInformation("Connected to Chrome {Id} on the browser sidecar.", chrome.Id);
            return await PuppeteerBrowserSession.CreateAsync(page, Options, OnSessionClosed, CloseAsync, chrome.Id);
        }
        catch
        {
            if (browser != null) await CloseProcessAsync(browser);
            try { await launcher.StopAsync(chrome.Id, returnProfile: false, CancellationToken.None); }
            catch (BrowserActionException ex) { _logger.LogDebug(ex, "Stopping the sidecar's browser failed."); }
            throw;
        }
    }

    /// <summary>Stops the browsers an earlier run of the app left on the sidecar, once, before the first new one.</summary>
    private async Task ResetLauncherOnceAsync(IBrowserLauncher launcher, CancellationToken ct)
    {
        if (Volatile.Read(ref _launcherReset)) return;
        await _resetLock.WaitAsync(ct);
        try
        {
            if (_launcherReset) return;
            await launcher.ResetAsync(ct);
            Volatile.Write(ref _launcherReset, true);
        }
        finally { _resetLock.Release(); }
    }

    private async Task CloseProcessAsync(IBrowser browser)
    {
        try { await browser.CloseAsync(); } catch (Exception ex) { _logger.LogDebug(ex, "Closing Chrome failed."); }
        try { browser.Dispose(); } catch (Exception ex) { _logger.LogDebug(ex, "Disposing the Chrome connection failed."); }
    }

    private void OnSessionClosed()
    {
        Interlocked.Decrement(ref _open);
        _slots.Release();
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _slots.Dispose();
        _resetLock.Dispose();
        return ValueTask.CompletedTask;
    }
}
