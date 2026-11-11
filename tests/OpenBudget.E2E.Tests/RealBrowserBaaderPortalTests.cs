using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using OpenBudget.Browser;
using OpenBudget.Connectors;
using OpenBudget.Connectors.FinTs;

namespace OpenBudget.E2E.Tests;

/// <summary>
/// The Baader portal connector against a real Chromium and a local made-up portal whose session, like the real one, only
/// works from the browser that signed in (here: requests without the browser's Sec-Fetch-Mode header are sent away).
/// Documents live on a storage host of another origin, one of them readable by the page (CORS) and one not.
/// Skipped when no Chromium is present. No bank is contacted.
/// </summary>
#pragma warning disable CA1001 // the listeners are closed in TearDown
[TestFixture]
public sealed class RealBrowserBaaderPortalTests
{
    private const string Customer = "4711";
    private const string PageToken = "tok-1";
    private static readonly ConnectorCredentials Credentials = new("alice", "pw");
    private static readonly RemoteAccount Cash = new("cash-1", "Verrechnungskonto", RemoteAccountType.Giro, null, "EUR");

    private HttpListener _portal = null!;
    private HttpListener _storage = null!;
    private readonly List<Task> _serving = [];
    private readonly ConcurrentDictionary<string, bool> _sessions = new(StringComparer.Ordinal);
    private int _signIns;
    private int _serverSideRefusals;
    private string _portalUrl = "";
    private string _storageUrl = "";
    private string _chrome = "";

    [SetUp]
    public void SetUp()
    {
        _chrome = ChromiumLocator.Find() ?? "";
        if (_chrome.Length == 0) Assert.Ignore("No Chromium found (set CHROME_PATH or run `playwright install chromium`).");
        var port = System.Security.Cryptography.RandomNumberGenerator.GetInt32(20000, 40000);
        _portalUrl = $"http://127.0.0.1:{port}/";
        _storageUrl = $"http://localhost:{port + 1}/"; // another origin
        _portal = Listen(_portalUrl);
        _storage = Listen(_storageUrl);
        _serving.Add(Task.Run(() => ServeAsync(_portal, HandlePortalAsync)));
        _serving.Add(Task.Run(() => ServeAsync(_storage, HandleStorageAsync)));
    }

    private static HttpListener Listen(string prefix)
    {
        var l = new HttpListener();
        l.Prefixes.Add(prefix);
        l.Start();
        return l;
    }

    [TearDown]
    public async Task TearDown()
    {
        _portal?.Close();
        _storage?.Close();
        foreach (var t in _serving)
        {
            try { await t; } catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { /* closed */ }
        }
    }

    private static async Task ServeAsync(HttpListener listener, Func<HttpListenerContext, Task> handle)
    {
        while (listener.IsListening)
        {
            var ctx = await listener.GetContextAsync();
            try { await handle(ctx); }
            finally { ctx.Response.Close(); }
        }
    }

    private bool SignedIn(HttpListenerRequest request) =>
        request.Cookies["sid"] is { } sid && _sessions.ContainsKey(sid.Value) && request.Headers["Sec-Fetch-Mode"] != null;

