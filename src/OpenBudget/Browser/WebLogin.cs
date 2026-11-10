using System.Net.WebSockets;
using OpenBudget.Connectors;

namespace OpenBudget.Browser;

public sealed class WebLoginOptions
{
    /// <summary>How long a web login may stay open before its browser is destroyed.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(10);

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Wait after the success condition was met, so cookies set by the landing page's scripts are included.</summary>
    public TimeSpan SettleDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>How long autofill waits for the sign-in form to appear.</summary>
    public TimeSpan FormTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>When the sign-in form is still (or again) there this long after submitting, the user is shown the page (wrong password, CAPTCHA, ...).</summary>
    public TimeSpan StuckAfter { get; set; } = TimeSpan.FromSeconds(8);
}

/// <summary>What the owner sees of a web login.</summary>
public enum WebLoginMode
{
    /// <summary>The app is signing in by itself (page loading, form being filled): a status line, no page.</summary>
    Starting,

    /// <summary>The form was submitted and the bank waits (an app approval, say): the connector's waiting message, no page.</summary>
    Waiting,

    /// <summary>The user acts on the page: the remote browser is shown.</summary>
    Interactive,
}

/// <summary>Outcome of a web login. On success <see cref="Cookies"/> holds every cookie of the connector's domains.</summary>
public sealed record WebLoginResult(bool Success, string? Error, IReadOnlyList<CookieState> Cookies, bool CredentialsRejected = false)
{
    public static WebLoginResult Ok(IReadOnlyList<CookieState> cookies) => new(true, null, cookies);

    public static WebLoginResult Fail(string error) => new(false, error, []);

    public static WebLoginResult Rejected(string error) => new(false, error, [], CredentialsRejected: true);
}

/// <summary>Starts web logins and lets only their owner attach a viewer.</summary>
public interface IWebLoginService
{
    /// <summary>False when no browser is installed.</summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Opens a fresh isolated browser for <paramref name="ownerUserId"/>, seeds it with <paramref name="seed"/> (filtered to the
    /// spec's domains) and starts watching for the success condition.
    /// </summary>
    Task<WebLoginSession> StartAsync(string ownerUserId, WebLoginSpec spec, IReadOnlyList<CookieState> seed, CancellationToken ct);

    /// <summary>The open web login with this id, only if it belongs to <paramref name="userId"/>.</summary>
    WebLoginSession? Find(string sessionId, string userId);
}

public sealed class WebLoginService : IWebLoginService
{
    private readonly IBrowserHost _host;
    private readonly WebLoginOptions _options;
    private readonly TimeProvider _clock;
    private readonly ILoggerFactory _loggers;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, WebLoginSession> _sessions = new(StringComparer.Ordinal);

    public WebLoginService(IBrowserHost host, WebLoginOptions options, TimeProvider clock, ILoggerFactory loggers)
    {
        _host = host;
        _options = options;
        _clock = clock;
        _loggers = loggers;
    }

    public bool IsAvailable => _host.IsAvailable;

    public async Task<WebLoginSession> StartAsync(string ownerUserId, WebLoginSpec spec, IReadOnlyList<CookieState> seed, CancellationToken ct)
    {
        if (!_host.IsAvailable) throw new BrowserActionException("No browser is available on this server.");
        var browser = await _host.OpenSessionAsync(ct);
        var now = _clock.GetUtcNow();
        var session = new WebLoginSession(
            ownerUserId, spec, browser, _host.Options, _options, now.UtcDateTime + _options.Timeout,
            _loggers.CreateLogger<WebLoginSession>(), s => _sessions.TryRemove(s.Id, out _));
        _sessions[session.Id] = session;
        session.Begin(seed.Where(c => spec.Matches(c) && (c.ExpiresUtc == null || c.ExpiresUtc > now.UtcDateTime)).ToList());
        return session;
    }

    public WebLoginSession? Find(string sessionId, string userId) =>
        _sessions.TryGetValue(sessionId, out var s) && string.Equals(s.OwnerUserId, userId, StringComparison.Ordinal) ? s : null;
}

/// <summary>
/// One web login: an isolated browser context that navigates to the bank, waits for the success condition, hands back the
/// cookies and then destroys the context. Nothing of it outlives <see cref="Completion"/>.
/// </summary>
public sealed class WebLoginSession : IAsyncDisposable
{
    private readonly WebLoginSpec _spec;
    private readonly IBrowserSession _browser;
    private readonly BrowserOptions _browserOptions;
    private readonly WebLoginOptions _options;
    private readonly ILogger _logger;
    private readonly Action<WebLoginSession> _onEnded;
    private readonly TaskCompletionSource<WebLoginResult> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _cts = new();
    private readonly CancellationTokenSource _autofill = new();

