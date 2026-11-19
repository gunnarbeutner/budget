using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using OpenBudget.Browser;
using Xunit;

namespace OpenBudget.Tests.Connectors;

/// <summary>
/// The browser sidecar: configuration, the launcher client, the DevTools address rewrite, the stream proxy's rules and the
/// shared screen that shows one web login at a time to its owner only.
/// </summary>
public sealed class RemoteBrowserTests
{
    private const string Token = "0123456789abcdef0123456789abcdef";

    private static RemoteBrowserOptions Options() => new()
    {
        LauncherUrl = new Uri("http://127.0.0.1:9300"),
        StreamUrl = new Uri("http://127.0.0.1:8080"),
        Token = Token,
    };

    [Fact]
    public void Remote_mode_needs_both_addresses_and_a_long_token()
    {
        Assert.True(Options().IsEnabled);
        Assert.False(new RemoteBrowserOptions().IsEnabled);
        var shortToken = Options();
        shortToken.Token = "short";
        Assert.False(shortToken.IsEnabled);
        var noStream = Options();
        noStream.StreamUrl = null;
        Assert.False(noStream.IsEnabled);
    }

    [Fact]
    public void The_DevTools_address_keeps_the_browser_id_and_takes_the_sidecar_host_and_port()
    {
        var rewritten = BrowserLauncherClient.RewriteEndpoint(
            new Uri("ws://127.0.0.1:19400/devtools/browser/5f1e-42"), "172.31.90.5", 9400);
        Assert.Equal("ws://172.31.90.5:9400/devtools/browser/5f1e-42", rewritten.ToString());
        Assert.Throws<BrowserActionException>(() => BrowserLauncherClient.RewriteEndpoint(new Uri("http://127.0.0.1:1/x"), "h", 2));
    }

    [Fact]
    public async Task A_host_name_is_resolved_to_an_address_and_an_address_stays()
    {
        Assert.Equal("10.1.2.3", await BrowserLauncherClient.ResolveAsync("10.1.2.3", CancellationToken.None));
        Assert.Equal("localhost", await BrowserLauncherClient.ResolveAsync("localhost", CancellationToken.None));
    }

    [Theory]
    [InlineData("GET", "/weblogin/desktop/", true)]
    [InlineData("GET", "/weblogin/desktop/assets/index-abc.js", true)]
    [InlineData("GET", "/weblogin/desktop/api/websockets", true)]
    [InlineData("GET", "/weblogin/desktop/api/health", true)]
    [InlineData("POST", "/weblogin/desktop/api/websockets", false)]
    [InlineData("GET", "/weblogin/desktop/api/tokens", false)]
    [InlineData("GET", "/weblogin/desktop/API/tokens", false)]
    [InlineData("GET", "/weblogin/desktop/api/switch", false)]
    [InlineData("GET", "/weblogin/desktop/api/files/Downloads/x", false)]
    [InlineData("GET", "/weblogin/desktop/api/sessions", false)]
    [InlineData("GET", "/weblogin/desktop/api/websocketsX", false)]
    [InlineData("GET", "/weblogin/desktop/api", false)]
    [InlineData("GET", "/weblogin/desktop/../desktop/api/tokens", false)]
    [InlineData("GET", "/weblogin/desktop/%2e%2e/api/tokens", false)]
    [InlineData("GET", "/weblogin/desktop//api/tokens", false)]
    [InlineData("GET", "/other/path", false)]
    [InlineData("DELETE", "/weblogin/desktop/", false)]
    public void The_stream_proxy_passes_only_the_stream_client(string method, string path, bool allowed) =>
        Assert.Equal(allowed, DesktopProxy.IsAllowed(method, path));

