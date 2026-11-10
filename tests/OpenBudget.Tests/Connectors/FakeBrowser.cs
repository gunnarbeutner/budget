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

    /// <summary>How long a fill waits for its field to appear (see <see cref="FakeBrowserSession.FieldWait"/>).</summary>
    public TimeSpan FieldWait { get; set; } = TimeSpan.FromSeconds(2);

    public Task<IBrowserSession> OpenSessionAsync(CancellationToken ct)
    {
        var session = new FakeBrowserSession(User) { FieldWait = FieldWait };
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

    public Task GoBackAsync() => Task.CompletedTask;

    public Task ReloadAsync() => Task.CompletedTask;

    /// <summary>Selectors the fake page shows (a sign-in form, say); empty: no form.</summary>
    public HashSet<string> Visible { get; } = new(StringComparer.Ordinal);

    /// <summary>What was typed into which field.</summary>
    public Dictionary<string, string> Filled { get; } = new(StringComparer.Ordinal);

    public List<string> Clicked { get; } = [];

    /// <summary>Runs (under the page lock) after a field was filled: a React-like page enables its button only now.</summary>
    public Action<FakeBrowserSession, string>? OnFill { get; set; }

    /// <summary>Runs when something is clicked (the fake bank reacts to the submit button).</summary>
    public Func<FakeBrowserSession, string, Task>? OnClick { get; set; }

    /// <summary>The most a fill waits for its field (the real browser waits for the page's script). A rig whose page never
    /// shows a form (the user signs in by hand) sets it to zero: waiting could only ever end in "no field".</summary>
    public TimeSpan FieldWait { get; init; } = TimeSpan.FromSeconds(2);

    public async Task<bool> FillAsync(string selector, string value, TimeSpan timeout)
    {
        // like the real browser: wait for the field (the page's script may still be loading), at most a moment in tests
        var until = DateTime.UtcNow + (timeout < FieldWait ? timeout : FieldWait);
        while (true)
        {
            lock (Visible)
            {
                if (Visible.Contains(selector))
                {
                    Filled[selector] = value;
                    OnFill?.Invoke(this, selector);
                    return true;
                }
            }
            if (DateTime.UtcNow >= until) return false;
            await Task.Delay(20);
        }
    }

    public async Task<bool> ClickAsync(string selector)
    {
        lock (Visible)
        {
            if (!Visible.Contains(selector)) return false;
            Clicked.Add(selector);
        }
        if (OnClick != null) await OnClick(this, selector);
        return true;
    }

    public Task<bool> IsVisibleAsync(string selector)
    {
        lock (Visible) return Task.FromResult(Visible.Contains(selector));
    }

    /// <summary>Visible text per selector (the bank's error box, say).</summary>
    public Dictionary<string, string> Texts { get; } = new(StringComparer.Ordinal);

    public Task<bool> HasVisibleTextAsync(string selector, IReadOnlyList<string> texts)
    {
        lock (Visible)
        {
            return Task.FromResult(Texts.TryGetValue(selector, out var t)
                && (texts.Count == 0 || texts.Any(x => t.Contains(x, StringComparison.OrdinalIgnoreCase))));
        }
    }

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
