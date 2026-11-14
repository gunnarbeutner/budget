using OpenBudget.Connectors;

namespace OpenBudget.Browser;

/// <summary>Creates isolated browser sessions. The real one drives Chromium; tests use a fake.</summary>
public interface IBrowserHost
{
    /// <summary>False when no Chromium is installed: web logins then report "browser not available" and nothing else is affected.</summary>
    bool IsAvailable { get; }

    BrowserOptions Options { get; }

    /// <summary>Opens a fresh, ephemeral session: its own cookie storage, no profile on disk, one page.</summary>
    Task<IBrowserSession> OpenSessionAsync(CancellationToken ct);
}

/// <summary>One isolated browser context with a single page. Disposing it destroys cookies and everything else.</summary>
public interface IBrowserSession : IAsyncDisposable
{
    /// <summary>Address of the page.</summary>
    Uri Url { get; }

    /// <summary>The User-Agent the page sees; requests that continue the session should send the same.</summary>
    string UserAgent => "";

    /// <summary>The native page dimensions, when available, for streamed frames and input coordinates.</summary>
    int? ViewportWidth => null;

    int? ViewportHeight => null;

    Task NavigateAsync(Uri url, CancellationToken ct);

    /// <summary>Types a one-time code into a visible single field or a group of one-character fields.</summary>
    Task<bool> FillCodeAsync(string selector, string code) => Task.FromResult(false);

    /// <summary>Puts cookies into the context (before the first navigation).</summary>
    Task SetCookiesAsync(IReadOnlyList<CookieState> cookies);

    /// <summary>Every cookie of the context, with expiry, flags and SameSite.</summary>
    Task<IReadOnlyList<CookieState>> GetCookiesAsync();

    Task StartScreencastAsync(Func<ScreencastFrame, Task> onFrame, int maxWidth, int maxHeight, int quality);

    Task ResizeScreencastAsync(int maxWidth, int maxHeight, int quality);

    Task StopScreencastAsync();

    Task<ScreencastFrame?> CaptureStillAsync(int quality, double scale);

    /// <summary>type: mousePressed | mouseReleased | mouseMoved | mouseWheel; coordinates in CSS px of the page.</summary>
    Task DispatchMouseAsync(string type, double x, double y, string button, int clickCount, double deltaX, double deltaY, int buttons, int modifiers, double? timestamp = null);

    /// <summary>type: keyDown | keyUp | rawKeyDown | char.</summary>
    Task DispatchKeyAsync(string type, string key, string? code, string? text, int modifiers, string[]? commands);

    Task InsertTextAsync(string text);

    /// <summary>One step back in the page's history; nothing happens when there is none.</summary>
    Task GoBackAsync();

    Task ReloadAsync();

    /// <summary>Waits up to <paramref name="timeout"/> for a visible element, then replaces its text by typing <paramref name="value"/>. False when none appeared.</summary>
    Task<bool> FillAsync(string selector, string value, TimeSpan timeout);

    /// <summary>Clicks the first visible element; false when there is none.</summary>
    Task<bool> ClickAsync(string selector);

    Task<bool> IsVisibleAsync(string selector);

    /// <summary>Ticks the first visible checkbox matching <paramref name="selector"/> unless it is ticked already; false when there is none.</summary>
    Task<bool> TickAsync(string selector) => Task.FromResult(false);

    /// <summary>True when a visible element matching <paramref name="selector"/> contains one of <paramref name="texts"/> (case-insensitive; empty: any).</summary>
    Task<bool> HasVisibleTextAsync(string selector, IReadOnlyList<string> texts);

    /// <summary>
    /// Enters one character into a single-character box (an access code digit): focuses it and inserts the character
    /// without any key events, so a keyup can never land on the box the page auto-advanced to. False when the box did not
    /// appear or does not hold the character afterwards.
    /// </summary>
    Task<bool> TypeCharacterAsync(string selector, string character, TimeSpan timeout) => FillAsync(selector, character, timeout);

    /// <summary>
    /// Runs <paramref name="function"/> (a JavaScript function taking one string argument) in the page and returns its
    /// {ok, reason} result. The argument may be a secret; only the boolean and the reason (which the script must keep free
    /// of secrets) come back. A script error counts as not ok.
    /// </summary>
    Task<WebLoginCheck> CheckAsync(string function, string argument) => Task.FromResult(new WebLoginCheck(true, ""));

    /// <summary>The page's current document as HTML (its DOM serialised, after the page's scripts ran).</summary>
    Task<string> GetHtmlAsync() => Task.FromResult("");

    /// <summary>Runs a request with the page's own <c>fetch</c> (same-origin credentials, redirects followed in the browser).</summary>
    Task<BrowserFetchResponse> FetchAsync(BrowserFetchRequest request, CancellationToken ct) =>
        throw new NotSupportedException("This browser cannot run requests in the page.");

    /// <summary>See <see cref="IBrowserFetcher.ResolveRedirectAsync"/>.</summary>
    Task<Uri?> ResolveRedirectAsync(Uri url, CancellationToken ct) => Task.FromResult<Uri?>(null);

    /// <summary>Calls <paramref name="onResponse"/> with the status (and a short error code, if any) of each response whose URL contains one of <paramref name="urlParts"/>.</summary>
    void ObserveResponses(IReadOnlyList<string> urlParts, Action<ObservedResponse> onResponse)
    {
    }

    /// <summary>
    /// Calls <paramref name="onUrl"/> with every address starting with <paramref name="prefix"/> (case-insensitive) that the
    /// page is sent to: a request, a redirect's Location or a navigation, including addresses of schemes the browser cannot
    /// open (an app's own callback scheme). The browser does not load them.
    /// </summary>
    Task ObserveRedirectsAsync(string prefix, Action<Uri> onUrl) => Task.CompletedTask;
}

/// <summary>Result of an in-page check: never carries secrets.</summary>
public sealed record WebLoginCheck(bool Ok, string Reason);

/// <summary>A watched response: its path, HTTP status and, when the body had one, a short top-level error code (field name and value).</summary>
public sealed record ObservedResponse(string Path, int Status, string? ErrorField, string? ErrorCode);