    [Fact]
    public void Only_the_stream_cookie_goes_to_selkies()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = ".AspNetCore.Identity.Application=secret; selkies_token=abc123";
        Assert.Equal("selkies_token=abc123", DesktopProxy.UpstreamCookie(context.Request.Cookies));
        var without = new DefaultHttpContext();
        without.Request.Headers.Cookie = ".AspNetCore.Identity.Application=secret";
        Assert.Null(DesktopProxy.UpstreamCookie(without.Request.Cookies));
    }

    [Fact]
    public void The_token_table_holds_one_viewer_with_keyboard_and_mouse_or_nobody()
    {
        Assert.Equal("""{"t1":{"role":"viewer","slot":null,"mk_control":true}}""", JsonSerializer.Serialize(SidecarScreenBackend.TokenTable("t1")));
        Assert.Equal("{}", JsonSerializer.Serialize(SidecarScreenBackend.TokenTable(null)));
    }

    /// <summary>Records what the screen asks the sidecar for.</summary>
    private sealed class FakeBackend : IRemoteScreenBackend
    {
        public List<string> Calls { get; } = [];

        public string? Shown { get; private set; }

        public string? ValidToken { get; private set; }

        public bool Fail { get; set; }

        public Task ShowAsync(string? screenId, CancellationToken ct)
        {
            if (Fail) throw new BrowserActionException("down");
            lock (Calls) Calls.Add("show " + (screenId ?? "none"));
            Shown = screenId;
            return Task.CompletedTask;
        }

        public Task SetViewerTokenAsync(string? token, CancellationToken ct)
        {
            lock (Calls) Calls.Add(token == null ? "revoke" : "token");
            ValidToken = token;
            return Task.CompletedTask;
        }
    }

    private static RemoteScreen Screen(FakeBackend backend) => new(backend, NullLogger<RemoteScreen>.Instance);

    [Fact]
    public async Task A_web_login_is_shown_to_its_owner_with_a_fresh_token_that_is_revoked_when_released()
    {
        var backend = new FakeBackend();
        await using var screen = Screen(backend);
        var ended = new TaskCompletionSource();
        var view = await screen.ShowAsync("s1", "gunnar", "chrome-1", ended.Task, CancellationToken.None);

        Assert.Equal("chrome-1", backend.Shown);
        Assert.Equal(view.Token, backend.ValidToken);
        Assert.Equal(64, view.Token.Length);
        Assert.True(screen.Authorize("gunnar", id => id == "s1", out var viewEnded));
        Assert.False(viewEnded.IsCancellationRequested);
        // someone else, or the owner once the login is gone, may not use the stream
        Assert.False(screen.Authorize("lisa", _ => true, out _));
        Assert.False(screen.Authorize("gunnar", _ => false, out _));

        await view.DisposeAsync();
        Assert.True(viewEnded.IsCancellationRequested);
        Assert.Null(backend.ValidToken);
        Assert.Null(backend.Shown);
        Assert.False(screen.Authorize("gunnar", _ => true, out _));
    }

    [Fact]
    public async Task Web_logins_take_turns_on_the_one_screen()
    {
        var backend = new FakeBackend();
        await using var screen = Screen(backend);
        var first = await screen.ShowAsync("s1", "gunnar", "chrome-1", new TaskCompletionSource().Task, CancellationToken.None);
        var secondEnded = new TaskCompletionSource();
        var waiting = screen.ShowAsync("s2", "lisa", "chrome-2", secondEnded.Task, CancellationToken.None);

        Assert.False(waiting.IsCompleted);
        Assert.Equal(1, screen.Position("s2"));
        Assert.Equal("chrome-1", backend.Shown);
        Assert.False(screen.Authorize("lisa", _ => true, out _));

        await first.DisposeAsync();
        var second = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("chrome-2", backend.Shown);
        Assert.Equal(second.Token, backend.ValidToken);
        Assert.NotEqual(first.Token, second.Token);
        Assert.True(screen.Authorize("lisa", id => id == "s2", out _));
        Assert.False(screen.Authorize("gunnar", _ => true, out _));

        // the web login ending ends its view, even while the dialog still holds it
        secondEnded.SetResult();
        await WaitUntilAsync(() => backend.ValidToken == null);
        Assert.True(second.Ended.IsCancellationRequested);
        Assert.Equal(-1, screen.Position("s2"));
    }

    [Fact]
    public async Task Two_views_of_one_web_login_share_its_turn_and_another_user_cannot_join_it()
    {
        var backend = new FakeBackend();
        await using var screen = Screen(backend);
        var ended = new TaskCompletionSource().Task;
        var a = await screen.ShowAsync("s1", "gunnar", "chrome-1", ended, CancellationToken.None);
        var b = await screen.ShowAsync("s1", "gunnar", "chrome-1", ended, CancellationToken.None);
        Assert.Equal(a.Token, b.Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => screen.ShowAsync("s1", "lisa", "chrome-1", ended, CancellationToken.None));

        await a.DisposeAsync();
        Assert.Equal(a.Token, backend.ValidToken);
        await b.DisposeAsync();
        Assert.Null(backend.ValidToken);
    }

    [Fact]
    public async Task A_waiting_view_that_gives_up_leaves_the_queue()
    {
        var backend = new FakeBackend();
        await using var screen = Screen(backend);
        var first = await screen.ShowAsync("s1", "gunnar", "chrome-1", new TaskCompletionSource().Task, CancellationToken.None);
        using var cancel = new CancellationTokenSource();
        var waiting = screen.ShowAsync("s2", "lisa", "chrome-2", new TaskCompletionSource().Task, cancel.Token);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(-1, screen.Position("s2"));

        await first.DisposeAsync();
        Assert.Null(backend.Shown);
        Assert.Null(backend.ValidToken);
    }

    [Fact]
    public async Task A_sidecar_that_cannot_show_the_browser_fails_the_view()
    {
        var backend = new FakeBackend { Fail = true };
        await using var screen = Screen(backend);
        await Assert.ThrowsAsync<BrowserActionException>(() =>
            screen.ShowAsync("s1", "gunnar", "chrome-1", new TaskCompletionSource().Task, CancellationToken.None));
        Assert.Null(backend.ValidToken);
        Assert.False(screen.Authorize("gunnar", _ => true, out _));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(20);
        Assert.True(condition());
    }

    /// <summary>Answers like the sidecar's launcher and records the requests.</summary>
    private sealed class FakeLauncher : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string? Auth, byte[] Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content == null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            Requests.Add((request.Method, request.RequestUri!.PathAndQuery, request.Headers.Authorization?.ToString(), body));
            var path = request.RequestUri.AbsolutePath;
            if (request.Method == HttpMethod.Post && path == "/browsers")
                return Json("""{"id":"0123456789abcdef0123456789abcdef","port":9401}""");
            if (request.Method == HttpMethod.Get && path == "/json/version")
                return Json("""{"Browser":"Chrome/1","webSocketDebuggerUrl":"ws://127.0.0.1:19401/devtools/browser/abc"}""");
            if (request.Method == HttpMethod.Delete && path.EndsWith("/gone", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (request.Method == HttpMethod.Delete && request.RequestUri.Query == "?profile=1")
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0x50, 0x4b]) };
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        private static HttpResponseMessage Json(string json) =>
            new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    [Fact]
    public async Task The_launcher_client_starts_connects_shows_and_stops_a_browser_with_the_token()
    {
        using var handler = new FakeLauncher();
        using var http = new HttpClient(handler);
        var client = new BrowserLauncherClient(http, Options());

        var chrome = await client.StartAsync([1, 2, 3], CancellationToken.None);
        Assert.Equal(new RemoteChrome("0123456789abcdef0123456789abcdef", 9401), chrome);
        Assert.Equal(new byte[] { 1, 2, 3 }, handler.Requests[0].Body);

        var endpoint = await client.DevToolsEndpointAsync(chrome, CancellationToken.None);
        Assert.Equal("ws://127.0.0.1:9401/devtools/browser/abc", endpoint.ToString());
        Assert.Equal("/json/version", handler.Requests[1].Path);

        await client.ShowAsync(chrome.Id, CancellationToken.None);
        Assert.Equal("""{"id":"0123456789abcdef0123456789abcdef"}""", Encoding.UTF8.GetString(handler.Requests[2].Body));
        Assert.Equal(new byte[] { 0x50, 0x4b }, await client.StopAsync(chrome.Id, returnProfile: true, CancellationToken.None));
        Assert.Null(await client.StopAsync(chrome.Id, returnProfile: false, CancellationToken.None));
        Assert.Null(await client.StopAsync("gone", returnProfile: true, CancellationToken.None));
        await client.ResetAsync(CancellationToken.None);

        // every launcher request carries the token; the DevTools endpoint is Chrome's own and needs none
        Assert.All(handler.Requests.Where(r => r.Path != "/json/version"), r => Assert.Equal("Bearer " + Token, r.Auth));
        Assert.Equal(["/browsers", "/json/version", "/front", "/browsers/" + chrome.Id + "?profile=1", "/browsers/" + chrome.Id, "/browsers/gone?profile=1", "/reset"],
            handler.Requests.Select(r => r.Path));
    }

    [Fact]
    public void The_launcher_client_needs_remote_mode()
    {
        using var http = new HttpClient();
        Assert.Throws<ArgumentException>(() => new BrowserLauncherClient(http, new RemoteBrowserOptions()));
    }
}
