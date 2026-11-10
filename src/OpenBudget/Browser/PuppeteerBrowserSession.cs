using System.Text.Json;
using PuppeteerSharp;
using OpenBudget.Connectors;

namespace OpenBudget.Browser;

/// <summary>One incognito-style Chromium context with a single tab, driven over CDP.</summary>
internal sealed class PuppeteerBrowserSession : IBrowserSession
{
    private const string SameTabLinks =
        "document.addEventListener('click', e => { const a = e.target && e.target.closest && e.target.closest('a[target]'); if (a && a.target !== '_self') a.target = '_self'; }, true);";

    private readonly IBrowserContext _context;
    private readonly IPage _page;
    private readonly ICDPSession _cdp;
    private readonly BrowserOptions _options;
    private readonly Action _onClosed;
    private LatestPump<ScreencastFrame>? _pump;
    private int _castW, _castH, _castQuality;
    private int _disposed;

    private PuppeteerBrowserSession(IBrowserContext context, IPage page, ICDPSession cdp, BrowserOptions options, Action onClosed)
    {
        _context = context;
        _page = page;
        _cdp = cdp;
        _options = options;
        _onClosed = onClosed;
        _cdp.MessageReceived += OnCdpMessage;
    }

    public static async Task<PuppeteerBrowserSession> CreateAsync(
        IBrowserContext context, IPage page, BrowserFingerprint fingerprint, BrowserOptions options, Action onClosed)
    {
        var cdp = await page.CreateCDPSessionAsync();
        await cdp.SendAsync("Emulation.setUserAgentOverride", fingerprint.ToOverrideParams(options));
        await TryAsync(() => cdp.SendAsync("Emulation.setLocaleOverride", new { locale = options.Locale }));
        await cdp.SendAsync("Emulation.setDeviceMetricsOverride", new
        {
            width = options.ViewportWidth,
            height = options.ViewportHeight,
            deviceScaleFactor = 1,
            mobile = false,
            screenWidth = options.ScreenWidth,
            screenHeight = options.ScreenHeight,
        });
        // Links and window.open load in this tab: the user only ever sees one page.
        await page.EvaluateFunctionOnNewDocumentAsync(
            "() => { " + SameTabLinks + " window.open = (u) => { if (u) location.href = u; return null; };"
            + " Object.defineProperty(navigator, 'languages', { get: () => ['de-DE', 'de'] }); }");
        page.DefaultNavigationTimeout = options.NavigationTimeoutMs;
        return new PuppeteerBrowserSession(context, page, cdp, options, onClosed);
    }

    public Uri Url => Uri.TryCreate(_page.Url, UriKind.Absolute, out var u) ? u : new Uri("about:blank");

    public async Task NavigateAsync(Uri url, CancellationToken ct)
    {
        try
        {
            await _page.GoToAsync(url.ToString(), _options.NavigationTimeoutMs, [WaitUntilNavigation.DOMContentLoaded]).WaitAsync(ct);
        }
        catch (PuppeteerException ex)
        {
            throw new BrowserActionException("The page could not be opened.", ex);
        }
    }

    public Task GoBackAsync() =>
        TryAsync(() => _page.GoBackAsync(new NavigationOptions { Timeout = _options.NavigationTimeoutMs, WaitUntil = [WaitUntilNavigation.DOMContentLoaded] }));

    public Task ReloadAsync() =>
        TryAsync(() => _page.ReloadAsync(_options.NavigationTimeoutMs, [WaitUntilNavigation.DOMContentLoaded]));

    // ---- form filling (autofill of the sign-in form) ----

    public async Task<bool> FillAsync(string selector, string value, TimeSpan timeout)
    {
        try
        {
            var el = await _page.WaitForSelectorAsync(selector, new WaitForSelectorOptions { Visible = true, Timeout = (int)timeout.TotalMilliseconds });
            if (el == null) return false;
            await using (el)
            {
                // focus and select what is there, then type like a user so the page's own input handlers see it
                await el.EvaluateFunctionAsync("e => { e.focus(); if (e.select) e.select(); }");
                await el.TypeAsync(value, new PuppeteerSharp.Input.TypeOptions { Delay = 25 });
            }
            return true;
        }
        catch (Exception ex) when (ex is WaitTaskTimeoutException or PuppeteerException)
        {
            return false;
        }
    }

    public async Task<bool> ClickAsync(string selector)
    {
        try
        {
            var el = await _page.QuerySelectorAsync(selector);
            if (el == null) return false;
            await using (el)
            {
                if (await el.BoundingBoxAsync() == null) return false;
                await el.ClickAsync();
            }
            return true;
        }
        catch (PuppeteerException)
        {
            return false;
        }
    }

