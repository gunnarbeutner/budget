using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using OpenBudget.Browser;
using OpenBudget.Connectors;
using OpenBudget.Connectors.PayPal;

namespace OpenBudget.E2E.Tests;

/// <summary>
/// The PayPal connector against a real Chromium and a local made-up PayPal: a combined or two-step sign-in form
/// under a cookie banner, an account summary, and the activity download (report request with the
/// page's CSRF token, report list, CSV). Like the real site, the session only works from the browser that signed in
/// (requests without the browser's Sec-Fetch-Mode header are refused). Skipped when no Chromium is present.
/// </summary>
#pragma warning disable CA1001 // the listener is closed in TearDown
[TestFixture]
public sealed class RealBrowserPayPalTests
{
    private const string Email = "me@example.org";
    private const string Password = "pw-1";
    private const string Csrf = "csrf-token-1";
    private const string Csv =
        "\"Date\",\"Time\",\"Name\",\"Type\",\"Status\",\"Currency\",\"Gross\",\"Fee\",\"Net\",\"Transaction ID\",\"Item Title\",\"Balance\",\"Balance Impact\"\r\n"
        + "\"{0}\",\"09:00:00\",\"Example Shop\",\"Express Checkout Payment\",\"Completed\",\"EUR\",\"-12.50\",\"0.00\",\"-12.50\",\"TXA\",\"Garden chair\",\"87.50\",\"Debit\"\r\n"
        + "\"{0}\",\"10:00:00\",\"Bob Example\",\"Mobile Payment\",\"Completed\",\"EUR\",\"20.00\",\"-0.70\",\"19.30\",\"TXB\",\"\",\"106.80\",\"Credit\"\r\n";

    private static readonly ConnectorCredentials Credentials = new(Email, Password);
    private readonly ConcurrentDictionary<string, bool> _sessions = new(StringComparer.Ordinal);
    private HttpListener _site = null!;
    private Task _serving = Task.CompletedTask;
    private string _url = "";
    private string _chrome = "";
    private int _signIns;
    private int _bannerDeclined;
    private int _reports;
    private bool _combinedLogin;

    [SetUp]
    public void SetUp()
    {
        _combinedLogin = false;
        _signIns = 0;
        _bannerDeclined = 0;
        _reports = 0;
        _sessions.Clear();
        _chrome = ChromiumLocator.Find() ?? "";
        if (_chrome.Length == 0) Assert.Ignore("No Chromium found (set CHROME_PATH or run `playwright install chromium`).");
        var port = System.Security.Cryptography.RandomNumberGenerator.GetInt32(20000, 40000);
        _url = $"http://127.0.0.1:{port}/";
        _site = new HttpListener();
        _site.Prefixes.Add(_url);
        _site.Start();
        _serving = Task.Run(ServeAsync);
    }

    [TearDown]
    public async Task TearDown()
    {
        _site?.Close();
        try { await _serving; } catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { /* closed */ }
    }

    private async Task ServeAsync()
    {
        while (_site.IsListening)
        {
            var ctx = await _site.GetContextAsync();
            try { await HandleAsync(ctx); }
            finally { ctx.Response.Close(); }
        }
    }

