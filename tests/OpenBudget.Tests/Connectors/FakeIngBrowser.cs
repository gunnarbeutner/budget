#pragma warning disable CA2000 // test fakes: lifetimes are owned by the tests
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenBudget.Browser;
using OpenBudget.Connectors;
using OpenBudget.Connectors.Ing;

namespace OpenBudget.Tests.Connectors;

/// <summary>
/// A browser without Chromium for <see cref="FakeIng"/>: it loads pages through the fake (cookies, redirects), plays the
/// part of ING's sign-in script (the same JSON calls the real page makes) and answers the connector's in-page scripts
/// the way they behave in a real browser (the real scripts run in the E2E tests).
/// </summary>
internal sealed class FakeIngBrowserHost : IBrowserHost
{
    private readonly FakeIng _bank;

    public FakeIngBrowserHost(FakeIng bank) => _bank = bank;

    public bool IsAvailable => true;

    public BrowserOptions Options { get; } = new();

    /// <summary>The connector's pause before a sign-in step is submitted (what its pacing scripts wait for).</summary>
    public TimeSpan Pause { get; init; } = IngWebClient.DefaultPause;

    /// <summary>The device the user picks in the page when the app cannot choose (null: the user does nothing).</summary>
    public int? UserPicksDevice { get; set; }

    /// <summary>Times the page asked for the user (the reasons the waiting script gave).</summary>
    public List<string> ShownToUser { get; } = [];

    public int Opened { get; private set; }

    public Task<IBrowserSession> OpenSessionAsync(CancellationToken ct)
    {
        Opened++;
        return Task.FromResult<IBrowserSession>(new FakeIngBrowserSession(_bank, this));
    }
}

internal sealed partial class FakeIngBrowserSession : IBrowserSession
{
    private enum Phase
    {
        None,
        Form,
        Device,
        Confirm,
        Rejected,
    }

    private readonly HttpMessageInvoker _http;
    private readonly FakeIngBrowserHost _host;
    private readonly List<CookieState> _cookies = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Phase _phase;
    private string _alias = "";
    private string _pin = "";
    private List<string> _devices = [];
    private DateTime _shownAt;
    private string? _devicePicked;
    private bool _loginError;

    public FakeIngBrowserSession(FakeIng bank, FakeIngBrowserHost host)
    {
        _http = new HttpMessageInvoker(bank, disposeHandler: false);
        _host = host;
    }

    public Uri Url { get; private set; } = new("about:blank");

    public string Html { get; private set; } = "";

    public bool Disposed { get; private set; }

    public async Task NavigateAsync(Uri url, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { await LoadAsync(url, ct); }
        finally { _gate.Release(); }
    }

    private async Task LoadAsync(Uri url, CancellationToken ct)
    {
        for (var hop = 0; hop < 10; hop++)
        {
            var response = await SendAsync(HttpMethod.Get, url, null, null, ct);
            if (response.Location is { } next)
            {
                url = next;
                continue;
            }
            Url = url;
            Html = Encoding.UTF8.GetString(response.Body);
            if (url.AbsolutePath == "/app/login/oauth")
            {
                // the page's script routes to the sign-in form
                Url = new Uri(url, "#/authenticate/ibbr");
                _phase = Phase.Form;
                _shownAt = DateTime.UtcNow;
            }
            else if (ScriptForward().Match(Html) is { Success: true } forward)
            {
                url = new Uri(url, forward.Groups["u"].Value);
                continue;
            }
            else _phase = Phase.None;
            return;
        }
        throw new InvalidOperationException("too many hops");
    }

    private sealed record Answer(int Status, Uri Url, string? Type, byte[] Body, Uri? Location);

    private async Task<Answer> SendAsync(HttpMethod method, Uri url, HttpContent? content, IReadOnlyDictionary<string, string>? headers, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(method, url) { Content = content };
        var cookie = string.Join("; ", _cookies.Where(c => string.Equals(c.Domain, url.Host, StringComparison.OrdinalIgnoreCase)).Select(c => c.Name + "=" + c.Value));
        if (cookie.Length > 0) message.Headers.TryAddWithoutValidation("Cookie", cookie);
        foreach (var (k, v) in headers ?? new Dictionary<string, string>(StringComparer.Ordinal))
        {
            if (!k.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) message.Headers.TryAddWithoutValidation(k, v);
        }
        using var response = await _http.SendAsync(message, ct);
        if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
        {
            foreach (var set in setCookies)
            {
                var pair = set.Split(';')[0].Split('=', 2);
                SetCookie(new CookieState(pair[0], pair[1], url.Host, "/", null, set.Contains("Secure", StringComparison.Ordinal), true));
            }
        }
        var location = (int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } l ? new Uri(url, l) : null;
        return new Answer((int)response.StatusCode, url, response.Content.Headers.ContentType?.MediaType, await response.Content.ReadAsByteArrayAsync(ct), location);
    }

