using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Globalization;
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
    private bool _requiresMfa;
    private bool _splitMfa;
    private DateTime _reportStart;
    private DateTime _reportEnd;

    [SetUp]
    public void SetUp()
    {
        _combinedLogin = false;
        _requiresMfa = false;
        _splitMfa = false;
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
        r.Cookies["sid"] is { } sid && _sessions.TryGetValue(sid.Value, out var signedIn) && signedIn && r.Headers["Sec-Fetch-Mode"] != null;

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
                _sessions[sid] = !_requiresMfa;
                Interlocked.Increment(ref _signIns);
                ctx.Response.AppendHeader("Set-Cookie", $"sid={sid}; Path=/; HttpOnly; SameSite=Lax");
                Redirect(ctx, _requiresMfa ? "/authflow/challenges/softwareToken/" : "/myaccount/summary");
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
        if (path == "/authflow/challenges/softwareToken/" && request.Cookies["sid"] is { } pending)
        {
            if (request.HttpMethod == "POST")
            {
                var form = await ReadFormAsync(request);
                var code = _splitMfa ? string.Concat(Enumerable.Range(0, 6).Select(i => form.GetValueOrDefault($"code{i}"))) : form.GetValueOrDefault("code");
                if (code == "123456")
                {
                    _sessions[pending.Value] = true;
                    Redirect(ctx, "/myaccount/summary");
                    return;
                }
            }
            var fields = _splitMfa
                ? string.Concat(Enumerable.Range(0, 6).Select(i => $"<input name=code{i} inputmode=numeric maxlength=1 oninput='this.nextElementSibling?.focus()'>"))
                : "<input name=code autocomplete=one-time-code maxlength=6>";
            await HtmlAsync(ctx, "<html><body><form method=post>" + fields + "<button type=submit>Verify</button></form></body></html>");
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
                var bootstrap = JsonSerializer.Serialize(new { _csrf = Csrf, locality = new { timezone = "Europe/Berlin" } });
                await HtmlAsync(ctx, $"<html><body><script id='server-data' type='application/json'>{bootstrap}</script><script>document.getElementById('server-data').remove()</script>Download activity</body></html>");
                return;
            case "/reports/apis/dlog/ql" when request.HttpMethod == "POST":
                Assert.That(request.Headers["X-CSRF-Token"], Is.EqualTo(Csrf));
                var refreshed = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow.AddHours(-1), TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles"));
                await WriteAsync(ctx, "application/json", JsonSerializer.Serialize(new[] { new { userGroup = "Premier", data = new { dlogRefreshInfo = new { last_updated = refreshed.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) } } } }));
                return;
            case "/reports/apis/common/ql" when request.HttpMethod == "POST":
                Assert.That(request.Headers["X-CSRF-Token"], Is.EqualTo(Csrf));
                Assert.That(request.Headers["X-Requested-With"], Is.EqualTo("XMLHttpRequest"));
                using (var reader = new StreamReader(request.InputStream, Encoding.UTF8))
                using (var body = JsonDocument.Parse(await reader.ReadToEndAsync()))
                {
                    var operation = body.RootElement.GetProperty("apiType").GetString();
                    Assert.That(body.RootElement.GetProperty("reportType").GetString(), Is.EqualTo("DLOG"));
                    if (operation == "reportCreate")
                    {
                        var form = body.RootElement.GetProperty("formdata");
                        Assert.That(form.GetProperty("filters").GetString(), Is.EqualTo("BALANCE_IMPACTING"));
                        Assert.That(form.GetProperty("name").GetString(), Is.EqualTo("DLOGTEMPLATE"));
                        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
                        _reportStart = TimeZoneInfo.ConvertTimeFromUtc(DateTime.ParseExact(form.GetProperty("start_date").GetString()!, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal), zone);
                        _reportEnd = TimeZoneInfo.ConvertTimeFromUtc(DateTime.ParseExact(form.GetProperty("end_date").GetString()!, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal), zone);
                        Interlocked.Increment(ref _reports);
                        await WriteAsync(ctx, "application/json", """[{"document_id":"R1"}]""");
                        return;
                    }
                    if (operation == "reports")
                    {
                        var reports = _reports == 0 ? Array.Empty<object>() : new object[] { new
                        {
                            documentStartDate = _reportStart.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                            documentEndDate = _reportEnd.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                            documentFileFormat = "CSV", documentMetadata = new { filters = "BALANCE_IMPACTING" },
                            status = "AVAILABLE", downloadUrls = new[] { "R1.CSV" },
                        } };
                        await WriteAsync(ctx, "application/json", JsonSerializer.Serialize(new[] { new { reportDetails = reports } }));
                        return;
                    }
                    Assert.That(operation, Is.EqualTo("download"));
                    Assert.That(body.RootElement.GetProperty("reportNames")[0].GetString(), Is.EqualTo("R1.CSV"));
                    var day = DateTime.UtcNow.AddDays(-1).ToString("MM/dd/yyyy", CultureInfo.InvariantCulture);
                    await WriteAsync(ctx, "application/octet-stream", Csv.Replace("{0}", day, StringComparison.Ordinal));
                    return;
                }
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

    [TestCase(false)]
    [TestCase(true)]
    public async Task A_security_code_uses_the_shared_prompt_and_retries_without_showing_the_browser(bool split)
    {
        _combinedLogin = true;
        _requiresMfa = true;
        _splitMfa = split;
        await using var browser = NewBrowser();
        using var http = new BankHttpClient();
        var spec = (await Connector(http).LoginAsync(Credentials, null, null, CancellationToken.None)).Challenge!.WebLoginSpec!;
        var asked = 0;
        var modes = new List<WebLoginMode>();
        Task<ChallengeAnswer?> AnswerCode(ConnectorChallenge challenge, CancellationToken ct)
        {
            Assert.That(challenge.Kind, Is.EqualTo(ChallengeKind.Code));
            Assert.That(challenge.Length, Is.EqualTo(6));
            Assert.That(challenge.Numeric, Is.True);
            return Task.FromResult<ChallengeAnswer?>(new ChallengeAnswer(challenge, ++asked == 1 ? "000000" : "123456"));
        }
        await using var login = await Service(browser).StartAsync("user-1", spec, [], CancellationToken.None, AnswerCode);
        login.ModeChanged += mode => modes.Add(mode);
        var result = await login.Completion.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.That(result.Success, Is.True, result.Error);
        Assert.That(asked, Is.EqualTo(2));
        Assert.That(modes, Does.Not.Contain(WebLoginMode.Interactive));
        await using var kept = login.TakeBrowser();
    }

    [Test]
    public async Task A_security_check_without_a_code_prompt_handler_automatically_shows_the_browser()
    {
        _combinedLogin = true;
        _requiresMfa = true;
        await using var browser = NewBrowser();
        using var http = new BankHttpClient();
        var spec = (await Connector(http).LoginAsync(Credentials, null, null, CancellationToken.None)).Challenge!.WebLoginSpec!;
        await using var login = await Service(browser).StartAsync("user-1", spec, [], CancellationToken.None);
        for (var i = 0; i < 300 && login.Mode != WebLoginMode.Interactive; i++) await Task.Delay(100);
        Assert.That(login.Mode, Is.EqualTo(WebLoginMode.Interactive));
        Assert.That(login.Completion.IsCompleted, Is.False);
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
