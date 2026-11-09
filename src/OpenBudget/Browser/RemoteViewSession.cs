using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace OpenBudget.Browser;

/// <summary>
/// One message from the view (mouse, key, text, view size). Short names keep the messages small.
/// t: m|k|text|v. Mouse k: move|down|up|wheel; key k: down|up|raw|char.
/// </summary>
public sealed class RemoteInput
{
    [JsonPropertyName("t")] public string T { get; set; } = "";

    [JsonPropertyName("k")] public string? K { get; set; }

    [JsonPropertyName("x")] public double X { get; set; }

    [JsonPropertyName("y")] public double Y { get; set; }

    /// <summary>Pressed button of down/up: left | middle | right.</summary>
    [JsonPropertyName("b")] public string? Button { get; set; }

    /// <summary>Bitmask of held buttons (left 1, right 2, middle 4).</summary>
    [JsonPropertyName("bs")] public int Buttons { get; set; }

    [JsonPropertyName("c")] public int Clicks { get; set; }

    [JsonPropertyName("dx")] public double Dx { get; set; }

    [JsonPropertyName("dy")] public double Dy { get; set; }

    /// <summary>Modifiers: alt 1, ctrl 2, meta 4, shift 8.</summary>
    [JsonPropertyName("m")] public int Mod { get; set; }

    [JsonPropertyName("key")] public string? Key { get; set; }

    [JsonPropertyName("code")] public string? Code { get; set; }

    [JsonPropertyName("text")] public string? Text { get; set; }

    /// <summary>View size: canvas CSS width and devicePixelRatio.</summary>
    [JsonPropertyName("w")] public double W { get; set; }

    [JsonPropertyName("dpr")] public double Dpr { get; set; }

    /// <summary>
    /// Clamps everything an untrusted client sent to sane ranges; null when the message is not one we understand.
    /// </summary>
    public RemoteInput? Sanitize(int viewportWidth, int viewportHeight)
    {
        static double Finite(double v, double min, double max) => double.IsNaN(v) || double.IsInfinity(v) ? 0 : Math.Clamp(v, min, max);
        switch (T)
        {
            case "m" when K is "move" or "down" or "up" or "wheel":
                X = Finite(X, 0, viewportWidth);
                Y = Finite(Y, 0, viewportHeight);
                Dx = Finite(Dx, -5000, 5000);
                Dy = Finite(Dy, -5000, 5000);
                Buttons &= 7;
                Mod &= 15;
                Clicks = Math.Clamp(Clicks, 0, 3);
                if (Button is not ("left" or "middle" or "right")) Button = "left";
                return this;
            case "k" when K is "down" or "up" or "raw" or "char":
                Mod &= 15;
                if (Key is not { Length: > 0 and <= 32 }) return null;
                if (Code is { Length: > 32 }) Code = null;
                if (Text is { Length: > 8 }) Text = null;
                return this;
            case "text" when !string.IsNullOrEmpty(Text):
                if (Text.Length > 2000) Text = Text[..2000];
                return this;
            case "v":
                W = Finite(W, 0, 10000);
                Dpr = Finite(Dpr, 1, 3);
                return this;
            default:
                return null;
        }
    }
}

public static class InputCoalescer
{
    /// <summary>
    /// Collapses neighbours that only matter as the last one: consecutive mouse moves (same buttons/modifiers), consecutive
    /// wheel events (deltas summed) and consecutive view sizes. Everything else keeps its order.
    /// </summary>
    public static List<RemoteInput> Coalesce(IEnumerable<RemoteInput> input)
    {
        var r = new List<RemoteInput>();
        foreach (var m in input)
        {
            var last = r.Count > 0 ? r[^1] : null;
            if (last is { T: "m" } && m.T == "m" && string.Equals(last.K, m.K, StringComparison.Ordinal))
            {
                if (m.K == "move" && last.Buttons == m.Buttons && last.Mod == m.Mod)
                {
                    r[^1] = m;
                    continue;
                }
                if (m.K == "wheel" && last.Mod == m.Mod)
                {
                    m.Dx += last.Dx;
                    m.Dy += last.Dy;
                    r[^1] = m;
                    continue;
                }
            }
            if (last is { T: "v" } && m.T == "v")
            {
                r[^1] = m;
                continue;
            }
            r.Add(m);
        }
        return r;
    }
}

