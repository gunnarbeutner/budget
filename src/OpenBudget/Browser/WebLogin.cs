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
public sealed record WebLoginResult(bool Success, string? Error, IReadOnlyList<CookieState> Cookies)
{
    public static WebLoginResult Ok(IReadOnlyList<CookieState> cookies) => new(true, null, cookies);

    public static WebLoginResult Fail(string error) => new(false, error, []);
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
            if (_spec.Autofill is { Username.Length: > 0, Password.Length: > 0 } fill) await AutofillAsync(fill, ct);
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
    /// Types the stored credentials into the bank's form and submits it. Anything unexpected (no form, no submit button)
    /// shows the page to the user instead; a page that is already signed in (the stored session still works) needs nothing.
    /// </summary>
    private async Task AutofillAsync(WebLoginAutofill fill, CancellationToken ct)
    {
        if (!await _browser.FillAsync(fill.UsernameSelector, fill.Username, _options.FormTimeout))
        {
            if (_spec.UrlMatches(_browser.Url)) return; // signed in already: WatchAsync finishes
            _logger.LogInformation("Web login: the sign-in form did not appear; showing the page.");
            SetMode(WebLoginMode.Interactive);
            return;
        }
        ct.ThrowIfCancellationRequested();
        if (!await _browser.FillAsync(fill.PasswordSelector, fill.Password, TimeSpan.FromSeconds(5)) || !await _browser.ClickAsync(fill.SubmitSelector))
        {
            _logger.LogInformation("Web login: the sign-in form could not be completed; showing the page.");
            SetMode(WebLoginMode.Interactive);
            return;
        }
        _submittedAt = DateTime.UtcNow;
        _logger.LogInformation("Web login: submitted the sign-in form; waiting for the bank.");
        SetMode(WebLoginMode.Waiting);
    }

    private async Task<WebLoginResult> WatchAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(_options.PollInterval);
        while (await timer.WaitForNextTickAsync(ct))
        {
            var url = _browser.Url;
            if (Mode == WebLoginMode.Waiting && _submittedAt is { } at && DateTime.UtcNow - at > _options.StuckAfter
                && _spec.Autofill is { } fill && !_spec.UrlMatches(url) && await _browser.IsVisibleAsync(fill.PasswordSelector))
            {
                _logger.LogInformation("Web login: the sign-in form is still shown after submitting; showing the page.");
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