    private async Task HandlePortalAsync(HttpListenerContext ctx)
    {
        var request = ctx.Request;
        var path = request.Url!.AbsolutePath;
        if (path == "/login" && request.HttpMethod == "POST")
        {
            var form = await ReadFormAsync(request);
            if (form.GetValueOrDefault("user") == "alice" && form.GetValueOrDefault("password") == "pw")
            {
                var sid = Guid.NewGuid().ToString("N");
                _sessions[sid] = true;
                Interlocked.Increment(ref _signIns);
                ctx.Response.AppendHeader("Set-Cookie", $"sid={sid}; Path=/; HttpOnly; SameSite=Lax");
            }
            Redirect(ctx, "/dashboard");
            return;
        }
        if (path == "/login")
        {
            await HtmlAsync(ctx, "<html><body><div id=n><p>Ihr Browser wird nicht unterstützt</p><button onclick=\"document.getElementById('n').remove();document.getElementById('loginform').style.display='block'\">Ich bin mir der Risiken bewusst</button></div>"
                + "<form id=loginform method=post action=/login style=display:none><input id=user name=user><input id=password name=password type=password><button id=ctaLogin type=submit>Login</button></form></body></html>");
            return;
        }
        if (path == "/")
        {
            await HtmlAsync(ctx, "<html><body>Baader</body></html>");
            return;
        }
        if (!SignedIn(request))
        {
            if (request.Cookies["sid"] != null) Interlocked.Increment(ref _serverSideRefusals);
            Redirect(ctx, "/"); // like the portal: a session it does not accept ends on its start page
            return;
        }
        if (path == "/dashboard")
        {
            await HtmlAsync(ctx, $"<html><head><meta content=\"{PageToken}\" name=\"infotok\" /></head><body>Dashboard</body></html>");
            return;
        }
        if (path == "/fstatus")
        {
            await WriteAsync(ctx, "application/json", $"[{{\"customer_id\":{Customer},\"first_name\":\"Alice\",\"last_name\":\"Example\",\"amount\":\"0,00\",\"currency\":\"EUR\"}}]");
            return;
        }
        if (path == "/mailrows/" + Customer && request.HttpMethod == "POST")
        {
            var form = await ReadFormAsync(request);
            if (form.GetValueOrDefault("token") != PageToken || request.Headers["X-Requested-With"] != "XMLHttpRequest")
            {
                ctx.Response.StatusCode = 403;
                return;
            }
            var to = DateOnly.ParseExact(form["to"], "dd.MM.yyyy", CultureInfo.InvariantCulture);
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            if (form["section"] == "ACCOUNT_STATEMENT" && to >= today)
            {
                var day = today.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                await WriteAsync(ctx, "application/json", $"[{{\"{day}\":[[1,\"Kontoauszug\",\"cors1\"],[0,\"Zinsstaffel\",\"plain2\"]]}}]");
            }
            else
            {
                await WriteAsync(ctx, "application/json", "{\"do\":\"error\",\"title\":\"Aktuell sind in Ihrem Postfach keine Dokumente hinterlegt\"}");
            }
            return;
        }
        if (path.StartsWith("/maildownload/", StringComparison.Ordinal))
        {
            Redirect(ctx, _storageUrl + "bucket/" + path["/maildownload/".Length..] + ".pdf?sig=1");
            return;
        }
        ctx.Response.StatusCode = 404;
    }

    private async Task HandleStorageAsync(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url!.AbsolutePath;
        // only one document's storage lets the portal's page read it; the other needs the resolved link
        if (path.Contains("cors", StringComparison.Ordinal)) ctx.Response.AppendHeader("Access-Control-Allow-Origin", "*");
        await WriteAsync(ctx, "application/pdf", "%PDF-1.4\n% " + path + "\n%%EOF\n");
    }

