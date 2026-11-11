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
        IBrowserContext context, IPage page, BrowserOptions options, Action onClosed)
    {
        var cdp = await page.CreateCDPSessionAsync();
        // Ordinary links stay in the streamed tab without replacing native browser functions or identity properties.
        await page.EvaluateFunctionOnNewDocumentAsync("() => { " + SameTabLinks + " }");
        page.DefaultNavigationTimeout = options.NavigationTimeoutMs;
        var native = await page.EvaluateFunctionAsync<JsonElement>("() => ({ userAgent: navigator.userAgent, width: innerWidth, height: innerHeight })");
        return new PuppeteerBrowserSession(context, page, cdp, options, onClosed)
        {
            UserAgent = native.GetProperty("userAgent").GetString()!,
            ViewportWidth = native.GetProperty("width").GetInt32(),
            ViewportHeight = native.GetProperty("height").GetInt32(),
        };
    }

    public string UserAgent { get; private init; } = "";

    public int? ViewportWidth { get; private set; }

    public int? ViewportHeight { get; private set; }

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
                // Controlled inputs (React) may drop keystrokes: when nothing (or only the start of the value) stuck, set it
                // through the native setter (bypassing React's value tracker) and fire a bubbling input event so onChange runs.
                // Any other value means the page took the keystrokes and rewrote them (a masked "•" in an access code box):
                // setting the value again would enter the character a second time, so it is left alone.
                await el.EvaluateFunctionAsync(
                    @"(e, v) => {
                        if (e.value !== v && v.startsWith(e.value)) {
                            const proto = e instanceof HTMLTextAreaElement ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
                            Object.getOwnPropertyDescriptor(proto, 'value').set.call(e, v);
                            e.dispatchEvent(new Event('input', { bubbles: true }));
                        }
                        e.dispatchEvent(new Event('change', { bubbles: true }));
                    }", value);
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

    public async Task<bool> HasVisibleTextAsync(string selector, IReadOnlyList<string> texts)
    {
        try
        {
            return await _page.EvaluateFunctionAsync<bool>(
                // An element counts as visible when it or anything inside has a box (a wrapper with display: contents
                // has none itself). Text is compared with collapsed whitespace (no-break spaces) and without soft hyphens.
                @"(sel, texts) => {
                    const norm = s => (s || '').replace(/­/g, '').replace(/[\s  ]+/g, ' ').toLowerCase();
                    const shown = e => { const r = e.getBoundingClientRect(); return r.width > 0 || r.height > 0; };
                    const wanted = texts.map(norm);
                    return Array.from(document.querySelectorAll(sel)).some(e => {
                        if (!shown(e) && !Array.from(e.querySelectorAll('*')).some(shown)) return false;
                        const t = norm(e.innerText || e.textContent);
                        return wanted.length === 0 || wanted.some(x => t.includes(x));
                    });
                }", selector, texts);
        }
        catch (PuppeteerException)
        {
            return false;
        }
    }

    public async Task<bool> TypeCharacterAsync(string selector, string character, TimeSpan timeout)
    {
        try
        {
            var el = await _page.WaitForSelectorAsync(selector, new WaitForSelectorOptions { Visible = true, Timeout = (int)timeout.TotalMilliseconds });
            if (el == null) return false;
            await using (el)
            {
                return await TypeCharacterAsync(el, character);
            }
        }
        catch (Exception ex) when (ex is WaitTaskTimeoutException or PuppeteerException) { return false; }
    }

    private async Task<bool> TypeCharacterAsync(IElementHandle el, string character)
    {
        // A bare char event avoids a keyup landing in the next box after the page auto-advances focus.
        var focused = await el.EvaluateFunctionAsync<bool>("e => { e.focus(); if (e.select) e.select(); return document.activeElement === e; }");
        if (!focused) return false;
        const string holds = @"(e, c) => new Promise(r => setTimeout(() => r(e.value === c || (e.value.length > 0 && !/[0-9A-Za-z]/.test(e.value))), 50))";
        await _cdp.SendAsync("Input.dispatchKeyEvent", new { type = "char", text = character, unmodifiedText = character, key = character });
        if (await el.EvaluateFunctionAsync<bool>(holds, character)) return true;
        await el.EvaluateFunctionAsync("e => { e.focus(); if (e.select) e.select(); }");
        await _page.Keyboard.PressAsync(character);
        return await el.EvaluateFunctionAsync<bool>(holds, character);
    }

    public async Task<bool> FillCodeAsync(string selector, string code)
    {
        IElementHandle[] fields = [];
        try
        {
            fields = await _page.QuerySelectorAllAsync(selector);
            var visible = new List<IElementHandle>();
            foreach (var field in fields)
            {
                if (await field.BoundingBoxAsync() != null) visible.Add(field);
            }
            if (visible.Count == 1)
            {
                var maxLength = await visible[0].EvaluateFunctionAsync<int>("e => e.maxLength");
                if (maxLength > 0 && maxLength < code.Length) return false;
                return await FillAsync(selector, code, TimeSpan.FromSeconds(2));
            }
            if (visible.Count != code.Length) return false;
            for (var i = 0; i < visible.Count; i++)
            {
                if (!await TypeCharacterAsync(visible[i], code[i].ToString())) return false;
            }
            return true;
        }
        catch (PuppeteerException) { return false; }
        finally
        {
            foreach (var field in fields) await field.DisposeAsync();
        }
    }

    public async Task<WebLoginCheck> CheckAsync(string function, string argument)
    {
        try
        {
            var r = await _page.EvaluateFunctionAsync<JsonElement>(function, argument);
            var ok = r.ValueKind == JsonValueKind.Object && r.TryGetProperty("ok", out var o) && o.ValueKind == JsonValueKind.True;
            var reason = r.ValueKind == JsonValueKind.Object && r.TryGetProperty("reason", out var why) && why.ValueKind == JsonValueKind.String
                ? why.GetString() ?? "" : "";
            return new WebLoginCheck(ok, reason.Length > 200 ? reason[..200] : reason);
        }
        catch (Exception ex) when (ex is PuppeteerException or JsonException)
        {
            return new WebLoginCheck(false, "the check could not run in the page");
        }
    }

    public void ObserveResponses(IReadOnlyList<string> urlParts, Action<ObservedResponse> onResponse)
    {
        _page.RequestFailed += (_, e) =>
        {
            var url = e.Request.Url;
            if (!urlParts.Any(p => url.Contains(p, StringComparison.OrdinalIgnoreCase))) return;
            var path = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.GetLeftPart(UriPartial.Path) : "?";
            var code = e.Request.FailureText;
            if (code is not { Length: > 0 and <= 80 } || !code.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-' or ':'))
                code = "request_failed";
            onResponse(new ObservedResponse(path, 0, "network", code));
        };
        _page.Response += async (_, e) =>
        {
            try
            {
                var url = e.Response.Url;
                if (!urlParts.Any(p => url.Contains(p, StringComparison.OrdinalIgnoreCase))) return;
                var path = Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.GetLeftPart(UriPartial.Path) : "?";
                var status = (int)e.Response.Status;
                string? field = null, code = null;
                if (status >= 400 && e.Response.Request.Method != HttpMethod.Options)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(await e.Response.TextAsync());
                        (field, code) = ShortErrorCode(doc.RootElement);
                    }
                    catch (Exception ex) when (ex is PuppeteerException or JsonException) { /* no readable body */ }
                }
                onResponse(new ObservedResponse(path, status, field, code));
            }
            catch (Exception ex) when (ex is PuppeteerException or InvalidOperationException) { /* page gone */ }
        };
    }

    /// <summary>A top-level error code field whose value looks like a code ("INVALID_CREDENTIALS", "401.3"), nothing longer.</summary>
    internal static (string? Field, string? Code) ShortErrorCode(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return (null, null);
        foreach (var name in new[] { "code", "errorCode", "error_code", "error", "status", "errorType", "type" })
        {
            if (!root.TryGetProperty(name, out var v)) continue;
            var s = v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind == JsonValueKind.Number ? v.GetRawText() : null;
            if (s is { Length: > 0 and <= 40 } && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-')) return (name, s);
        }
        return (null, null);
    }

    /// <summary>
    /// The page's own fetch: the body comes back base64-encoded (binary-safe). A request the browser cannot complete
    /// (network error, unreadable cross-origin answer) is status 0.
    /// </summary>
    private const string FetchScript = """
        async (req) => {
          try {
            let body = req.body ?? undefined;
            if (req.form) { body = new FormData(); for (const k in req.form) body.append(k, req.form[k]); }
            const r = await fetch(req.url, { method: req.method, headers: req.headers, body, credentials: 'same-origin', redirect: 'follow', cache: 'no-store' });
            const buf = new Uint8Array(await r.arrayBuffer());
            let s = '';
            for (let i = 0; i < buf.length; i += 0x8000) s += String.fromCharCode.apply(null, buf.subarray(i, i + 0x8000));
            return { status: r.status, url: r.url, type: r.headers.get('content-type'), body: btoa(s) };
          } catch (e) {
            return { status: 0, url: null, type: null, body: '' };
          }
        }
        """;

    public async Task<BrowserFetchResponse> FetchAsync(BrowserFetchRequest request, CancellationToken ct)
    {
        try
        {
            var r = await _page.EvaluateFunctionAsync<JsonElement>(FetchScript, new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["method"] = request.Method,
                ["url"] = request.Url.ToString(),
                ["headers"] = request.Headers,
                ["body"] = request.Body,
                ["form"] = request.Form,
            }).WaitAsync(ct);
            var status = r.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.Number ? st.GetInt32() : 0;
            Uri? url = r.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String && Uri.TryCreate(u.GetString(), UriKind.Absolute, out var parsed) ? parsed : null;
            var type = r.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            var body = r.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? Convert.FromBase64String(b.GetString() ?? "") : [];
            return new BrowserFetchResponse(status, url, type, body);
        }
        catch (Exception ex) when (ex is PuppeteerException or JsonException or FormatException)
        {
            throw new BrowserActionException("The request could not run in the page.", ex);
        }
    }

    /// <summary>
    /// Requests <paramref name="url"/> from the signed-in page (its cookies, its fingerprint) with redirects not followed
    /// and the CDP Fetch domain pausing the answer. The paused answer's Location is read, the request is failed, and only
    /// then the address is returned, so the redirect target is never loaded by the browser.
    /// </summary>
    public async Task<Uri?> ResolveRedirectAsync(Uri url, CancellationToken ct)
    {
        var found = new TaskCompletionSource<Uri?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handled = 0;
        void OnPaused(object? sender, MessageEventArgs e)
        {
            if (e.MessageID != "Fetch.requestPaused") return;
            _ = HandlePausedAsync(e.MessageData);
        }
        async Task HandlePausedAsync(JsonElement p)
        {
            var id = p.GetProperty("requestId").GetString();
            var requested = p.TryGetProperty("request", out var r) && r.TryGetProperty("url", out var u) ? u.GetString() : null;
            // one interception: the page's request for this URL; anything else the pattern pauses is failed as well
            var mine = string.Equals(requested, url.ToString(), StringComparison.Ordinal) && Interlocked.Exchange(ref handled, 1) == 0;
            Uri? target = null;
            try
            {
                var status = p.TryGetProperty("responseStatusCode", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : 0;
                if (mine && status is >= 300 and < 400 && p.TryGetProperty("responseHeaders", out var headers) && headers.ValueKind == JsonValueKind.Array)
                {
                    foreach (var h in headers.EnumerateArray())
                    {
                        if (string.Equals(h.GetProperty("name").GetString(), "location", StringComparison.OrdinalIgnoreCase)
                            && Uri.TryCreate(url, h.GetProperty("value").GetString(), out var abs))
                            target = abs;
                    }
                }
                await _cdp.SendAsync("Fetch.failRequest", new { requestId = id, errorReason = "Aborted" });
            }
            catch (Exception ex) when (ex is PuppeteerException or InvalidOperationException or KeyNotFoundException)
            {
                target = null;
            }
            if (mine) found.TrySetResult(target); // only once the request was failed: it can never go on to the target
        }

        _cdp.MessageReceived += OnPaused;
        try
        {
            await _cdp.SendAsync("Fetch.enable", new
            {
                patterns = new[] { new { urlPattern = "*" + url.AbsolutePath + "*", requestStage = "Response" } },
            });
            // the page's fetch fails (the request is aborted, redirects are not followed); only the paused answer matters
            _ = _page.EvaluateFunctionAsync("u => fetch(u, { credentials: 'same-origin', cache: 'no-store', redirect: 'manual' }).catch(() => null)", url.ToString());
            return await found.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        }
        catch (TimeoutException)
        {
            return null;
        }
        catch (PuppeteerException ex)
        {
            throw new BrowserActionException("The browser could not follow the download link.", ex);
        }
        finally
        {
            _cdp.MessageReceived -= OnPaused;
            await TryAsync(() => _cdp.SendAsync("Fetch.disable"));
        }
    }

    public async Task<string> GetHtmlAsync()
    {
        try
        {
            return await _page.GetContentAsync();
        }
        catch (PuppeteerException ex)
        {
            throw new BrowserActionException("The page could not be read.", ex);
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
            var width = (int)Math.Round(D("deviceWidth"));
            var height = (int)Math.Round(D("deviceHeight"));
            if (width > 0) ViewportWidth = width;
            if (height > 0) ViewportHeight = height;
            pump.Post(new ScreencastFrame(data, width, height, false));
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
        return new ScreencastFrame(data, w > 0 ? w : ViewportWidth ?? _options.ViewportWidth, h > 0 ? h : ViewportHeight ?? _options.ViewportHeight, true);
    }

    // ---- input ----

    public Task InsertTextAsync(string text) => _cdp.SendAsync("Input.insertText", new { text });

    public Task DispatchMouseAsync(string type, double x, double y, string button, int clickCount, double deltaX, double deltaY, int buttons, int modifiers, double? timestamp = null)
    {
        var args = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["type"] = type,
            ["x"] = x,
            ["y"] = y,
            ["modifiers"] = modifiers,
        };
        if (timestamp is { } time) args["timestamp"] = time;
        if (type == "mouseWheel")
        {
            args["deltaX"] = deltaX;
            args["deltaY"] = deltaY;
        }
        else if (type == "mouseMoved")
        {
            args["buttons"] = buttons;
            args["button"] = (buttons & 1) != 0 ? "left" : (buttons & 4) != 0 ? "middle" : (buttons & 2) != 0 ? "right" : "none";
        }
        else
        {
            args["button"] = button;
            args["buttons"] = buttons;
            args["clickCount"] = Math.Max(1, clickCount);
        }
        return _cdp.SendAsync("Input.dispatchMouseEvent", args);
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
