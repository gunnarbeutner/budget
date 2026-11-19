using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using OpenBudget.Browser;

namespace OpenBudget.E2E.Tests;

/// <summary>
/// A stand-in for the browser sidecar's launcher: starts each Chrome as a local headless process with a DevTools port,
/// on a user-data directory of its own (unpacked from the profile zip, packed again when stopped), so the app's one
/// browser path runs against a real Chrome without the sidecar and without opening windows. There is no shared screen:
/// <see cref="ShowAsync"/> does nothing.
/// </summary>
public sealed class LocalChromeLauncher : IBrowserLauncher
{
    private readonly string _chrome;
    private readonly bool _headless;
    private readonly ConcurrentDictionary<string, (Process Process, string Dir, int Port)> _running = new(StringComparer.Ordinal);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    /// <summary>A launcher of <paramref name="chrome"/>.</summary>
    /// <param name="chrome">The Chromium executable (<see cref="ChromiumLocator"/>).</param>
    /// <param name="headless">False starts a headed Chrome (for the identity check, only where no window shows).</param>
    public LocalChromeLauncher(string chrome, bool headless = true)
    {
        _chrome = chrome;
        _headless = headless;
    }

    /// <summary>The DevTools port of the running Chrome <paramref name="id"/>, for a test that acts on its page like the user.</summary>
    public int PortOf(string id) => _running[id].Port;

    public async Task<RemoteChrome> StartAsync(byte[]? profile, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        var dir = Path.Combine(Path.GetTempPath(), "ob-e2e-chrome-" + id);
        Directory.CreateDirectory(dir);
        if (profile is { Length: > 0 }) await ZipFile.ExtractToDirectoryAsync(new MemoryStream(profile), dir, ct);
        foreach (var guard in Directory.GetFiles(dir, "Singleton*")) File.Delete(guard);
        int port;
        using (var reservation = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
        {
            reservation.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            port = ((IPEndPoint)reservation.LocalEndPoint!).Port;
        }
        var start = new ProcessStartInfo(_chrome) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var arg in (string[])
        [
            "--disable-dev-shm-usage", "--lang=de-DE", "--no-first-run", "--no-default-browser-check", "--window-size=1280,800",
            "--user-data-dir=" + dir, "--remote-debugging-address=127.0.0.1", "--remote-debugging-port=" + port,
        ])
            start.ArgumentList.Add(arg);
        if (OperatingSystem.IsLinux()) start.ArgumentList.Add("--no-sandbox");
        // headless pages otherwise count as hidden and stop rendering, which stalls clicks (they wait for a frame)
        if (_headless)
            foreach (var arg in (string[])["--headless=new", "--disable-background-timer-throttling", "--disable-renderer-backgrounding", "--disable-backgrounding-occluded-windows"])
                start.ArgumentList.Add(arg);
        start.ArgumentList.Add("about:blank");
        var process = Process.Start(start) ?? throw new BrowserActionException("Chrome did not start.");
        process.ErrorDataReceived += (_, _) => { };
        process.OutputDataReceived += (_, _) => { };
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();
        _running[id] = (process, dir, port);
        return new RemoteChrome(id, port);
    }

    public async Task<Uri> DevToolsEndpointAsync(RemoteChrome chrome, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(chrome);
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (true)
        {
            try
            {
                var json = await Http.GetStringAsync(new Uri($"http://127.0.0.1:{chrome.Port}/json/version"), ct);
                using var doc = JsonDocument.Parse(json);
                return new Uri(doc.RootElement.GetProperty("webSocketDebuggerUrl").GetString()!);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                if (DateTime.UtcNow > until) throw new BrowserActionException("The local Chrome cannot be reached.", ex);
                await Task.Delay(100, ct);
            }
        }
    }

    public async Task<byte[]?> StopAsync(string id, bool returnProfile, CancellationToken ct)
    {
        if (!_running.TryRemove(id, out var chrome)) return null;
        await EndAsync(chrome.Process);
        try
        {
            if (!returnProfile) return null;
            using var buffer = new MemoryStream();
            await ZipFile.CreateFromDirectoryAsync(chrome.Dir, buffer, ct);
            return buffer.ToArray();
        }
        finally
        {
            try { Directory.Delete(chrome.Dir, recursive: true); }
            catch (IOException) { /* a temp directory */ }
        }
    }

    public Task ShowAsync(string? id, CancellationToken ct) => Task.CompletedTask;

    public Task ResetAsync(CancellationToken ct) => Task.CompletedTask;

    private static async Task EndAsync(Process process)
    {
        try
        {
            // the app closes Chrome over CDP first; whatever is left is ended here
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await process.WaitForExitAsync(wait.Token); }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
        catch (InvalidOperationException) { /* never started or already gone */ }
        finally { process.Dispose(); }
    }
}

/// <summary>The sidecar's screen without selkies: nothing is streamed; it records which browser would be shown.</summary>
public sealed class NoStreamScreenBackend : IRemoteScreenBackend
{
    /// <summary>The browser on the (absent) shared screen.</summary>
    public string? Shown { get; private set; }

    public Task ShowAsync(string? screenId, CancellationToken ct)
    {
        Shown = screenId;
        return Task.CompletedTask;
    }

    public Task SetViewerTokenAsync(string? token, CancellationToken ct) => Task.CompletedTask;
}