    /// <summary>
    /// The user wants to sign in on the page themselves: automatic filling stops where it is, the page stays as it is,
    /// and the login keeps watching for the success condition.
    /// </summary>
    public void TakeOver()
    {
        if (_autofill.IsCancellationRequested) return;
        _logger.LogInformation("Web login: the user took over the page.");
        _autofill.Cancel();
        SetMode(WebLoginMode.Interactive);
    }
    private readonly Lock _viewerGate = new();
    private Action? _cancelViewer;
    private Task? _run;
    private int _ended;

    internal WebLoginSession(
        string ownerUserId, WebLoginSpec spec, IBrowserSession browser, BrowserOptions browserOptions, WebLoginOptions options,
        DateTime expiresAt, ILogger logger, Action<WebLoginSession> onEnded)
    {
        OwnerUserId = ownerUserId;
        _spec = spec;
        _browser = browser;
        _browserOptions = browserOptions;
        _options = options;
        ExpiresAt = expiresAt;
        _logger = logger;
        _onEnded = onEnded;
    }

    /// <summary>Unguessable (128 random bits); together with the owner check it authorises the viewer's WebSocket.</summary>
    public string Id { get; } = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));

    public string OwnerUserId { get; }

    public DateTime ExpiresAt { get; }

    /// <summary>What the owner should see now; <see cref="ModeChanged"/> reports changes.</summary>
    public WebLoginMode Mode { get; private set; }

    public event Action<WebLoginMode>? ModeChanged;

    private void SetMode(WebLoginMode mode)
    {
        if (Mode == mode) return;
        Mode = mode;
        try { ModeChanged?.Invoke(mode); }
        catch (Exception ex) { _logger.LogDebug(ex, "A web login mode listener failed."); }
    }

    private DateTime? _submittedAt;

    // the page was shown only because the form stayed after submitting: the bank's error box (which may come late) still ends the login
    private bool _shownBecauseStuck;

    /// <summary>Completes (never throws) when the login succeeded, failed, timed out or was cancelled; the browser is gone by then.</summary>
    public Task<WebLoginResult> Completion => _result.Task;

    internal void Begin(IReadOnlyList<CookieState> seed) => _run = Task.Run(() => RunAsync(seed));

    private async Task RunAsync(IReadOnlyList<CookieState> seed)
    {
        WebLoginResult result;
        try
        {
            _cts.CancelAfter(_options.Timeout);
            var ct = _cts.Token;
            await _browser.SetCookiesAsync(seed);
            _ready.TrySetResult(); // a viewer may attach now and watch the page load
            await _browser.NavigateAsync(_spec.StartUrl, ct);
            if (_spec.Autofill is { Username.Length: > 0, Password.Length: > 0 } fill)
            {
                using var autofillCt = CancellationTokenSource.CreateLinkedTokenSource(ct, _autofill.Token);
                try { await AutofillAsync(fill, autofillCt.Token); }
                catch (OperationCanceledException) when (_autofill.IsCancellationRequested && !ct.IsCancellationRequested) { /* the user took over */ }
            }
            else SetMode(WebLoginMode.Interactive);
            result = await WatchAsync(ct);
        }
        catch (OperationCanceledException)
        {
            result = WebLoginResult.Fail(_cts.IsCancellationRequested && !_userCancelled ? "The web login timed out." : "The web login was cancelled.");
        }
        catch (BrowserActionException ex)
        {
            result = WebLoginResult.Fail(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A web login failed unexpectedly.");
            result = WebLoginResult.Fail("The browser stopped working during the web login.");
        }
        _ready.TrySetCanceled();
        await EndAsync(result);
    }

    private bool _userCancelled;

    /// <summary>
    /// Types the stored credentials into the bank's form, step by step, and submits it. Anything unexpected (no form,
    /// no submit button) shows the page to the user instead; a page that is already signed in (the stored session
    /// still works) needs nothing. Selectors and the page path are logged, never the values.
    /// </summary>
    private async Task AutofillAsync(WebLoginAutofill fill, CancellationToken ct)
    {
        foreach (var selector in _spec.Dismiss)
        {
            if (!await WaitVisibleAsync(selector, TimeSpan.FromSeconds(3), ct)) continue;
            _logger.LogInformation("Web login: dismissing an interstitial ({Selector}).", selector);
            await _browser.ClickAsync(selector);
            await Task.Delay(_options.SettleDelay, ct);
        }
        for (var i = 0; i < fill.Steps.Count; i++)
        {
            var step = fill.Steps[i];
            var label = $"step {i + 1} of {fill.Steps.Count}";
            if (step.WaitFor != null && !await WaitVisibleAsync(step.WaitFor, _options.FormTimeout, ct))
            {
                if (i == 0 && await SignedInAsync()) return;
                ShowPage($"{label}: {step.WaitFor} did not appear");
                return;
            }
            for (var f = 0; f < step.Fields.Count; f++)
            {
                var field = step.Fields[f];
                // the first field of a step waits for the page (or the next page) to load; later ones are already there
                var value = fill.ValueOf(field.Credential);
                if (field.CharacterBoxes > 0 && value.Length != field.CharacterBoxes)
                {
                    ShowPage($"{label}: access code length doesn't match the form ({field.CharacterBoxes} boxes)");
                    return;
                }
                var boxes = field.BoxSelectors();
                for (var b = 0; b < boxes.Count; b++)
                {
                    var timeout = f == 0 && b == 0 ? _options.FormTimeout : TimeSpan.FromSeconds(5);
                    var filled = field.CharacterBoxes > 0
                        ? await _browser.TypeCharacterAsync(boxes[b], value[b].ToString(), timeout)
                        : await _browser.FillAsync(boxes[b], value, timeout);
                    if (!filled && field.CharacterBoxes > 0 && step.Verify != null)
                    {
                        // the step's verification checks what the page holds and can repair it, so carry on
                        _logger.LogInformation("Web login: {Step}: box {Box} did not take its character; the check before submitting decides.", label, b + 1);
                        filled = true;
                    }
                    if (!filled)
                    {
                        if (i == 0 && f == 0 && b == 0 && await SignedInAsync()) return; // signed in already: WatchAsync finishes
                        ShowPage($"{label}: no field matched {boxes[b]}");
                        return;
                    }
                    ct.ThrowIfCancellationRequested();
                }
            }
            if (step.Verify is { } verify && !await VerifyAsync(verify, fill.ValueOf(verify.Credential), label))
                return;
            if (i == fill.Steps.Count - 1 && _spec.ObservedRequests.Count > 0)
                _browser.ObserveResponses(_spec.ObservedRequests, r => _logger.LogInformation(
                    "Web login: {Path} answered {Status}{Code}.", r.Path, r.Status,
                    r.ErrorField != null ? $" ({r.ErrorField}={r.ErrorCode})" : ""));
            if (!await ClickWhenReadyAsync(step.SubmitSelector, ct))
            {
                ShowPage($"{label}: no enabled button matched {step.SubmitSelector}");
                return;
            }
            _logger.LogInformation("Web login: submitted {Step} of the sign-in form.", label);
        }
        _submittedAt = DateTime.UtcNow;
        _logger.LogInformation("Web login: submitted the sign-in form; waiting for the bank.");
        SetMode(WebLoginMode.Waiting);
    }

    private void ShowPage(string reason)
    {
        _logger.LogInformation("Web login: {Reason} on {Path}; showing the page.", reason, SafePath(_browser.Url));
        SetMode(WebLoginMode.Interactive);
    }

    /// <summary>Host and path of the page, without query or fragment (they may carry tokens).</summary>
    private static string SafePath(Uri url) => url.IsAbsoluteUri ? url.GetLeftPart(UriPartial.Path) : "?";

    /// <summary>
    /// True when the page already satisfies the full success condition (URL and cookies). The URL prefix alone is not
    /// enough: a bank's login page may itself live under the success prefix (Openbank's /amazon/login under /amazon/).
    /// </summary>
    private async Task<bool> SignedInAsync()
    {
        var url = _browser.Url;
        if (!_spec.UrlMatches(url)) return false;
        IReadOnlyList<CookieState> cookies = _spec.SuccessWhen != null ? await _browser.GetCookiesAsync() : [];
        return _spec.IsSuccess(url, cookies);
    }

    private async Task<bool> WaitVisibleAsync(string selector, TimeSpan timeout, CancellationToken ct)
    {
        var until = DateTime.UtcNow + timeout;
        while (true)
        {
            if (await _browser.IsVisibleAsync(selector)) return true;
            if (DateTime.UtcNow >= until) return false;
            await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
        }
    }

    /// <summary>
    /// Runs the step's pre-submit check; when it fails, lets the repair script try once and checks again. Only the
    /// check's reason (counts, never values) is logged. False (and the page is shown) when the step must not be submitted.
    /// </summary>
    private async Task<bool> VerifyAsync(WebLoginVerify verify, string value, string label)
    {
        var check = await _browser.CheckAsync(verify.Check, value);
        if (check.Ok) return true;
        _logger.LogInformation("Web login: {Step} check failed before submitting: {Reason}.", label, check.Reason);
        if (verify.Repair != null)
        {
            var repair = await _browser.CheckAsync(verify.Repair, value);
            if (!repair.Ok) _logger.LogInformation("Web login: {Step} repair reported: {Reason}.", label, repair.Reason);
            await Task.Delay(TimeSpan.FromMilliseconds(150)); // let the page re-render
            check = await _browser.CheckAsync(verify.Check, value);
            if (check.Ok)
            {
                _logger.LogInformation("Web login: {Step} repaired through the page's own handlers.", label);
                return true;
            }
        }
        ShowPage($"{label}: not submitted, {check.Reason}");
        return false;
    }

    /// <summary>
    /// Waits a few seconds for the page's script to enable the submit button, then clicks it exactly once: a click that
    /// fails half-way is never repeated (the first one may already have submitted the credentials).
    /// </summary>
    private async Task<bool> ClickWhenReadyAsync(string selector, CancellationToken ct)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!await _browser.IsVisibleAsync(selector))
        {
            if (DateTime.UtcNow >= until) return false;
            await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
        }
        return await _browser.ClickAsync(selector);
    }

    private async Task<WebLoginResult> WatchAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.PollInterval);
        while (await timer.WaitForNextTickAsync(ct))
        {
            var url = _browser.Url;
            // after the app submitted the stored credentials, the bank's own error message means they are wrong:
            // end now instead of waiting for an approval that will never come (the user did not type anything)
            if (_submittedAt != null && (Mode != WebLoginMode.Interactive || _shownBecauseStuck) && _spec.CredentialsError is { } error
                && await _browser.HasVisibleTextAsync(error.Selector, error.Texts))
            {
                _logger.LogWarning("Web login: the sign-in page reports wrong credentials ({Selector}) on {Path}.", error.Selector, SafePath(url));
                return WebLoginResult.Rejected(error.Message);
            }
            if (Mode == WebLoginMode.Waiting && _submittedAt is { } at && DateTime.UtcNow - at > _options.StuckAfter
                && _spec.Autofill is { } fill && !_spec.UrlMatches(url) && await _browser.IsVisibleAsync(fill.PasswordSelector))
            {
                _logger.LogInformation("Web login: the sign-in form is still shown after submitting; showing the page.");
                _shownBecauseStuck = true;
                SetMode(WebLoginMode.Interactive);
            }
            if (!_spec.UrlMatches(url)) continue;
            IReadOnlyList<CookieState> cookies = _spec.SuccessWhen != null ? await _browser.GetCookiesAsync() : [];
            if (!_spec.IsSuccess(url, cookies)) continue;
            if (_options.SettleDelay > TimeSpan.Zero) await Task.Delay(_options.SettleDelay, ct);
            var all = await _browser.GetCookiesAsync();
            _logger.LogInformation("Web login succeeded; exporting {Count} cookies of {Domains}.", all.Count(_spec.Matches), string.Join(", ", _spec.CookieDomains));
            return WebLoginResult.Ok(all.Where(_spec.Matches).ToList());
        }
        ct.ThrowIfCancellationRequested();
        return WebLoginResult.Fail("The web login ended.");
    }

    private async Task EndAsync(WebLoginResult result)
    {
        if (Interlocked.Exchange(ref _ended, 1) == 1) return;
        _onEnded(this);
        lock (_viewerGate) _cancelViewer?.Invoke();
        try { await _browser.DisposeAsync(); }
        catch (Exception ex) { _logger.LogDebug(ex, "Destroying the browser context failed."); }
        _result.TrySetResult(result);
    }

    /// <summary>Ends the login now (the sync was cancelled or finished) and destroys its browser.</summary>
    public async ValueTask DisposeAsync()
    {
        _userCancelled = true;
        await _cts.CancelAsync();
        if (_run != null) await _run;
        else await EndAsync(WebLoginResult.Fail("The web login was cancelled."));
        _cts.Dispose();
        _autofill.Dispose();
    }

    /// <summary>Streams the page to a viewer until the socket closes or the login ends. A newer viewer replaces an older one.</summary>
    public async Task ServeAsync(WebSocket socket, CancellationToken ct)
    {
        using var viewer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Action cancel = viewer.Cancel;
        lock (_viewerGate)
        {
            _cancelViewer?.Invoke();
            _cancelViewer = cancel;
        }
        try
        {
            await _ready.Task.WaitAsync(viewer.Token);
            await RemoteViewSession.ServeAsync(_browser, _browserOptions, _logger, socket, viewer.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException) { /* login ended or viewer replaced */ }
        finally
        {
            lock (_viewerGate) { if (_cancelViewer == cancel) _cancelViewer = null; }
            try
            {
                if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
            }
            catch (WebSocketException) { /* already gone */ }
        }
    }
}
