using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using OpenBudget.Browser;
using OpenBudget.Connectors;

namespace OpenBudget.E2E.Tests;

/// <summary>
/// The embedded browser against a real Chromium (CHROME_PATH, or the one Playwright installed) and a tiny local page
/// that "signs in" by setting a cookie. Skipped when no Chromium is present. No bank is contacted.
/// </summary>
#pragma warning disable CA1001 // the listener is closed in TearDown
[TestFixture]
public sealed class RealBrowserWebLoginTests
{
    private HttpListener _listener = null!;
    private Task _serving = Task.CompletedTask;
    private readonly List<string> _loginCookieHeaders = [];
    private string _baseUrl = "";
    private string _chrome = "";

    [SetUp]
    public void SetUp()
    {
        _chrome = ChromiumLocator.Find() ?? "";
        if (_chrome.Length == 0) Assert.Ignore("No Chromium found (set CHROME_PATH or run `playwright install chromium`).");

        var port = System.Security.Cryptography.RandomNumberGenerator.GetInt32(20000, 40000);
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        _baseUrl = $"http://127.0.0.1:{port}/";
        _serving = Task.Run(ServeAsync);
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_listener == null) return;
        _listener.Close();
        try { await _serving; } catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { /* closed */ }
    }

    private async Task ServeAsync()
    {
        while (_listener.IsListening)
        {
            var ctx = await _listener.GetContextAsync();
            var path = ctx.Request.Url!.AbsolutePath;
            string html;
            if (path == "/login")
            {
                lock (_loginCookieHeaders) _loginCookieHeaders.Add(ctx.Request.Headers["Cookie"] ?? "");
                // The "user" signs in after a moment: a persistent cookie, then the landing page.
                html = "<html><body><h1>Sign in</h1><script>setTimeout(function(){document.cookie='sid=abc; path=/; max-age=3600';location.href='/home';},400);</script></body></html>";
            }
            else
            {
                html = "<html><body><h1>Home</h1></body></html>";
            }
            var bytes = Encoding.UTF8.GetBytes(html);
            ctx.Response.ContentType = "text/html";
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
    }

    private BrowserService NewBrowser() => new(NullLogger<BrowserService>.Instance, new BrowserOptions { ExecutablePath = _chrome, IdleShutdown = TimeSpan.FromSeconds(1) });

    [Test]
    public async Task A_web_login_seeds_the_browser_exports_the_cookies_and_leaves_nothing_behind()
    {
        await using var browser = NewBrowser();
        var service = new WebLoginService(browser, new WebLoginOptions { SettleDelay = TimeSpan.FromMilliseconds(300) }, TimeProvider.System, NullLoggerFactory.Instance);
        var spec = new WebLoginSpec(new Uri(_baseUrl + "login"), ["127.0.0.1"], new Uri(_baseUrl + "home"));
        var seed = new CookieState("remember", "device-1", "127.0.0.1", "/", DateTime.UtcNow.AddDays(30), false, true, "Lax");

        await using var session = await service.StartAsync("user-1", spec, [seed], CancellationToken.None);
        Assert.That(service.Find(session.Id, "user-2"), Is.Null);
        var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.That(result.Success, Is.True, result.Error);
        Assert.That(result.Cookies.Select(c => c.Name), Is.EquivalentTo(new[] { "remember", "sid" }));
        var sid = result.Cookies.Single(c => c.Name == "sid");
        Assert.That(sid.Value, Is.EqualTo("abc"));
        Assert.That(sid.ExpiresUtc, Is.Not.Null.And.GreaterThan(DateTime.UtcNow));
        // The login page received the seeded cookie: stored cookies make it into the browser.
        lock (_loginCookieHeaders) Assert.That(_loginCookieHeaders, Has.Some.Contain("remember=device-1"));
        Assert.That(service.Find(session.Id, "user-1"), Is.Null, "the session is gone once the login completed");
    }

    [Test]
    public async Task A_second_context_does_not_see_the_cookies_of_the_first()
    {
        await using var browser = NewBrowser();
        var spec = new WebLoginSpec(new Uri(_baseUrl + "login"), ["127.0.0.1"], new Uri(_baseUrl + "home"));
        var service = new WebLoginService(browser, new WebLoginOptions { SettleDelay = TimeSpan.FromMilliseconds(100) }, TimeProvider.System, NullLoggerFactory.Instance);

        await using var first = await service.StartAsync("user-1", spec, [], CancellationToken.None);
        Assert.That((await first.Completion.WaitAsync(TimeSpan.FromSeconds(60))).Success, Is.True);
        var before = _loginCookieHeaders.Count;

        await using var second = await service.StartAsync("user-2", spec, [], CancellationToken.None);
        Assert.That((await second.Completion.WaitAsync(TimeSpan.FromSeconds(60))).Success, Is.True);
        lock (_loginCookieHeaders) Assert.That(_loginCookieHeaders.Skip(before), Has.None.Contain("sid=abc"), "bank sessions of different logins must never mix");
    }

    [Test]
    public async Task The_page_is_streamed_as_jpeg_frames_and_input_is_accepted()
    {
        await using var browser = NewBrowser();
        await using var session = await browser.OpenSessionAsync(CancellationToken.None);
        var frame = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        await session.StartScreencastAsync(f =>
        {
            frame.TrySetResult(f.Data);
            return Task.CompletedTask;
        }, 640, 400, 60);
        await session.NavigateAsync(new Uri(_baseUrl + "login"), CancellationToken.None);

        var jpeg = await frame.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.That(jpeg[0], Is.EqualTo(0xFF));
        Assert.That(jpeg[1], Is.EqualTo(0xD8));
        Assert.DoesNotThrowAsync(() => session.DispatchMouseAsync("mouseMoved", 10, 10, "left", 0, 0, 0, 0, 0));
        Assert.DoesNotThrowAsync(() => session.DispatchKeyAsync("keyDown", "a", "KeyA", "a", 0, null));
        Assert.DoesNotThrowAsync(() => session.InsertTextAsync("hello"));
        Assert.That(await session.CaptureStillAsync(80, 1), Is.Not.Null);
    }
}