    private bool SignedIn(HttpListenerRequest r) =>
        r.Cookies["sid"] is { } sid && _sessions.ContainsKey(sid.Value) && r.Headers["Sec-Fetch-Mode"] != null;

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        var request = ctx.Request;
        var path = request.Url!.AbsolutePath;
        if (path == "/signin" && request.HttpMethod == "POST")
        {
            var form = await ReadFormAsync(request);
            if (form.GetValueOrDefault("login_email") == Email && form.GetValueOrDefault("login_password") == Password)
            {
                var sid = Guid.NewGuid().ToString("N");
                _sessions[sid] = true;
                Interlocked.Increment(ref _signIns);
                ctx.Response.AppendHeader("Set-Cookie", $"sid={sid}; Path=/; HttpOnly; SameSite=Lax");
                Redirect(ctx, "/myaccount/summary");
                return;
            }
            await HtmlAsync(ctx, SignInPage(request, "<div class=\"notification-critical\" role=\"alert\">Some of your info isn't correct. Please try again.</div>"));
            return;
        }
        if (path == "/signin")
        {
            await HtmlAsync(ctx, SignInPage(request, ""));
            return;
        }
        if (path == "/cookie-decline")
        {
            Interlocked.Increment(ref _bannerDeclined);
            ctx.Response.AppendHeader("Set-Cookie", "cookie_prefs=necessary; Path=/; SameSite=Lax");
            return;
        }
        if (!SignedIn(request))
        {
            Redirect(ctx, "/signin");
            return;
        }
        switch (path)
        {
            case "/myaccount/summary":
                await HtmlAsync(ctx, "<html><body><h1>PayPal balance</h1><p>106,80 €</p></body></html>");
                return;
            case "/reports/dlog":
                await HtmlAsync(ctx, $"<html><head><meta name=\"csrf-token\" content=\"{Csrf}\"></head><body>Download activity</body></html>");
                return;
            case "/reports/dlog/generate" when request.HttpMethod == "POST":
                if (request.Headers["X-CSRF-Token"] != Csrf)
                {
                    ctx.Response.StatusCode = 403;
                    return;
                }
                Interlocked.Increment(ref _reports);
                await WriteAsync(ctx, "application/json", "{\"reportId\":\"R1\"}");
                return;
            case "/reports/dlog/list":
                await WriteAsync(ctx, "application/json", "{\"reports\":[{\"id\":\"R1\",\"status\":\"COMPLETED\"}]}");
                return;
            case "/reports/dlog/download/R1":
                var day = DateTime.UtcNow.AddDays(-1).ToString("MM/dd/yyyy", System.Globalization.CultureInfo.InvariantCulture);
                await WriteAsync(ctx, "text/csv", Csv.Replace("{0}", day, StringComparison.Ordinal));
                return;
            default:
                ctx.Response.StatusCode = 404;
                return;
        }
    }

    /// <summary>E-mail and password on one page or two. The cookie banner sits on top until a choice was stored.</summary>
    private string SignInPage(HttpListenerRequest request, string notice) => "<html><body>"
        + (request.Cookies["cookie_prefs"] != null ? "" : "<div id=gdprCookieBanner style=\"position:fixed;inset:0;background:#fff;z-index:9\"><p>Cookies</p>"
        + "<button id=acceptAllButton onclick=\"this.parentNode.remove()\">Accept</button>"
        + "<button id=bannerDeclineButton onclick=\"fetch('/cookie-decline');this.parentNode.remove()\">Decline</button></div>")
        + notice
        + "<form name=login method=post action=/signin>"
        + (_combinedLogin
            ? "<input id=email name=login_email type=email><input id=password name=login_password type=password><button id=btnLogin type=submit>Log In</button>"
            : "<div id=splitEmail><input id=email name=login_email type=email><button id=btnNext type=button onclick=\"document.getElementById('splitEmail').style.display='none';document.getElementById('splitPassword').style.display='block'\">Next</button></div>"
                + "<div id=splitPassword style=display:none><input id=password name=login_password type=password><button id=btnLogin type=submit>Log In</button></div>")
        + "</form></body></html>";

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

    private PayPalConnector Connector(BankHttpClient http) =>
        new(new ConnectorContext(http, TimeProvider.System, NullLogger.Instance), new PayPalOptions { BaseUri = new Uri(_url), PollDelay = TimeSpan.Zero });

    [TestCase(false)]
    [TestCase(true)]
    public async Task Signs_in_declines_cookies_and_reads_the_activity_download_through_the_kept_browser(bool combinedLogin)
    {
        _combinedLogin = combinedLogin;
        await using var browser = NewBrowser();
        var service = Service(browser);
        using var http = new BankHttpClient();
        var connector = Connector(http);

        var ask = await connector.LoginAsync(Credentials, null, null, CancellationToken.None);
        Assert.That(ask.Status, Is.EqualTo(StepStatus.Challenge));
        var spec = ask.Challenge!.WebLoginSpec!;
        await using var session = await service.StartAsync("user-1", spec, http.ExportCookies(), CancellationToken.None);
        var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.That(result.Success, Is.True, result.Error);
        Assert.That(_signIns, Is.EqualTo(1));
        Assert.That(_bannerDeclined, Is.Positive, "the cookie banner is declined, not accepted");
        await using var kept = session.TakeBrowser();
        Assert.That(kept, Is.Not.Null);
        http.ReplaceCookies(spec, result.Cookies);

        var done = await connector.LoginAsync(Credentials, null, new ChallengeAnswer(ask.Challenge, null) { Browser = kept }, CancellationToken.None);
        Assert.That(done.Status, Is.EqualTo(StepStatus.Done), done.FailureReason);
        var accounts = await connector.ListAccountsAsync(CancellationToken.None);
        Assert.That(accounts.Select(a => a.RemoteId), Is.EqualTo(new[] { "paypal-EUR" }));
        var refresh = await connector.RefreshAsync(accounts[0], DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-30), null, CancellationToken.None);
        Assert.That(refresh.Status, Is.EqualTo(StepStatus.Done), refresh.FailureReason);
        Assert.That(refresh.Value!.Balance, Is.EqualTo(106.80m));
        Assert.That(refresh.Value.Transactions.Select(t => (t.BankTransactionId, t.Amount)),
            Is.EqualTo(new[] { ("TXA", -12.50m), ("TXB", 20.00m), ("TXB:fee", -0.70m) }));
        Assert.That(_reports, Is.EqualTo(1));
    }

    [Test]
    public async Task A_wrong_password_ends_the_sign_in_as_rejected()
    {
        await using var browser = NewBrowser();
        var service = Service(browser);
        using var http = new BankHttpClient();
        var spec = Connector(http).WebLogin(new ConnectorCredentials(Email, "wrong"));
        await using var session = await service.StartAsync("user-1", spec, [], CancellationToken.None);
        var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.That(result.Success, Is.False);
        Assert.That(result.CredentialsRejected, Is.True, result.Error);
        Assert.That(_signIns, Is.Zero);
    }
}