    private static async Task<Dictionary<string, string>> ReadFormAsync(HttpListenerRequest request)
    {
        using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();
        return body.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2))
            .ToDictionary(p => WebUtility.UrlDecode(p[0]), p => p.Length > 1 ? WebUtility.UrlDecode(p[1]) : "", StringComparer.Ordinal);
    }

    private static void Redirect(HttpListenerContext ctx, string location)
    {
        ctx.Response.StatusCode = 302;
        ctx.Response.RedirectLocation = location;
    }

    private static Task HtmlAsync(HttpListenerContext ctx, string html) => WriteAsync(ctx, "text/html; charset=utf-8", html);

    private static async Task WriteAsync(HttpListenerContext ctx, string type, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        ctx.Response.ContentType = type;
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
    }

    private BrowserService NewBrowser() => new(NullLogger<BrowserService>.Instance, new BrowserOptions { ExecutablePath = _chrome, IdleShutdown = TimeSpan.FromSeconds(1) });

    private static WebLoginService Service(BrowserService browser) => new(browser,
        new WebLoginOptions { SettleDelay = TimeSpan.FromMilliseconds(100), FormTimeout = TimeSpan.FromSeconds(10) },
        TimeProvider.System, NullLoggerFactory.Instance);

    [Test]
    public async Task Documents_are_listed_and_downloaded_through_the_kept_browser_which_is_closed_afterwards()
    {
        await using var browser = NewBrowser();
        var service = Service(browser);
        using var http = new BankHttpClient();
        var portal = new BaaderPortal(http, TimeProvider.System, NullLogger.Instance, new Uri(_portalUrl));
        var none = new HashSet<string>(StringComparer.Ordinal);

        var ask = await portal.FetchAsync([Cash], none, Credentials, null, CancellationToken.None);
        Assert.That(ask.Status, Is.EqualTo(StepStatus.Challenge));
        var spec = ask.Challenge!.WebLoginSpec!;

        await using var session = await service.StartAsync("user-1", spec, http.ExportCookies(), CancellationToken.None);
        var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.That(result.Success, Is.True, result.Error);
        Assert.That(service.Find(session.Id, "user-1"), Is.Null, "nobody can watch the kept browser");
        var kept = session.TakeBrowser();
        Assert.That(kept, Is.Not.Null);
        http.ReplaceCookies(spec, result.Cookies);

        // The exported cookie alone does not work outside the browser.
        var overHttp = new BaaderPortal(http, TimeProvider.System, NullLogger.Instance, new Uri(_portalUrl));
        Assert.That((await overHttp.FetchAsync([Cash], none, Credentials, null, CancellationToken.None)).Status, Is.EqualTo(StepStatus.Challenge));
        Assert.That(_serverSideRefusals, Is.GreaterThan(0));

        var done = await portal.FetchAsync([Cash], none, Credentials, new ChallengeAnswer(ask.Challenge, null) { Browser = kept }, CancellationToken.None);
        Assert.That(done.Status, Is.EqualTo(StepStatus.Done), done.FailureReason);
        Assert.That(done.Value!.Select(d => d.Identifier), Is.EquivalentTo(new[] { "baader:cors1", "baader:plain2" }));
        Assert.That(done.Value!.Select(d => Encoding.ASCII.GetString(d.Content, 0, 5)), Is.All.EqualTo("%PDF-"));
        Assert.That(Encoding.ASCII.GetString(done.Value!.Single(d => d.Identifier == "baader:plain2").Content), Does.Contain("/bucket/plain2.pdf"));

        Assert.That(browser.OpenSessions, Is.EqualTo(1));
        await kept!.DisposeAsync();
        Assert.That(kept.IsOpen, Is.False);
        Assert.That(browser.OpenSessions, Is.Zero, "the kept browser is gone");
        // without the browser the portal asks for a sign-in again
        Assert.That((await portal.FetchAsync([Cash], none, Credentials, null, CancellationToken.None)).Status, Is.EqualTo(StepStatus.Challenge));
    }

    [Test]
    public async Task A_stored_session_that_still_works_in_the_browser_resumes_without_a_sign_in()
    {
        await using var browser = NewBrowser();
        var service = Service(browser);
        using var http = new BankHttpClient();
        var spec = new BaaderPortal(http, TimeProvider.System, NullLogger.Instance, new Uri(_portalUrl)).WebLogin(Credentials);

        IReadOnlyList<CookieState> cookies;
        await using (var first = await service.StartAsync("user-1", spec, [], CancellationToken.None))
        {
            var result = await first.Completion.WaitAsync(TimeSpan.FromSeconds(60));
            Assert.That(result.Success, Is.True, result.Error);
            cookies = result.Cookies;
        }
        Assert.That(_signIns, Is.EqualTo(1));
        Assert.That(browser.OpenSessions, Is.Zero, "an untaken kept browser is destroyed with its login");

        await using var second = await service.StartAsync("user-1", spec, cookies, CancellationToken.None);
        var resumed = await second.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.That(resumed.Success, Is.True, resumed.Error);
        Assert.That(_signIns, Is.EqualTo(1), "no second sign-in");
        await using var kept = second.TakeBrowser();
        var status = await kept!.GetAsync(new Uri(_portalUrl + "fstatus"), "application/json", CancellationToken.None);
        Assert.That(status.Text, Does.Contain(Customer));
    }
}
