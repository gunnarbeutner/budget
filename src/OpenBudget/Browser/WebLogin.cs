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

    /// <summary>The longest a browser kept after the login (<see cref="WebLoginSpec.KeepBrowser"/>) stays open.</summary>
    public TimeSpan KeptBrowserLifetime { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>The longest an unattended (background) web login may take before it counts as needing the owner.</summary>
    public TimeSpan UnattendedTimeout { get; set; } = TimeSpan.FromMinutes(2);
}

/// <summary>How a web login runs when nobody watches (a background sync).</summary>
public enum WebLoginUnattended
{
    /// <summary>The owner is there: the page is shown whenever it needs them.</summary>
    None,

    /// <summary>Only the stored session may be reused (<see cref="WebLoginSpec.ResumeUrl"/>); nothing is typed or submitted.</summary>
    ResumeOnly,

    /// <summary>
    /// May also fill in the stored credentials (and a code from a stored TOTP secret); anything else the page wants
    /// (a captcha, an SMS or app code, a security check, an unknown page) ends the login for the owner.
    /// </summary>
    SignIn,
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
public sealed record WebLoginResult(bool Success, string? Error, IReadOnlyList<CookieState> Cookies, bool CredentialsRejected = false, string? UserAgent = null)
{
    public bool HasCookieSnapshot { get; init; }

    public static WebLoginResult Ok(IReadOnlyList<CookieState> cookies, string? userAgent = null) => new(true, null, cookies, UserAgent: userAgent);

    public static WebLoginResult Fail(string error) => new(false, error, []);

    public static WebLoginResult Rejected(string error) => new(false, error, [], CredentialsRejected: true);

    /// <summary>An unattended login stopped where the page needs the owner; nothing more was submitted.</summary>
    public bool NeedsUser { get; init; }

    public static WebLoginResult ForUser(string reason) => new(false, reason, []) { NeedsUser = true };
}

/// <summary>An unattended web login reached a page that needs the owner.</summary>
public sealed class UnattendedStopException : Exception
{
    public UnattendedStopException()
    {
    }

    public UnattendedStopException(string message) : base(message)
    {
    }

    public UnattendedStopException(string message, Exception innerException) : base(message, innerException)
    {
    }
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
    Task<WebLoginSession> StartAsync(string ownerUserId, WebLoginSpec spec, IReadOnlyList<CookieState> seed, CancellationToken ct,
        Func<ConnectorChallenge, CancellationToken, Task<ChallengeAnswer?>>? askForCode = null, WebLoginUnattended unattended = WebLoginUnattended.None);

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

    public async Task<WebLoginSession> StartAsync(string ownerUserId, WebLoginSpec spec, IReadOnlyList<CookieState> seed, CancellationToken ct,
        Func<ConnectorChallenge, CancellationToken, Task<ChallengeAnswer?>>? askForCode = null, WebLoginUnattended unattended = WebLoginUnattended.None)
    {
        if (!_host.IsAvailable) throw new BrowserActionException("No browser is available on this server.");
        var browser = await _host.OpenSessionAsync(ct);
        var now = _clock.GetUtcNow();
        var session = new WebLoginSession(
            ownerUserId, spec, browser, _host.Options, _options, now.UtcDateTime + _options.Timeout,
            _loggers.CreateLogger<WebLoginSession>(), s => _sessions.TryRemove(s.Id, out _))
        {
            Clock = _clock,
            AskForCode = unattended == WebLoginUnattended.None ? askForCode : null,
            Unattended = unattended,
        };
        _sessions[session.Id] = session;
        session.Begin(seed.Where(c => spec.Matches(c) && (c.ExpiresUtc == null || c.ExpiresUtc > now.UtcDateTime)).ToList());
        return session;
    }

    public WebLoginSession? Find(string sessionId, string userId) =>
        _sessions.TryGetValue(sessionId, out var s) && string.Equals(s.OwnerUserId, userId, StringComparison.Ordinal) ? s : null;
}

/// <summary>
/// One web login: an isolated browser context that navigates to the bank, waits for the success condition, hands back the
/// cookies and then destroys the context. Nothing of it outlives <see cref="Completion"/>, except a browser kept for the
/// connector (<see cref="WebLoginSpec.KeepBrowser"/>): <see cref="TakeBrowser"/> hands it over, and disposing the login
/// destroys it when nobody took it.
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
#pragma warning disable CA2213 // disposed in DisposeAsync unless taken; whoever took it disposes it
    private KeptBrowser? _kept;
#pragma warning restore CA2213

