using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using PuppeteerSharp;

namespace OpenBudget.Browser;

/// <summary>
/// Singleton owning one lazily launched headed Chromium with its native browser identity. It has no persistent profile (a throw-away
/// user-data directory per process) and hands out <see cref="IBrowserSession"/>s that are each an isolated
/// incognito-style context, so bank sessions of different users can never mix. The process is closed again a
/// minute after the last session ended. A session in a long-lived profile (<see cref="OpenSessionAsync(string?, CancellationToken)"/>)
/// runs in a Chromium process of its own on that profile, restored from <see cref="BrowserProfileStore"/> and stored
/// back when the session ends; one such session per profile runs at a time.
/// </summary>
public sealed class BrowserService : IBrowserHost, IAsyncDisposable
{
    private readonly ILogger<BrowserService> _logger;
    private readonly SemaphoreSlim _slots;
    private readonly SemaphoreSlim _launchLock = new(1, 1);
    private readonly Lock _gate = new();
    private IBrowser? _browser;
    private string? _profileDir;
    private int _open;
    private CancellationTokenSource? _idleCts;
    private bool _disposed;
    private readonly BrowserProfileStore? _profiles;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _profileGates = new(StringComparer.Ordinal);

    public BrowserService(ILogger<BrowserService> logger, BrowserOptions? options = null, BrowserProfileStore? profiles = null)
    {
        _logger = logger;
        _profiles = profiles;
        Options = options ?? new BrowserOptions();
        _slots = new SemaphoreSlim(Math.Max(1, Options.MaxConcurrentSessions));
    }

    public BrowserOptions Options { get; }

    /// <summary>Browser contexts open right now (web logins and kept browsers).</summary>
    internal int OpenSessions
    {
        get { lock (_gate) return _open; }
    }

    public bool IsAvailable => FindChrome(Options.ExecutablePath) != null;

