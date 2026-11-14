using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using OpenBudget.Browser;
using OpenBudget.Connectors;
using OpenBudget.Connectors.Ing;
using OpenBudget.Tests.Connectors;

namespace OpenBudget.E2E.Tests;

/// <summary>
/// ING's web banking part against a real Chromium and the made-up ING served locally: a sign-in page rendered by script
/// under a hash route, a device choice, the app confirmation polled by the page, an empty page that forwards by script,
/// then the Wicket pages (overview, CSV export, Postbox) read inside the kept browser. Skipped when no Chromium is
/// present. No bank is contacted.
/// </summary>
#pragma warning disable CA1001 // the listener is closed in TearDown
[TestFixture]
public sealed class RealBrowserIngTests
{
    private static readonly ConnectorCredentials Credentials = new(FakeIng.Alias, "654321PIN");

    private HttpListener _listener = null!;
    private Task _serving = Task.CompletedTask;
    private HttpMessageInvoker _invoker = null!;
    private FakeIng _bank = null!;
    private string _baseUrl = "";
    private string _chrome = "";

    [SetUp]
    public void SetUp()
    {
        _chrome = ChromiumLocator.Find() ?? "";
        if (_chrome.Length == 0) Assert.Ignore("No Chromium found (set CHROME_PATH or run `playwright install chromium`).");
        (_listener, _baseUrl) = LocalHttpListener.Start();
        _bank = new FakeIng(_baseUrl);
        _invoker = new HttpMessageInvoker(_bank, disposeHandler: false);
        _serving = Task.Run(ServeAsync);
    }

    [TearDown]
    public async Task TearDown()
    {
        _listener?.Close();
        try { await _serving; } catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { /* closed */ }
        _invoker?.Dispose();
        _bank?.Dispose();
    }