    internal TimeProvider Clock { get; init; } = TimeProvider.System;

    internal Func<ConnectorChallenge, CancellationToken, Task<ChallengeAnswer?>>? AskForCode { get; init; }

    /// <summary>Whether nobody watches this login (see <see cref="WebLoginUnattended"/>).</summary>
    internal WebLoginUnattended Unattended { get; init; }

    /// <summary>
    /// The signed-in browser after a successful login whose spec keeps it (null otherwise, or once taken). The caller owns
    /// it from then on and must dispose it.
    /// </summary>
    public KeptBrowser? TakeBrowser() => Interlocked.Exchange(ref _kept, null);

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

    private void SetMode(WebLoginMode mode, bool notify = false)
    {
        // nobody can act on the page: stop here, before anything more is typed or submitted
        if (mode == WebLoginMode.Interactive && Unattended != WebLoginUnattended.None)
            throw new UnattendedStopException("The bank's page wants something from you; sync it once yourself.");
        if (Mode == mode && !notify) return;
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
            _cts.CancelAfter(Unattended == WebLoginUnattended.None ? _options.Timeout : _options.UnattendedTimeout);
            var ct = _cts.Token;
            if (_spec.ObservedRequests.Count > 0)
                _browser.ObserveResponses(_spec.ObservedRequests, r => _logger.LogInformation(
                    "Web login: {Path} answered {Status}{Code}.", r.Path, r.Status,
                    r.ErrorField != null ? $" ({r.ErrorField}={r.ErrorCode})" : ""));
            await _browser.SetCookiesAsync(seed);
            _ready.TrySetResult(); // a viewer may attach now and watch the page load
            if (seed.Count > 0 && _spec.ResumeUrl != null && await ResumeAsync(_spec.ResumeUrl, ct) is { } resumed)
            {
                _ready.TrySetCanceled();
                await EndAsync(resumed);
                return;
            }
            if (Unattended == WebLoginUnattended.ResumeOnly || (Unattended == WebLoginUnattended.SignIn && _spec.Autofill == null))
            {
                _ready.TrySetCanceled();
                await EndAsync(WebLoginResult.ForUser("The stored session has expired; sync it once yourself."));
                return;
            }
            await _browser.NavigateAsync(_spec.StartUrl, ct);
            if (_spec.Autofill is { Username.Length: > 0, Password.Length: > 0 } fill)
                await AutofillAsync(fill, ct);
            else SetMode(WebLoginMode.Interactive);
            result = await WatchAsync(ct);
        }
        catch (UnattendedStopException ex)
        {
            _logger.LogInformation(ex, "Web login: stopped unattended on {Path}: the page needs the owner.", SafePath(_browser.Url));
            result = WebLoginResult.ForUser(ex.Message);
        }
        catch (OperationCanceledException) when (Unattended != WebLoginUnattended.None && !_userCancelled && _cts.IsCancellationRequested)
        {
            result = WebLoginResult.ForUser("The sign-in did not finish by itself; sync it once yourself.");
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
    /// Opens the spec's signed-in page with the stored cookies; the login result when the full success condition holds
    /// there (no form, no second factor), else null.
    /// </summary>
    private async Task<WebLoginResult?> ResumeAsync(Uri url, CancellationToken ct)
    {
        await _browser.NavigateAsync(url, ct);
        if (!await SignedInAsync() || (_spec.SuccessCheck is { } check && !(await _browser.CheckAsync(check, "")).Ok))
        {
            _logger.LogInformation("Web login: the stored session does not open {Path}; signing in.", SafePath(url));
            return null;
        }
        _logger.LogInformation("Web login: the stored session is still signed in.");
        return await SucceededAsync(ct);
    }

    private async Task<WebLoginResult> SucceededAsync(CancellationToken ct)
    {
        if (_options.SettleDelay > TimeSpan.Zero) await Task.Delay(_options.SettleDelay, ct);
        var all = await _browser.GetCookiesAsync();
        _logger.LogInformation("Web login succeeded; exporting {Count} cookies of {Domains}.", all.Count(_spec.Matches), string.Join(", ", _spec.CookieDomains));
        return WebLoginResult.Ok(all.Where(_spec.Matches).ToList(), _browser.UserAgent is { Length: > 0 } ua ? ua : null);
    }

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
        var entered = new HashSet<WebLoginCredential>();
        for (var i = 0; i < fill.Steps.Count; i++)
        {
            var step = fill.Steps[i];
            var label = $"step {i + 1} of {fill.Steps.Count}";
            if (step.SkipWhenEntered && step.Fields.All(f => entered.Contains(f.Credential)))
            {
                _logger.LogInformation("Web login: {Step} skipped; its fields were on an earlier page.", label);
                continue;
            }
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
                if (field.Optional && field.CharacterBoxes == 0)
                {
                    if (await _browser.FillAsync(boxes[0], value, f == 0 ? _options.FormTimeout : TimeSpan.FromSeconds(2))) entered.Add(field.Credential);
                    else _logger.LogInformation("Web login: {Step}: the optional field {Selector} is not on this page.", label, boxes[0]);
                    continue;
                }
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
                entered.Add(field.Credential);
            }
            if (step.Verify is { } verify && !await VerifyAsync(verify, fill.ValueOf(verify.Credential), label))
                return;
            await TickAsync(step.Tick);
            if (!await ClickWhenReadyAsync(step.SubmitSelector, ct))
            {
                if (!step.EnterSubmits)
                {
                    ShowPage($"{label}: no enabled button matched {step.SubmitSelector}");
                    return;
                }
                // the last filled field still has the focus: Enter submits its form like a person would
                _logger.LogInformation("Web login: {Step}: no button matched {Selector}; pressing Enter.", label, step.SubmitSelector);
                await _browser.DispatchKeyAsync("keyDown", "Enter", "Enter", "\r", 0, null);
                await _browser.DispatchKeyAsync("keyUp", "Enter", "Enter", null, 0, null);
            }
            _logger.LogInformation("Web login: submitted {Step} of the sign-in form.", label);
        }
        _submittedAt = DateTime.UtcNow;
        _logger.LogInformation("Web login: submitted the sign-in form; waiting for the bank.");
        SetMode(WebLoginMode.Waiting);
    }

    /// <summary>Ticks the shown ones of these checkboxes (see <see cref="WebLoginStep.Tick"/>).</summary>
    private async Task TickAsync(IReadOnlyList<string> selectors)
    {
        foreach (var selector in selectors)
            if (await _browser.TickAsync(selector)) _logger.LogInformation("Web login: ticked {Selector}.", selector);
    }

    private void ShowPage(string reason, bool notify = false)
    {
        _logger.LogInformation("Web login: {Reason} on {Path}; showing the page.", reason, SafePath(_browser.Url));
        SetMode(WebLoginMode.Interactive, notify);
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
            if (await HandleCodePromptAsync(url, ct)) continue;
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
            if (_spec.WaitingScript is { } waiting && (_submittedAt != null || Mode == WebLoginMode.Interactive)
                && await RunWaitingScriptAsync(waiting, url) is { } ended)
                return ended;
            if (!_spec.UrlMatches(url)) continue;
            IReadOnlyList<CookieState> cookies = _spec.SuccessWhen != null ? await _browser.GetCookiesAsync() : [];
            if (!_spec.IsSuccess(url, cookies)) continue;
            if (_spec.SuccessCheck is { } check && await _browser.CheckAsync(check, "") is { Ok: false } failed)
            {
                if (Mode != WebLoginMode.Interactive)
                {
                    _logger.LogInformation("Web login: landed on {Path}, but the bank still wants something on the page; showing it.{Reason}",
                        SafePath(url), failed.Reason.Length > 0 ? " " + failed.Reason : "");
                    SetMode(WebLoginMode.Interactive);
                }
                continue;
            }
            return await SucceededAsync(ct);
        }
        ct.ThrowIfCancellationRequested();
        return WebLoginResult.Fail("The web login ended.");
    }

    private bool _codePromptDisabled;
    private DateTime? _codeSubmittedAt;

    // codes entered from the stored TOTP secret; once they were rejected the shared prompt takes over
    private int _totpAttempts;
    private bool _totpRejected;
    private bool _authenticatorChosen;

    // since when the code page has been waited on for the stored secret without a form to act on (still loading, say)
    private DateTime? _totpIdleSince;

    private async Task<bool> HandleCodePromptAsync(Uri url, CancellationToken ct)
    {
        if (_codePromptDisabled || _spec.CodePrompt is not { } form || !IsCodePage(url, form))
        {
            _totpIdleSince = null;
            return false;
        }
        var totp = _totpRejected ? null : form.Totp;
        if (AskForCode == null && totp == null) return false;
        var settle = _totpAttempts > 0 && !_totpRejected ? form.TotpSettle : TimeSpan.FromSeconds(2);
        if (_codeSubmittedAt is { } submitted && DateTime.UtcNow - submitted < settle) return true;
        if (totp != null && !_authenticatorChosen && form.ChooseAuthenticator.Count > 0 && !await _browser.IsVisibleAsync(form.FieldSelector)
            && await ChooseAuthenticatorAsync(form, ct))
        {
            _totpIdleSince = null;
            return true;
        }
        if (!await _browser.IsVisibleAsync(form.FieldSelector) || !await _browser.IsVisibleAsync(form.SubmitSelector))
            return totp != null && KeepHiddenForSecret();
        _totpIdleSince = null;

        string code;
        var fromSecret = false;
        string? notice = null;
        if (totp != null && TotpDecision.Next(_totpAttempts, totp, Clock.GetUtcNow()) is { } generated)
        {
            code = generated;
            fromSecret = true;
            _totpAttempts++;
            _logger.LogInformation("Web login: entering the authenticator code from the stored TOTP secret (attempt {Attempt}).", _totpAttempts);
        }
        else
        {
            if (totp != null)
            {
                _totpRejected = true;
                notice = form.TotpRejectedMessage;
                _logger.LogWarning("Web login: the site did not accept the codes from the stored TOTP secret.");
                if (AskForCode == null)
                {
                    _codePromptDisabled = true;
                    ShowPage(notice, notify: true);
                    return true;
                }
            }
            var message = notice == null ? form.Message : notice + " " + form.Message;
            do
            {
                var answer = await AskForCode!(ConnectorChallenge.Code(message, form.Length, form.Numeric, Id), ct);
                code = answer?.Value?.Trim() ?? throw new BrowserActionException("The security code was not provided in time.");
            } while (code.Length != form.Length || form.Numeric && !code.All(char.IsAsciiDigit));
        }
        if (IsCodePage(_browser.Url, form)) await TickAsync(form.Tick);
        if (!IsCodePage(_browser.Url, form) || !await _browser.FillCodeAsync(form.FieldSelector, code) || !await ClickWhenReadyAsync(form.SubmitSelector, ct))
        {
            _codePromptDisabled = true;
            ShowPage("the security-code form could not be filled; showing the bank's page", notify: true);
            return true;
        }
        _codeSubmittedAt = DateTime.UtcNow;
        // Restore the web-login status after the shared code challenge, even when the mode was already Waiting.
        SetMode(WebLoginMode.Waiting, notify: !fromSecret);
        return true;
    }

    /// <summary>
    /// With a stored secret the app enters the code itself, so the page stays hidden (the spec's waiting script is not
    /// consulted) while the code form is not there yet; only after <see cref="WebLoginOptions.StuckAfter"/> without a form
    /// to act on does the code page count as needing the user.
    /// </summary>
    private bool KeepHiddenForSecret()
    {
        _totpIdleSince ??= DateTime.UtcNow;
        return DateTime.UtcNow - _totpIdleSince < _options.StuckAfter;
    }

    /// <summary>Picks the authenticator app on a page that offers several methods (once); true when something was clicked.</summary>
    private async Task<bool> ChooseAuthenticatorAsync(WebLoginCodePrompt form, CancellationToken ct)
    {
        foreach (var selector in form.ChooseAuthenticator)
        {
            if (!await _browser.IsVisibleAsync(selector)) continue;
            _authenticatorChosen = true;
            if (!await _browser.ClickAsync(selector)) return false;
            _logger.LogInformation("Web login: chose the authenticator app as the security check.");
            if (await _browser.IsVisibleAsync(form.SubmitSelector)) await ClickWhenReadyAsync(form.SubmitSelector, ct);
            _codeSubmittedAt = DateTime.UtcNow;
            return true;
        }
        return false;
    }

    private bool IsCodePage(Uri url, WebLoginCodePrompt form) =>
        string.Equals(url.GetLeftPart(UriPartial.Authority), _spec.StartUrl.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase)
        && url.AbsolutePath.StartsWith(form.PathPrefix, StringComparison.OrdinalIgnoreCase);

    // the page was shown because the spec's waiting script asked for the user; it hides it again once the script acts
    private bool _shownByScript;

    private string? _lastWaitingReason;

    /// <summary>Runs the spec's <see cref="WebLoginSpec.WaitingScript"/> once; a result when it ended the sign-in.</summary>
    private async Task<WebLoginResult?> RunWaitingScriptAsync(string script, Uri url)
    {
        var r = await _browser.CheckAsync(script, _spec.WaitingArgument);
        if (r.Ok && r.Reason.StartsWith("waiting:", StringComparison.Ordinal))
        {
            // the bank waits for a confirmation elsewhere (an app): nothing to do on the page, so it is hidden
            if (Mode == WebLoginMode.Interactive)
            {
                _logger.LogInformation("Web login: {Reason} on {Path}; hiding the page.", r.Reason[8..].Trim(), SafePath(url));
                _shownByScript = false;
                _shownBecauseStuck = false;
                SetMode(WebLoginMode.Waiting);
            }
            return null;
        }
        if (r.Ok)
        {
            if (r.Reason.Length == 0) return null;
            _logger.LogInformation("Web login: {Reason} on {Path}.", r.Reason, SafePath(url));
            if (_shownByScript && Mode == WebLoginMode.Interactive)
            {
                _shownByScript = false;
                SetMode(WebLoginMode.Waiting);
            }
            return null;
        }
        if (r.Reason.StartsWith("end:", StringComparison.Ordinal))
        {
            var message = r.Reason[4..].Trim();
            _logger.LogWarning("Web login: ended by the page's state on {Path}: {Reason}", SafePath(url), message);
            return WebLoginResult.Fail(message.Length > 0 ? message : "The sign-in did not complete.");
        }
        if (Mode != WebLoginMode.Interactive)
        {
            _logger.LogInformation("Web login: the page needs the user ({Reason}) on {Path}; showing it.", r.Reason, SafePath(url));
            _shownByScript = true;
            SetMode(WebLoginMode.Interactive);
        }
        else if (!string.Equals(_lastWaitingReason, r.Reason, StringComparison.Ordinal))
        {
            _logger.LogInformation("Web login: the page still needs the user ({Reason}) on {Path}.", r.Reason, SafePath(url));
        }
        _lastWaitingReason = r.Reason;
        return null;
    }

    private async Task EndAsync(WebLoginResult result)
    {
        if (Interlocked.Exchange(ref _ended, 1) == 1) return;
        _onEnded(this);
        lock (_viewerGate) _cancelViewer?.Invoke();
        if (!result.Success)
        {
            try
            {
                var cookies = await _browser.GetCookiesAsync().WaitAsync(TimeSpan.FromSeconds(5));
                result = result with
                {
                    Cookies = cookies.Where(_spec.Matches).ToList(),
                    HasCookieSnapshot = true,
                    UserAgent = _browser.UserAgent is { Length: > 0 } ua ? ua : null,
                };
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Could not collect cookies from the interrupted web login."); }
        }
        if (result.Success && _spec.KeepBrowser)
        {
            // nobody can attach a viewer any more (the login is gone from the service); the page stays signed in
            _kept = new KeptBrowser(_browser, _options.KeptBrowserLifetime, Clock, _logger);
        }
        else
        {
            try { await _browser.DisposeAsync(); }
            catch (Exception ex) { _logger.LogDebug(ex, "Destroying the browser context failed."); }
        }
        _result.TrySetResult(result);
    }

    /// <summary>Ends the login now (the sync was cancelled or finished) and destroys its browser, unless it was taken.</summary>
    public async ValueTask DisposeAsync()
    {
        await CancelAsync();
        if (TakeBrowser() is { } untaken) await untaken.DisposeAsync();
        _cts.Dispose();
    }

    internal async Task CancelAsync()
    {
        _userCancelled = true;
        await _cts.CancelAsync();
        if (_run != null) await _run;
        else await EndAsync(WebLoginResult.Fail("The web login was cancelled."));
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
