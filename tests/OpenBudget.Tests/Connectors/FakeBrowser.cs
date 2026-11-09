#pragma warning disable CA2000 // test fakes: lifetimes are owned by the tests
using OpenBudget.Browser;
using OpenBudget.Connectors;

namespace OpenBudget.Tests.Connectors;

/// <summary>A browser without Chromium: sessions record what the framework does with them and "sign in" through a script.</summary>
internal sealed class FakeBrowserHost : IBrowserHost
{
    public bool IsAvailable { get; set; } = true;

    public BrowserOptions Options { get; } = new();

    public List<FakeBrowserSession> Sessions { get; } = [];

    /// <summary>What the user does on the page once it has loaded (null: nothing, the login stays open).</summary>
    public Func<FakeBrowserSession, Task>? User { get; set; }

    public Task<IBrowserSession> OpenSessionAsync(CancellationToken ct)
    {
        var session = new FakeBrowserSession(User);
        lock (Sessions) Sessions.Add(session);
        return Task.FromResult<IBrowserSession>(session);
    }
}

internal sealed class FakeBrowserSession : IBrowserSession
{
    private readonly Func<FakeBrowserSession, Task>? _user;
    private readonly List<CookieState> _cookies = [];

    public FakeBrowserSession(Func<FakeBrowserSession, Task>? user) => _user = user;

    public Uri Url { get; set; } = new("about:blank");

    /// <summary>The cookies the framework put into the context before navigating.</summary>
    public List<CookieState> Seeded { get; } = [];

    public Uri? StartedAt { get; private set; }

    public bool Disposed { get; private set; }

    public List<(string Type, string Key)> Keys { get; } = [];

    public void SetCookie(CookieState cookie)
    {
        lock (_cookies)
        {
            _cookies.RemoveAll(c => c.Name == cookie.Name && c.Domain == cookie.Domain && c.Path == cookie.Path);
            _cookies.Add(cookie);
        }
    }

    public void DropCookie(string name)
    {
        lock (_cookies) _cookies.RemoveAll(c => c.Name == name);
    }

    public Task NavigateAsync(Uri url, CancellationToken ct)
    {
        StartedAt = url;
        Url = url;
        if (_user != null) _ = Task.Run(() => _user(this), CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task SetCookiesAsync(IReadOnlyList<CookieState> cookies)
    {
        Seeded.AddRange(cookies);
        foreach (var c in cookies) SetCookie(c);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<CookieState>> GetCookiesAsync()
    {
        lock (_cookies) return Task.FromResult<IReadOnlyList<CookieState>>([.. _cookies]);
    }

    public Task StartScreencastAsync(Func<ScreencastFrame, Task> onFrame, int maxWidth, int maxHeight, int quality) => Task.CompletedTask;

    public Task ResizeScreencastAsync(int maxWidth, int maxHeight, int quality) => Task.CompletedTask;

    public Task StopScreencastAsync() => Task.CompletedTask;

    public Task<ScreencastFrame?> CaptureStillAsync(int quality, double scale) => Task.FromResult<ScreencastFrame?>(null);

    public Task DispatchMouseAsync(string type, double x, double y, string button, int clickCount, double deltaX, double deltaY, int buttons, int modifiers) => Task.CompletedTask;

    public Task DispatchKeyAsync(string type, string key, string? code, string? text, int modifiers, string[]? commands)
    {
        lock (Keys) Keys.Add((type, key));
        return Task.CompletedTask;
    }

    public Task InsertTextAsync(string text) => Task.CompletedTask;

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}