    /// <summary>Hands every request to the made-up ING (method, headers, body) and writes its answer back.</summary>
    private async Task ServeAsync()
    {
        while (_listener.IsListening)
        {
            var ctx = await _listener.GetContextAsync();
            try
            {
                var request = ctx.Request;
                using var message = new HttpRequestMessage(new HttpMethod(request.HttpMethod), request.Url);
                if (request.HasEntityBody)
                {
                    using var body = new MemoryStream();
                    await request.InputStream.CopyToAsync(body);
                    message.Content = new ByteArrayContent(body.ToArray());
                    if (request.ContentType != null) message.Content.Headers.TryAddWithoutValidation("Content-Type", request.ContentType);
                }
                foreach (var name in request.Headers.AllKeys.OfType<string>())
                {
                    if (!name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
                        message.Headers.TryAddWithoutValidation(name, request.Headers.GetValues(name) ?? []);
                }
                using var response = await _invoker.SendAsync(message, CancellationToken.None);
                ctx.Response.StatusCode = (int)response.StatusCode;
                if (response.Headers.Location is { } location) ctx.Response.RedirectLocation = location.ToString();
                if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
                {
                    foreach (var c in cookies) ctx.Response.AppendHeader("Set-Cookie", c);
                }
                var bytes = await response.Content.ReadAsByteArrayAsync();
                if (response.Content.Headers.ContentType is { } type) ctx.Response.ContentType = type.ToString();
                ctx.Response.ContentLength64 = bytes.Length;
                await ctx.Response.OutputStream.WriteAsync(bytes);
            }
            catch (Exception ex) when (ex is HttpListenerException or IOException)
            {
                // the browser went away
            }
            finally
            {
                ctx.Response.Close();
            }
        }
    }

    private BrowserService NewBrowser() => new(NullLogger<BrowserService>.Instance, new BrowserOptions { ExecutablePath = _chrome, IdleShutdown = TimeSpan.FromSeconds(1), Headless = true });

    private static WebLoginService Service(BrowserService browser) => new(browser,
        new WebLoginOptions { PollInterval = TimeSpan.FromMilliseconds(100), StuckAfter = TimeSpan.FromSeconds(2), CodeSettle = TimeSpan.FromMilliseconds(500), DismissWait = TimeSpan.FromMilliseconds(500), SettleDelay = TimeSpan.FromMilliseconds(100), FormTimeout = TimeSpan.FromSeconds(10), Timeout = TimeSpan.FromSeconds(60) },
        TimeProvider.System, NullLoggerFactory.Instance);

    private IngWebClient Client(string? device = null) =>
        new(new Uri(_baseUrl), TimeProvider.System, NullLogger.Instance) { PreferredDevice = device };

    [Test]
    public async Task The_script_sign_in_with_the_remembered_device_hands_a_browser_that_reads_accounts_transactions_and_the_Postbox()
    {
        await using var browser = NewBrowser();
        var service = Service(browser);
        var client = Client("tablet / Example Tablet");
        var spec = client.WebLogin(Credentials);

        await using var session = await service.StartAsync("user-1", spec, [], CancellationToken.None);
        var modes = new List<WebLoginMode>();
        session.ModeChanged += m => { lock (modes) modes.Add(m); };
        var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.That(result.Success, Is.True, result.Error);
        Assert.That(modes, Does.Not.Contain(WebLoginMode.Interactive), "the remembered device needs nobody");
        Assert.That(_bank.ChosenDevice, Is.EqualTo("tablet / Example Tablet"));
        Assert.That((_bank.Pushes, _bank.SilentApprovals, _bank.SignIns), Is.EqualTo((1, 0, 1)));
        Assert.That(result.Cookies.Select(c => c.Name), Does.Contain(FakeIng.SessionCookie));

        await using var kept = session.TakeBrowser();
        Assert.That(kept, Is.Not.Null);
        await client.AttachAsync(kept!, CancellationToken.None);
        Assert.That(client.PreferredDevice, Is.EqualTo("tablet / Example Tablet"));
        Assert.That(client.Accounts.Select(a => a.Iban), Does.Contain(FakeIng.VlIban).And.Contain(FakeIng.CreditIban));

        var vl = await client.TransactionsAsync(FakeIng.VlIban, null, CancellationToken.None);
        Assert.That(vl.Transactions, Has.Count.EqualTo(2));
        Assert.That(vl.Balance, Is.EqualTo(1040m));

        var entries = await client.ListPostboxAsync(full: true, CancellationToken.None);
        Assert.That(entries, Has.Count.EqualTo(_bank.Documents.Count));
        Assert.That(_bank.Ranges, Does.Contain("DATUM"));
        var pdf = await client.DownloadAsync(entries.First(e => e.Row.Type == "Jahressteuerbescheinigung").Download, CancellationToken.None);
        Assert.That(Encoding.ASCII.GetString(pdf!, 0, 5), Is.EqualTo("%PDF-"));
        Assert.That(await client.DownloadAsync(entries.First(e => e.Row.Subject == "Broken document").Download, CancellationToken.None), Is.Null);
    }

    [Test]
    public async Task Without_a_remembered_device_the_page_is_shown_for_the_choice_and_a_stored_session_resumes_without_a_sign_in()
    {
        await using var browser = NewBrowser();
        var service = Service(browser);
        var spec = Client().WebLogin(Credentials);

        await using (var session = await service.StartAsync("user-1", spec, [], CancellationToken.None))
        {
            var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            session.ModeChanged += m => { if (m == WebLoginMode.Interactive) shown.TrySetResult(); };
            if (session.Mode == WebLoginMode.Interactive) shown.TrySetResult();
            await shown.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.That(_bank.ChosenDevice, Is.Null, "two devices and none remembered: the user picks");
            Assert.That(_bank.Count("/app/login/api/authenticate"), Is.EqualTo(1), "the access number and PIN went in by themselves");
        }

        // a single device needs no choice; the session it leaves behind opens the overview next time
        _bank.Devices = ["phone / Example Phone"];
        IReadOnlyList<CookieState> cookies;
        await using (var first = await service.StartAsync("user-1", spec, [], CancellationToken.None))
        {
            var result = await first.Completion.WaitAsync(TimeSpan.FromSeconds(60));
            Assert.That(result.Success, Is.True, result.Error);
            cookies = result.Cookies;
        }
        Assert.That(_bank.SignIns, Is.EqualTo(1));
        await using var second = await service.StartAsync("user-1", spec, cookies, CancellationToken.None);
        var resumed = await second.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.That(resumed.Success, Is.True, resumed.Error);
        Assert.That(_bank.SignIns, Is.EqualTo(1), "no second sign-in");
    }

    [Test]
    public async Task The_PIN_on_a_page_of_its_own_is_entered_there()
    {
        await using var browser = NewBrowser();
        var service = Service(browser);
        _bank.PinOnNextPage = true;
        _bank.Devices = ["phone / Example Phone"];
        await using var session = await service.StartAsync("user-1", Client().WebLogin(Credentials), [], CancellationToken.None);
        var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.That(result.Success, Is.True, result.Error);
        Assert.That((_bank.Count("/app/login/api/authenticate"), _bank.SignIns, _bank.Pushes), Is.EqualTo((1, 1, 1)));
    }

    [Test]
    public async Task A_confirmation_rejected_in_the_app_ends_the_sign_in()
    {
        await using var browser = NewBrowser();
        var service = Service(browser);
        _bank.Devices = ["phone / Example Phone"];
        _bank.Reject = true;
        await using var session = await service.StartAsync("user-1", Client().WebLogin(Credentials), [], CancellationToken.None);
        var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.That(result.Success, Is.False);
        Assert.That(result.CredentialsRejected, Is.False);
        Assert.That(result.Error, Does.Contain("did not confirm"));
    }

    [Test]
    public async Task A_wrong_PIN_is_reported_as_rejected_credentials()
    {
        await using var browser = NewBrowser();
        var service = Service(browser);
        _bank.Pin = "other";
        await using var session = await service.StartAsync("user-1", Client().WebLogin(Credentials), [], CancellationToken.None);
        var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.That(result.CredentialsRejected, Is.True, result.Error);
    }
}