    private async Task<JsonElement> ApiAsync(string path, object? body)
    {
        var content = body == null ? null : new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        var answer = await SendAsync(body == null ? HttpMethod.Get : HttpMethod.Post, new Uri(Url, "/app/login/api/" + path), content, null, CancellationToken.None);
        using var json = JsonDocument.Parse(answer.Body.Length == 0 ? "{}"u8.ToArray() : answer.Body);
        var root = json.RootElement.Clone();
        return answer.Status == 200 ? root : JsonElement.Parse("{\"failed\":true}");
    }

    // ---- the sign-in form ----

    public Task<bool> IsVisibleAsync(string selector) =>
        Task.FromResult(_phase == Phase.Form && selector is IngWebClient.UsernameSelector or IngWebClient.PasswordSelector or IngWebClient.SubmitSelector);


    public Task<bool> FillAsync(string selector, string value, TimeSpan timeout)
    {
        if (_phase != Phase.Form) return Task.FromResult(false);
        if (selector == IngWebClient.UsernameSelector) _alias = value;
        else if (selector == IngWebClient.PasswordSelector) _pin = value;
        else return Task.FromResult(false);
        return Task.FromResult(true);
    }

    public async Task<bool> ClickAsync(string selector)
    {
        if (_phase != Phase.Form || selector != IngWebClient.SubmitSelector) return false;
        var r = await ApiAsync("authenticate", new { alias = _alias, pin = _pin });
        if (r.TryGetProperty("failed", out _))
        {
            _loginError = true; // the form stays, with the error
            return true;
        }
        _shownAt = DateTime.UtcNow;
        if (r.GetProperty("next").GetString() == "device")
        {
            _devices = r.GetProperty("devices").EnumerateArray().Select(d => d.GetString()!).ToList();
            _phase = Phase.Device;
            Url = new Uri(Url, "#/authenticate/device");
        }
        else
        {
            _phase = Phase.Confirm;
            Url = new Uri(Url, "#/authenticate/confirm");
        }
        return true;
    }

    // ---- the connector's scripts ----