    public async Task<bool> IsVisibleAsync(string selector)
    {
        try
        {
            var el = await _page.QuerySelectorAsync(selector);
            if (el == null) return false;
            await using (el) return await el.BoundingBoxAsync() != null;
        }
        catch (PuppeteerException)
        {
            return false;
        }
    }

    // ---- cookies ----

    public async Task SetCookiesAsync(IReadOnlyList<CookieState> cookies)
    {
        if (cookies.Count == 0) return;
        var list = cookies.Select(c =>
        {
            var d = new Dictionary<string, object?>
(StringComparer.Ordinal)
            {
                ["name"] = c.Name,
                ["value"] = c.Value,
                ["domain"] = c.Domain,
                ["path"] = string.IsNullOrEmpty(c.Path) ? "/" : c.Path,
                ["secure"] = c.Secure,
                ["httpOnly"] = c.HttpOnly,
            };
            if (c.ExpiresUtc is { } e) d["expires"] = new DateTimeOffset(DateTime.SpecifyKind(e, DateTimeKind.Utc)).ToUnixTimeSeconds();
            if (c.SameSite is "Strict" or "Lax" or "None") d["sameSite"] = c.SameSite;
            return d;
        }).ToList();
        await _cdp.SendAsync("Network.setCookies", new { cookies = list });
    }

    public async Task<IReadOnlyList<CookieState>> GetCookiesAsync()
    {
        var result = (await _cdp.SendAsync("Network.getAllCookies"))!.Value;
        var list = new List<CookieState>();
        foreach (var c in result.GetProperty("cookies").EnumerateArray())
        {
            string S(string n) => c.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            bool B(string n) => c.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.True;
            DateTime? expires = null;
            if (c.TryGetProperty("expires", out var ex) && ex.ValueKind == JsonValueKind.Number && ex.GetDouble() > 0
                && !(c.TryGetProperty("session", out var sess) && sess.ValueKind == JsonValueKind.True))
                expires = DateTimeOffset.FromUnixTimeSeconds((long)ex.GetDouble()).UtcDateTime;
            var sameSite = S("sameSite");
            list.Add(new CookieState(S("name"), S("value"), S("domain"), S("path") is { Length: > 0 } p ? p : "/", expires,
                B("secure"), B("httpOnly"), sameSite.Length == 0 ? null : sameSite));
        }
        return list;
    }

    // ---- screencast ----

    public async Task StartScreencastAsync(Func<ScreencastFrame, Task> onFrame, int maxWidth, int maxHeight, int quality)
    {
        await StopScreencastAsync();
        _pump = new LatestPump<ScreencastFrame>(onFrame);
        await SendCastAsync(maxWidth, maxHeight, quality);
    }

    private async Task SendCastAsync(int w, int h, int q)
    {
        _castW = w;
        _castH = h;
        _castQuality = q;
        await _cdp.SendAsync("Page.startScreencast", new { format = "jpeg", quality = q, maxWidth = w, maxHeight = h, everyNthFrame = 1 });
    }

    public async Task ResizeScreencastAsync(int maxWidth, int maxHeight, int quality)
    {
        if (_pump == null || (maxWidth == _castW && maxHeight == _castH && quality == _castQuality)) return;
        await _cdp.SendAsync("Page.stopScreencast");
        await SendCastAsync(maxWidth, maxHeight, quality);
    }

    public async Task StopScreencastAsync()
    {
        if (_pump == null) return;
        _pump = null;
        await TryAsync(() => _cdp.SendAsync("Page.stopScreencast"));
    }

    private void OnCdpMessage(object? sender, MessageEventArgs e)
    {
        if (e.MessageID != "Page.screencastFrame") return;
        var pump = _pump;
        if (pump == null) return;
        try
        {
            var p = e.MessageData;
            var sessionId = p.GetProperty("sessionId").GetInt32();
            _ = AckAsync(sessionId); // ack first, before anything is sent to the client: keeps frames flowing
            var m = p.GetProperty("metadata");
            double D(string n) => m.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
            var data = Convert.FromBase64String(p.GetProperty("data").GetString()!);
            pump.Post(new ScreencastFrame(data, (int)Math.Round(D("deviceWidth")), (int)Math.Round(D("deviceHeight")), false));
        }
        catch (Exception ex) when (ex is JsonException or FormatException or KeyNotFoundException or InvalidOperationException)
        {
            // a broken frame is simply skipped; the next one repairs the picture
        }
    }

    private Task AckAsync(int sessionId) => TryAsync(() => _cdp.SendAsync("Page.screencastFrameAck", new { sessionId }));