public static class RemoteFramePacket
{
    /// <summary>Binary frame: [kind 0 moving / 1 still][css width u16 BE][css height u16 BE][jpeg].</summary>
    public static byte[] Build(ScreencastFrame f)
    {
        var b = new byte[5 + f.Data.Length];
        b[0] = (byte)(f.Still ? 1 : 0);
        b[1] = (byte)(f.Width >> 8);
        b[2] = (byte)f.Width;
        b[3] = (byte)(f.Height >> 8);
        b[4] = (byte)f.Height;
        f.Data.CopyTo(b, 5);
        return b;
    }
}

/// <summary>
/// Drives one browser session for the remote view: pushes frames (newest wins, JPEG while moving, a sharp still shortly
/// after the last change) and executes input coming from the view. Frames are never stored; nothing the user types
/// is logged.
/// </summary>
public sealed class RemoteViewSession : IAsyncDisposable
{
    /// <summary>Incoming messages queued before the newest ones are dropped (a flooding client only hurts itself).</summary>
    internal const int InputQueueLimit = 512;

    private readonly IBrowserSession _page;
    private readonly BrowserOptions _options;
    private readonly ILogger _logger;
    private readonly Func<byte[], Task> _sendBinary;
    private readonly Channel<RemoteInput> _in = Channel.CreateBounded<RemoteInput>(
        new BoundedChannelOptions(InputQueueLimit) { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly Timer _stillTimer;
    private Task? _loop;
    private long _frameSeq;
    private double _displayW;
    private double _dpr = 1;
    private int _disposed;

    public RemoteViewSession(IBrowserSession page, BrowserOptions options, ILogger logger, Func<byte[], Task> sendBinary)
    {
        _page = page;
        _options = options;
        _logger = logger;
        _sendBinary = sendBinary;
        _stillTimer = new Timer(OnStillTimer, null, Timeout.Infinite, Timeout.Infinite);
    }

    private void OnStillTimer(object? state) => _ = SendStillAsync();

    public async Task StartAsync(double displayCssWidth, double dpr)
    {
        _displayW = displayCssWidth;
        _dpr = dpr;
        var (w, h) = ScreencastPolicy.FrameSize(_displayW, _dpr, _options.ViewportWidth, _options.ViewportHeight);
        await _page.StartScreencastAsync(OnFrameAsync, w, h, _options.ScreencastQuality);
        _loop = Task.Run(LoopAsync);
    }

    private async Task OnFrameAsync(ScreencastFrame f)
    {
        var packet = RemoteFramePacket.Build(f);
        await _sendLock.WaitAsync();
        try
        {
            Interlocked.Increment(ref _frameSeq);
            await _sendBinary(packet);
        }
        finally { _sendLock.Release(); }
        ArmStill();
    }

    private void ArmStill()
    {
        if (_disposed != 0) return;
        try { _stillTimer.Change(ScreencastPolicy.StillDelay, Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { /* shutting down */ }
    }

    private async Task SendStillAsync()
    {
        if (_disposed != 0) return;
        try
        {
            var seq = Interlocked.Read(ref _frameSeq);
            var still = await _page.CaptureStillAsync(_options.StillQuality, ScreencastPolicy.StillScale(_displayW, _dpr, _options.ViewportWidth));
            if (still is null) return;
            var packet = RemoteFramePacket.Build(still);
            await _sendLock.WaitAsync();
            try
            {
                if (Interlocked.Read(ref _frameSeq) != seq || Volatile.Read(ref _disposed) != 0) return; // the page moved on: a stale still would undo it
                await _sendBinary(packet);
            }
            finally { _sendLock.Release(); }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Sending a still frame failed.");
        }
    }

    /// <summary>Queues one JSON message from the view; malformed or unknown messages are ignored.</summary>
    public void Post(string json)
    {
        RemoteInput? m;
        try { m = JsonSerializer.Deserialize<RemoteInput>(json)?.Sanitize(_options.ViewportWidth, _options.ViewportHeight); }
        catch (JsonException) { return; }
        if (m is not null) _in.Writer.TryWrite(m);
    }

    private async Task LoopAsync()
    {
        var ct = _cts.Token;
        try
        {
            while (await _in.Reader.WaitToReadAsync(ct))
            {
                var batch = new List<RemoteInput>();
                while (_in.Reader.TryRead(out var m)) batch.Add(m);
                foreach (var m in InputCoalescer.Coalesce(batch))
                {
                    try { await HandleAsync(m); }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogDebug(ex, "Forwarding an input event failed.");
                    }
                }
            }
        }
        catch (OperationCanceledException) { /* disposed */ }
    }

    private async Task HandleAsync(RemoteInput m)
    {
        switch (m.T)
        {
            case "m":
                var type = m.K switch { "down" => "mousePressed", "up" => "mouseReleased", "wheel" => "mouseWheel", _ => "mouseMoved" };
                await _page.DispatchMouseAsync(type, m.X, m.Y, m.Button ?? "left", m.Clicks, m.Dx, m.Dy, m.Buttons, m.Mod);
                if (m.K != "move" || m.Buttons != 0) ArmStill();
                break;
            case "k":
                await HandleKeyAsync(m);
                ArmStill();
                break;
            case "text":
                await _page.InsertTextAsync(m.Text!);
                ArmStill();
                break;
            case "v":
                _displayW = m.W;
                _dpr = m.Dpr;
                var (w, h) = ScreencastPolicy.FrameSize(m.W, m.Dpr, _options.ViewportWidth, _options.ViewportHeight);
                await _page.ResizeScreencastAsync(w, h, _options.ScreencastQuality);
                ArmStill();
                break;
            default:
                break;
        }
    }

    private async Task HandleKeyAsync(RemoteInput m)
    {
        var down = m.K is "down" or "raw";
        // Ctrl/Cmd+A selects everything in the remote field (the local clipboard shortcuts use the paste event).
        if (down && (m.Mod & (2 | 4)) != 0 && (m.Mod & (1 | 8)) == 0 && m.Key is "a" or "A")
        {
            await _page.DispatchKeyAsync("rawKeyDown", m.Key!, m.Code, null, m.Mod, ["selectAll"]);
            return;
        }
        var type = m.K switch { "up" => "keyUp", "raw" => "rawKeyDown", "char" => "char", _ => "keyDown" };
        await _page.DispatchKeyAsync(type, m.Key!, m.Code, m.Text, m.Mod, null);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        await _cts.CancelAsync();
        _in.Writer.TryComplete();
        await _stillTimer.DisposeAsync();
        try { await _page.StopScreencastAsync(); }
        catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogDebug(ex, "Stopping the screencast failed."); }
        try { if (_loop != null) await _loop.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException) { /* the loop ends with the token */ }
        _cts.Dispose();
        _sendLock.Dispose();
    }

    /// <summary>Serves one WebSocket until it closes: binary frames out, JSON input in. Closes the view when the socket ends.</summary>
    public static async Task ServeAsync(IBrowserSession page, BrowserOptions options, ILogger logger, WebSocket ws, CancellationToken ct)
    {
        await using var view = new RemoteViewSession(page, options, logger,
            b => ws.State == WebSocketState.Open ? ws.SendAsync(b, WebSocketMessageType.Binary, true, ct) : Task.CompletedTask);
        try
        {
            await view.StartAsync(0, 1);
            var buf = new byte[8 * 1024];
            using var ms = new MemoryStream();
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var r = await ws.ReceiveAsync(buf, ct);
                if (r.MessageType == WebSocketMessageType.Close) break;
                await ms.WriteAsync(buf.AsMemory(0, r.Count), ct);
                if (ms.Length > 16 * 1024)
                {
                    await ws.CloseAsync(WebSocketCloseStatus.MessageTooBig, null, CancellationToken.None);
                    break;
                }
                if (!r.EndOfMessage) continue;
                if (r.MessageType == WebSocketMessageType.Text) view.Post(System.Text.Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length));
                ms.SetLength(0);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException) { /* the viewer went away */ }
        finally
        {
            try
            {
                if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
            }
            catch (WebSocketException) { /* already gone */ }
        }
    }
}
