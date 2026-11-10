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

    Task NavigateAsync(Uri url, CancellationToken ct);

    /// <summary>Puts cookies into the context (before the first navigation).</summary>
    Task SetCookiesAsync(IReadOnlyList<CookieState> cookies);

    /// <summary>Every cookie of the context, with expiry, flags and SameSite.</summary>
    Task<IReadOnlyList<CookieState>> GetCookiesAsync();

    Task StartScreencastAsync(Func<ScreencastFrame, Task> onFrame, int maxWidth, int maxHeight, int quality);

    Task ResizeScreencastAsync(int maxWidth, int maxHeight, int quality);

    Task StopScreencastAsync();

    Task<ScreencastFrame?> CaptureStillAsync(int quality, double scale);

    /// <summary>type: mousePressed | mouseReleased | mouseMoved | mouseWheel; coordinates in CSS px of the page.</summary>
    Task DispatchMouseAsync(string type, double x, double y, string button, int clickCount, double deltaX, double deltaY, int buttons, int modifiers);

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

    /// <summary>True when a visible element matching <paramref name="selector"/> contains one of <paramref name="texts"/> (case-insensitive; empty: any).</summary>
    Task<bool> HasVisibleTextAsync(string selector, IReadOnlyList<string> texts);
}