    public async Task<ScreencastFrame?> CaptureStillAsync(int quality, double scale)
    {
        if (_pump == null) return null;
        object args = new { format = "jpeg", quality };
        int w = 0, h = 0;
        try
        {
            var lm = (await _cdp.SendAsync("Page.getLayoutMetrics"))!.Value;
            var vv = lm.GetProperty("cssVisualViewport");
            double V(string n) => vv.GetProperty(n).GetDouble();
            w = (int)Math.Round(V("clientWidth"));
            h = (int)Math.Round(V("clientHeight"));
            args = new { format = "jpeg", quality, clip = new { x = V("pageX"), y = V("pageY"), width = V("clientWidth"), height = V("clientHeight"), scale } };
        }
        catch (Exception ex) when (ex is PuppeteerException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            // plain screenshot at scale 1
        }
        var r = (await _cdp.SendAsync("Page.captureScreenshot", args))!.Value;
        var data = Convert.FromBase64String(r.GetProperty("data").GetString()!);
        return new ScreencastFrame(data, w > 0 ? w : _options.ViewportWidth, h > 0 ? h : _options.ViewportHeight, true);
    }

    // ---- input ----

    public Task InsertTextAsync(string text) => _cdp.SendAsync("Input.insertText", new { text });

    public async Task DispatchMouseAsync(string type, double x, double y, string button, int clickCount, double deltaX, double deltaY, int buttons, int modifiers)
    {
        if (type == "mouseWheel")
            await _cdp.SendAsync("Input.dispatchMouseEvent", new { type, x, y, deltaX, deltaY, modifiers });
        else if (type == "mouseMoved")
            await _cdp.SendAsync("Input.dispatchMouseEvent", new
            {
                type,
                x,
                y,
                modifiers,
                buttons,
                button = (buttons & 1) != 0 ? "left" : (buttons & 4) != 0 ? "middle" : (buttons & 2) != 0 ? "right" : "none",
            });
        else
            await _cdp.SendAsync("Input.dispatchMouseEvent", new { type, x, y, button, buttons, modifiers, clickCount = Math.Max(1, clickCount) });
    }

    public Task DispatchKeyAsync(string type, string key, string? code, string? text, int modifiers, string[]? commands) =>
        _cdp.SendAsync("Input.dispatchKeyEvent", new
        {
            type,
            key,
            code,
            modifiers,
            text = type == "keyUp" ? null : text,
            unmodifiedText = type == "keyUp" ? null : text,
            windowsVirtualKeyCode = KeyCodes.VirtualKey(key, code),
            commands,
        });

    private static async Task TryAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) when (ex is PuppeteerException or ObjectDisposedException or InvalidOperationException) { /* page already gone */ }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        _cdp.MessageReceived -= OnCdpMessage;
        _pump = null;
        try { await _context.CloseAsync(); }
        catch (Exception ex) when (ex is PuppeteerException or ObjectDisposedException or InvalidOperationException) { /* browser already gone */ }
        _onClosed();
    }
}

/// <summary>Windows virtual key codes for CDP key events.</summary>
internal static class KeyCodes
{
    /// <summary>
    /// Named keys first, then the physical key (KeyboardEvent.code): a character's own value is not its key code
    /// ("." is 46 = Delete, "(" is 40 = ArrowDown), so printable characters never map by their value.
    /// </summary>
    public static int VirtualKey(string key, string? code) => key switch
    {
        "Backspace" => 8,
        "Tab" => 9,
        "Enter" => 13,
        "Escape" => 27,
        "Delete" => 46,
        "ArrowLeft" => 37,
        "ArrowUp" => 38,
        "ArrowRight" => 39,
        "ArrowDown" => 40,
        "Home" => 36,
        "End" => 35,
        "PageUp" => 33,
        "PageDown" => 34,
        " " => 32,
        _ => CodeKey(code) ?? (key.Length == 1 && char.IsAsciiLetterOrDigit(key[0]) ? char.ToUpperInvariant(key[0]) : 0),
    };

    private static int? CodeKey(string? code) => code switch
    {
        null or "" => null,
        { Length: 4 } c when c.StartsWith("Key", StringComparison.Ordinal) => c[3],
        { Length: 6 } c when c.StartsWith("Digit", StringComparison.Ordinal) => c[5],
        "Space" => 32,
        "Semicolon" => 186,
        "Equal" => 187,
        "Comma" => 188,
        "Minus" => 189,
        "Period" => 190,
        "Slash" => 191,
        "Backquote" => 192,
        "BracketLeft" => 219,
        "Backslash" => 220,
        "BracketRight" => 221,
        "Quote" => 222,
        "IntlBackslash" => 226,
        _ => null,
    };
}