    public async Task<WebLoginCheck> CheckAsync(string function, string argument)
    {
        await _gate.WaitAsync();
        try
        {
            if (function == IngWebClient.SuccessScript)
                return new WebLoginCheck(Url.AbsolutePath.StartsWith("/app/obligo", StringComparison.Ordinal) && IngHtml.IsOverview(Html), "");
            if (function.Contains("__openbudgetIng", StringComparison.Ordinal)) return await WaitingAsync(argument);
            if (function == IngWebClient.PaceScript(_host.Pause))
            {
                var wait = _shownAt + _host.Pause - DateTime.UtcNow;
                if (wait > TimeSpan.Zero) await Task.Delay(wait);
                return new WebLoginCheck(true, "");
            }
            if (function.Contains("max-age=0", StringComparison.Ordinal))
            {
                var picked = _devicePicked;
                _devicePicked = null;
                return new WebLoginCheck(picked != null, picked ?? "");
            }
            return new WebLoginCheck(false, "unknown script");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<WebLoginCheck> WaitingAsync(string argument)
    {
        switch (_phase)
        {
            case Phase.Device:
                using (var arg = JsonDocument.Parse(argument))
                {
                    var remembered = arg.RootElement.TryGetProperty("device", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                    var index = _devices.Count == 1 ? 0 : _devices.FindIndex(x => string.Equals(x, remembered, StringComparison.Ordinal));
                    if (index < 0)
                    {
                        var reason = $"device choice: {_devices.Count} devices, none remembered";
                        _host.ShownToUser.Add(reason);
                        // the user looks at the page for a moment, then clicks
                        if (_host.UserPicksDevice is { } pick && DateTime.UtcNow - _shownAt > _host.Pause * 2 / 3) await ChooseAsync(pick);
                        return new WebLoginCheck(false, reason);
                    }
                    if (DateTime.UtcNow - _shownAt < _host.Pause) return new WebLoginCheck(true, "");
                    await ChooseAsync(index);
                    return new WebLoginCheck(true, $"device chosen ({_devices.Count} offered)");
                }
            case Phase.Confirm:
                var status = await ApiAsync("status", null);
                switch (status.GetProperty("state").GetString())
                {
                    case "done":
                        await LoadAsync(new Uri(Url, status.GetProperty("redirect").GetString()), CancellationToken.None);
                        break;
                    case "rejected":
                        _phase = Phase.Rejected;
                        break;
                    default:
                        break;
                }
                return new WebLoginCheck(true, "");
            case Phase.Rejected:
                return new WebLoginCheck(false, "end: ING did not confirm the sign-in (rejected in the app or timed out).");
            case Phase.None:
            case Phase.Form:
            default:
                return new WebLoginCheck(true, "");
        }
    }

    private async Task ChooseAsync(int index)
    {
        await ApiAsync("device", new { index });
        _devicePicked = _devices[index];
        _phase = Phase.Confirm;
        Url = new Uri(Url, "#/authenticate/confirm");
    }

    // ---- the signed-in page ----

    public Task<string> GetHtmlAsync() => Task.FromResult(Html);

    public async Task<BrowserFetchResponse> FetchAsync(BrowserFetchRequest request, CancellationToken ct)
    {
        if (Disposed) throw new BrowserActionException("The page is gone.");
        await _gate.WaitAsync(ct);
        try
        {
            var method = new HttpMethod(request.Method);
            var url = request.Url;
            HttpContent? content = request.Body == null ? null : new StringContent(request.Body, Encoding.UTF8, "application/x-www-form-urlencoded");
            for (var hop = 0; hop < 10; hop++)
            {
                var answer = await SendAsync(method, url, content, request.Headers, ct);
                if (answer.Location is { } next)
                {
                    url = next;
                    method = HttpMethod.Get;
                    content = null;
                    continue;
                }
                return new BrowserFetchResponse(answer.Status, url, answer.Type, answer.Body);
            }
            throw new InvalidOperationException("too many redirects");
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---- cookies and the rest ----

    private void SetCookie(CookieState cookie)
    {
        lock (_cookies)
        {
            _cookies.RemoveAll(c => c.Name == cookie.Name && c.Domain == cookie.Domain);
            _cookies.Add(cookie);
        }
    }

    public Task SetCookiesAsync(IReadOnlyList<CookieState> cookies)
    {
        foreach (var c in cookies) SetCookie(c with { Domain = c.Domain.TrimStart('.') });
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<CookieState>> GetCookiesAsync()
    {
        lock (_cookies) return Task.FromResult<IReadOnlyList<CookieState>>([.. _cookies]);
    }

    public Task GoBackAsync() => Task.CompletedTask;

    public Task ReloadAsync() => Task.CompletedTask;

    public Task<bool> HasVisibleTextAsync(string selector, IReadOnlyList<string> texts) =>
        Task.FromResult(_loginError && _phase == Phase.Form && selector == IngWebClient.CredentialsErrorSelector && texts.Contains("falsch", StringComparer.Ordinal));

    public Task StartScreencastAsync(Func<ScreencastFrame, Task> onFrame, int maxWidth, int maxHeight, int quality) => Task.CompletedTask;

    public Task ResizeScreencastAsync(int maxWidth, int maxHeight, int quality) => Task.CompletedTask;

    public Task StopScreencastAsync() => Task.CompletedTask;

    public Task<ScreencastFrame?> CaptureStillAsync(int quality, double scale) => Task.FromResult<ScreencastFrame?>(null);

    public Task DispatchMouseAsync(string type, double x, double y, string button, int clickCount, double deltaX, double deltaY, int buttons, int modifiers, double? timestamp = null) => Task.CompletedTask;

    public Task DispatchKeyAsync(string type, string key, string? code, string? text, int modifiers, string[]? commands) => Task.CompletedTask;

    public Task InsertTextAsync(string text) => Task.CompletedTask;

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        _http.Dispose();
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }

    [GeneratedRegex("location\\.replace\\(\"(?<u>[^\"]+)\"\\)", RegexOptions.None, 1000)]
    private static partial Regex ScriptForward();
}