    /// <summary>Resolved Chromium path, or null when none is installed.</summary>
    public static string? FindChrome(string? configured = null)
    {
        foreach (var candidate in new[]
        {
            configured,
            Environment.GetEnvironmentVariable("CHROME_PATH"),
            "/usr/bin/chromium",
            "/usr/bin/chromium-browser",
            "/usr/bin/google-chrome",
            "/usr/bin/google-chrome-stable",
            "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
        })
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate)) return candidate;
        }
        return null;
    }

    public async Task<IBrowserSession> OpenSessionAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // web logins beyond the limit queue for a slot: a sync of several banks opens more of them at once
        if (!await _slots.WaitAsync(Options.SlotWait, ct))
            throw new BrowserActionException("Too many web logins are open at the moment; try again in a few minutes.");
        try
        {
            lock (_gate)
            {
                _open++;
                CancelIdleShutdown();
            }
            var browser = await EnsureBrowserAsync();
            var context = await browser.CreateBrowserContextAsync();
            try
            {
                var page = await context.NewPageAsync();
                var session = await PuppeteerBrowserSession.CreateAsync(context, page, Options, OnSessionClosed);
                return session;
            }
            catch
            {
                try { await context.CloseAsync(); } catch (Exception ex) { _logger.LogDebug(ex, "Closing the browser context failed."); }
                throw;
            }
        }
        catch
        {
            OnSessionClosed();
            throw;
        }
    }

    public async Task<IBrowserSession> OpenSessionAsync(string? profile, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (profile == null || _profiles is not { IsEnabled: true }) return await OpenSessionAsync(ct);
        var gate = _profileGates.GetOrAdd(profile, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(Options.ProfileWait, ct))
        {
            _logger.LogInformation("The browser profile {Profile} is in use; this session runs in an ephemeral profile.", profile);
            return await OpenSessionAsync(ct);
        }
        var gateHeld = 1;
        void ReleaseGate()
        {
            if (Interlocked.Exchange(ref gateHeld, 0) == 1) gate.Release();
        }
        try
        {
            if (!await _slots.WaitAsync(Options.SlotWait, ct))
                throw new BrowserActionException("Too many web logins are open at the moment; try again in a few minutes.");
        }
        catch
        {
            ReleaseGate();
            throw;
        }
        lock (_gate)
        {
            _open++;
            CancelIdleShutdown();
        }
        var dir = Path.Combine(Path.GetTempPath(), "openbudget-profile-" + Guid.NewGuid().ToString("N"));
        IBrowser? browser = null;
        try
        {
            Directory.CreateDirectory(dir);
            _profiles.Restore(profile, dir);
            browser = await LaunchAsync(dir);
            var page = (await browser.PagesAsync()).FirstOrDefault() ?? await browser.NewPageAsync();
            var started = browser;
            async Task CloseAsync()
            {
                try
                {
                    await CloseProcessAsync(started);
                    _profiles.Save(profile, dir);
                }
                finally
                {
                    DeleteDirectory(dir);
                    ReleaseGate();
                }
            }
            return await PuppeteerBrowserSession.CreateAsync(browser.DefaultContext, page, Options, OnSessionClosed, CloseAsync);
        }
        catch
        {
            if (browser != null) await CloseProcessAsync(browser);
            DeleteDirectory(dir);
            ReleaseGate();
            OnSessionClosed();
            throw;
        }
    }

    private async Task CloseProcessAsync(IBrowser browser)
    {
        try { await browser.CloseAsync(); } catch (Exception ex) { _logger.LogDebug(ex, "Closing Chromium failed."); }
        try { browser.Dispose(); } catch (Exception ex) { _logger.LogDebug(ex, "Disposing Chromium failed."); }
    }

    private void DeleteDirectory(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception ex) { _logger.LogDebug(ex, "Deleting a browser profile directory failed."); }
    }

    private void CancelIdleShutdown()
    {
        var old = _idleCts;
        _idleCts = null;
        if (old == null) return;
        old.Cancel();
        old.Dispose();
    }

    private void OnSessionClosed()
    {
        _slots.Release();
        CancellationTokenSource? cts = null;
        lock (_gate)
        {
            if (--_open == 0 && !_disposed)
            {
                cts = _idleCts = new CancellationTokenSource();
            }
        }
        if (cts != null) _ = ShutdownWhenIdleAsync(cts.Token);
    }

    private async Task ShutdownWhenIdleAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(Options.IdleShutdown, ct);
            await _launchLock.WaitAsync(ct);
            try
            {
                lock (_gate) { if (_open != 0) return; }
                await CloseBrowserAsync();
            }
            finally { _launchLock.Release(); }
        }
        catch (OperationCanceledException) { /* a new session arrived */ }
    }

    private async Task<IBrowser> EnsureBrowserAsync()
    {
        if (_browser is { IsClosed: false, IsConnected: true }) return _browser;
        await _launchLock.WaitAsync();
        try
        {
            if (_browser is { IsClosed: false, IsConnected: true }) return _browser;
            await CloseBrowserAsync();
            _profileDir = Path.Combine(Path.GetTempPath(), "openbudget-chromium-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_profileDir);
            var browser = await LaunchAsync(_profileDir);
            _browser = browser;
            return browser;
        }
        finally { _launchLock.Release(); }
    }

    /// <summary>Starts a Chromium process on the user-data directory <paramref name="userDataDir"/>.</summary>
    private async Task<IBrowser> LaunchAsync(string userDataDir)
    {
        var exe = FindChrome(Options.ExecutablePath)
            ?? throw new BrowserActionException("No Chromium found (CHROME_PATH is not set or the file is missing).");
        var noSandbox = Options.NoSandbox ?? (File.Exists("/.dockerenv") || Environment.UserName == "root"
            || Environment.GetEnvironmentVariable("BROWSER_NO_SANDBOX") == "1");
        int debuggingPort;
        using (var reservation = new Socket(AddressFamily.InterNetwork, SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp))
        {
            reservation.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            debuggingPort = ((IPEndPoint)reservation.LocalEndPoint!).Port;
        }
        var args = new List<string>
        {
            "--disable-dev-shm-usage", $"--lang={Options.Locale}", "--no-first-run", "--no-default-browser-check",
            $"--window-size={Options.ViewportWidth},{Options.ViewportHeight}", $"--user-data-dir={userDataDir}",
            "--remote-debugging-address=127.0.0.1", $"--remote-debugging-port={debuggingPort}",
        };
        if (noSandbox) args.Add("--no-sandbox");
        // headless pages otherwise count as hidden and stop rendering, which stalls clicks (they wait for a frame)
        if (Options.Headless)
            args.AddRange(["--headless=new", "--disable-background-timer-throttling", "--disable-renderer-backgrounding", "--disable-backgrounding-occluded-windows"]);
        var launch = new LaunchOptions
        {
            ExecutablePath = exe,
            Headless = false,
            IgnoreDefaultArgs = true,
            UserDataDir = userDataDir,
            Args = [.. args],
            DefaultViewport = null,
            Timeout = 60000,
        };
        if (!Options.Headless && OperatingSystem.IsLinux() && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
        {
            const string xvfb = "/usr/bin/xvfb-run";
            if (!File.Exists(xvfb)) throw new BrowserActionException("Web logins need a headed browser; install Xvfb and xauth or provide a DISPLAY.");
            launch.ExecutablePath = xvfb;
            launch.Args = ["--auto-servernum", "--", exe, .. args];
        }
        _logger.LogInformation("Starting {Mode} Chromium {Exe} (no-sandbox={NoSandbox}).", Options.Headless ? "headless" : "headed", exe, noSandbox);
        return await Puppeteer.LaunchAsync(launch);
    }

    private async Task CloseBrowserAsync()
    {
        var browser = _browser;
        _browser = null;
        if (browser != null)
        {
            try { await browser.CloseAsync(); } catch (Exception ex) { _logger.LogDebug(ex, "Closing Chromium failed."); }
            try { browser.Dispose(); } catch (Exception ex) { _logger.LogDebug(ex, "Disposing Chromium failed."); }
        }
        if (_profileDir != null)
        {
            try { Directory.Delete(_profileDir, recursive: true); } catch (Exception ex) { _logger.LogDebug(ex, "Deleting the throw-away profile failed."); }
            _profileDir = null;
        }
    }

    /// <summary>The "Chromium x.y.z" version reported by the executable, or null (diagnostics).</summary>
    public async Task<string?> ProbeVersionAsync()
    {
        var exe = FindChrome(Options.ExecutablePath);
        if (exe == null) return null;
        try
        {
            using var proc = Process.Start(new ProcessStartInfo(exe, "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (proc == null) return null;
            var output = await proc.StandardOutput.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await proc.WaitForExitAsync(timeout.Token);
            var m = Regex.Match(output, @"\d+\.\d+\.\d+\.\d+", RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(1));
            return m.Success ? m.Value : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not read the Chromium version.");
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_gate) CancelIdleShutdown();
        await CloseBrowserAsync();
        _slots.Dispose();
        _launchLock.Dispose();
    }
}
