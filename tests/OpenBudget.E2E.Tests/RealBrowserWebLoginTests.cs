using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
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

        (_listener, _baseUrl) = LocalHttpListener.Start();
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
            else if (path == "/twostep")
            {
                html = TwoStepPage;
            }
            else if (path == "/alert")
            {
                // A "browser not supported" notice in front of a plain login form (like a bank's portal).
                html = "<html><body><div id=a><p>Ihr Browser wird nicht unterstützt</p><button onclick=\"document.getElementById('a').remove();document.getElementById('f').style.display='block'\">Ich bin mir der möglichen Risiken bewusst</button></div>"
                    + "<form id=f style=display:none onsubmit=\"event.preventDefault();if(document.getElementById('u').value==='alice'&&document.getElementById('p').value==='pw'){document.cookie='sid=abc; path=/; max-age=3600';location.href='/home';}\">"
                    + "<input id=u><input id=p type=password><button id=go type=submit>Login</button></form></body></html>";
            }
            else if (path == "/masked")
            {
                html = MaskedCodePage;
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

    /// <summary>
    /// Like Openbank's React sign-in: an e-mail field without type/name/id whose value only "counts" through React's
    /// value tracker (a setter that remembers the last value; the input handler ignores events whose value did not
    /// change through the native setter), a "Weiter" button that stays disabled until then, then the access code, one digit per box.
    /// The form appears after a delay (the SPA is loading).
    /// </summary>
    private const string TwoStepPage = """
        <html><body><div id="root"></div><script>
        function trackedInput(attrs, onValue) {
          const el = document.createElement('input');
          for (const k in attrs) el.setAttribute(k, attrs[k]);
          const desc = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value');
          let tracked = '';
          Object.defineProperty(el, 'value', { configurable: true, get() { return desc.get.call(this); }, set(v) { tracked = String(v); desc.set.call(this, v); } });
          el.addEventListener('input', () => { const v = desc.get.call(el); if (v !== tracked) { tracked = v; onValue(v); } });
          return el;
        }
        setTimeout(() => {
          const root = document.getElementById('root');
          const next = document.createElement('button');
          next.setAttribute('type', 'primary'); next.setAttribute('data-testid', 'login-email-links'); next.disabled = true; next.textContent = 'Weiter';
          let email = '';
          const input = trackedInput({ placeholder: 'E-Mail-Adresse eingeben', 'data-testid': 'login-email-email' }, v => { email = v; next.disabled = !v.includes('@'); });
          next.onclick = () => {
            if (email !== 'alice@example.com') return;
            root.innerHTML = '';
            const submit = document.createElement('button');
            submit.setAttribute('type', 'primary'); submit.setAttribute('data-testid', 'login-gaps-button'); submit.disabled = true; submit.textContent = 'Anmelden';
            const digits = ['', '', '', ''];
            const boxes = digits.map((_, i) => trackedInput({ type: 'tel', maxlength: '1', 'data-testid': 'password-module-inputs-gap-' + i }, v => {
              digits[i] = v; submit.disabled = digits.some(d => d.length !== 1);
              if (v && i < 3) boxes[i + 1].focus(); // auto-advance like the real page
            }));
            submit.onclick = () => { if (digits.join('') === '8264') { document.cookie = 'sid=abc; path=/; max-age=3600'; location.href = '/home'; } };
            root.append(...boxes, submit);
          };
          root.append(input, next);
        }, 300);
        </script></body></html>
        """;

    /// <summary>
    /// An access code form whose boxes take each digit into the page's state, then mask it ("•") and move the focus on.
    /// Every input event whose value differs from what the page last wrote counts as a typed digit, as in a React
    /// component: a digit set a second time after the masking would enter it twice. A wrong code shows Openbank's error box.
    /// </summary>
    private const string MaskedCodePage = """
        <html><body><div id="root"></div><script>
        const desc = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value');
        let code = '';
        const submit = document.createElement('button');
        submit.setAttribute('data-testid', 'login-gaps-button'); submit.disabled = true; submit.textContent = 'Anmelden';
        const boxes = [0, 1, 2, 3].map(i => {
          const el = document.createElement('input');
          el.setAttribute('type', 'tel'); el.setAttribute('maxlength', '1'); el.setAttribute('data-testid', 'password-module-inputs-gap-' + i);
          let tracked = '';
          Object.defineProperty(el, 'value', { configurable: true, get() { return desc.get.call(this); }, set(v) { tracked = String(v); desc.set.call(this, v); } });
          el.addEventListener('input', () => {
            const v = desc.get.call(el);
            if (v === tracked) return;
            const digit = v.replace('•', '');
            if (!digit) return;
            code += digit;
            el.value = '•';
            submit.disabled = code.length < 4;
            if (i < 3) boxes[i + 1].focus();
          });
          return el;
        });
        submit.onclick = () => {
          if (code === '8264') { document.cookie = 'sid=abc; path=/; max-age=3600'; location.href = '/home'; return; }
          const err = document.createElement('div');
          err.setAttribute('role', 'alert');
          err.textContent = 'Vergewissere dich, dass die E-Mail-Adresse und der Zugangscode korrekt sind.';
          document.getElementById('root').append(err);
        };
        document.getElementById('root').append(...boxes, submit);
        </script></body></html>
        """;

    private WebLoginSpec MaskedSpec(string code) => new(new Uri(_baseUrl + "masked"), ["127.0.0.1"], new Uri(_baseUrl + "home"))
    {
        Autofill = new WebLoginAutofill("alice@example.com", code,
        [
            new WebLoginStep([new WebLoginField("[data-testid=password-module-inputs-gap-{0}]", WebLoginCredential.Secret, 4)],
                "[data-testid=login-gaps-button]:not([disabled])"),
        ]),
        CredentialsError = new WebLoginError("[role=alert]", ["Zugangscode korrekt"], "Openbank rejected the e-mail address or access code."),
    };

    [Test]
    public async Task Browser_keeps_its_native_identity_and_cookies_survive_a_fresh_context()
    {
        // The identity of the headed browser that web logins use. A headed browser is only invisible under Xvfb (Linux
        // without a display); elsewhere the test runs when OPENBUDGET_TEST_HEADED=1 asks for the window.
        if (!(OperatingSystem.IsLinux() && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
            && Environment.GetEnvironmentVariable("OPENBUDGET_TEST_HEADED") != "1")
            Assert.Ignore("Opens a browser window; set OPENBUDGET_TEST_HEADED=1 to run it here.");
        await using var browser = NewBrowser(headless: false);
        IReadOnlyList<CookieState> cookies;
        string userAgent;
        await using (var page = await browser.OpenSessionAsync(CancellationToken.None))
        {
            await page.NavigateAsync(new Uri(_baseUrl + "home"), CancellationToken.None);
            var native = await page.CheckAsync("""
                () => ({
                    ok: !navigator.userAgent.includes('HeadlessChrome')
                        && !navigator.userAgentData.brands.some(b => b.brand === 'Not/A)Brand')
                        && navigator.webdriver === false
                        && window.open.toString().includes('[native code]')
                        && !Object.prototype.hasOwnProperty.call(navigator, 'languages'),
                    reason: navigator.userAgent
                })
                """, "");
            Assert.That(native.Ok, Is.True, native.Reason);
            Assert.That(page.UserAgent, Is.EqualTo(native.Reason));
            userAgent = page.UserAgent;
            await page.CheckAsync("() => { document.cookie = 'verification=verified; path=/; max-age=3600'; return { ok: true, reason: '' }; }", "");
            cookies = await page.GetCookiesAsync();
        }
        await using var restored = await browser.OpenSessionAsync(CancellationToken.None);
        await restored.SetCookiesAsync(cookies);
        await restored.NavigateAsync(new Uri(_baseUrl + "home"), CancellationToken.None);
        Assert.That(restored.UserAgent, Is.EqualTo(userAgent));
        Assert.That((await restored.GetCookiesAsync()).Any(c => c.Name == "verification" && c.Value == "verified"), Is.True);
    }

    [Test]
    public async Task A_landing_page_that_still_needs_a_second_factor_is_shown_and_does_not_count_as_signed_in()
    {
        await using var browser = NewBrowser();
        var service = new WebLoginService(browser, new WebLoginOptions { SettleDelay = TimeSpan.FromMilliseconds(100), FormTimeout = TimeSpan.FromSeconds(10) },
            TimeProvider.System, NullLoggerFactory.Instance);
        var pending = new WebLoginSpec(new Uri(_baseUrl + "login"), ["127.0.0.1"], new Uri(_baseUrl + "home"))
        {
            SuccessCheck = "() => ({ ok: localStorage.getItem('tan') === '1', reason: '' })",
        };
        await using (var session = await service.StartAsync("user-1", pending, [], CancellationToken.None))
        {
            await Task.Delay(3000);
            Assert.That(session.Completion.IsCompleted, Is.False);
            Assert.That(session.Mode, Is.EqualTo(WebLoginMode.Interactive));
        }

        var done = new WebLoginSpec(new Uri(_baseUrl + "login"), ["127.0.0.1"], new Uri(_baseUrl + "home"))
        {
            SuccessCheck = "() => ({ ok: true, reason: '' })",
        };
        await using var second = await service.StartAsync("user-1", done, [], CancellationToken.None);
        Assert.That((await second.Completion.WaitAsync(TimeSpan.FromSeconds(30))).Success, Is.True);
    }

    [Test]
    public async Task A_browser_notice_in_front_of_the_form_is_clicked_away_before_filling_it_in()
    {
        await using var browser = NewBrowser();
        var service = new WebLoginService(browser, new WebLoginOptions { SettleDelay = TimeSpan.FromMilliseconds(100), FormTimeout = TimeSpan.FromSeconds(10) },
            TimeProvider.System, NullLoggerFactory.Instance);
        var spec = new WebLoginSpec(new Uri(_baseUrl + "alert"), ["127.0.0.1"], new Uri(_baseUrl + "home"))
        {
            Dismiss = ["button::-p-text(Risiken bewusst)"],
            Autofill = new WebLoginAutofill("alice", "pw", [new WebLoginStep([new WebLoginField("#u", WebLoginCredential.Username), new WebLoginField("#p", WebLoginCredential.Secret)], "#go")]),
        };
        await using var session = await service.StartAsync("user-1", spec, [], CancellationToken.None);
        var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.That(result.Success, Is.True, result.Error);
    }

    [Test]
    public async Task Masked_auto_advancing_code_boxes_get_each_digit_exactly_once()
    {
        await using var browser = NewBrowser();
        using var logs = new OpenbankWebLoginTests.Logs();
        using var factory = Microsoft.Extensions.Logging.LoggerFactory.Create(b => b.AddProvider(logs));
        var service = new WebLoginService(browser, new WebLoginOptions { SettleDelay = TimeSpan.FromMilliseconds(100), FormTimeout = TimeSpan.FromSeconds(10) },
            TimeProvider.System, factory);
        await using var session = await service.StartAsync("user-1", MaskedSpec("8264"), [], CancellationToken.None);
        var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(60)).ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : WebLoginResult.Fail("timeout"), TaskScheduler.Default);
        Assert.That(result.Success, Is.True, result.Error + "\n" + string.Join("\n", logs.Lines));
    }

    [Test]
    public async Task The_bank_s_error_box_ends_the_login_as_rejected_credentials()
    {
        await using var browser = NewBrowser();
        var service = new WebLoginService(browser, new WebLoginOptions { SettleDelay = TimeSpan.FromMilliseconds(100), FormTimeout = TimeSpan.FromSeconds(10), StuckAfter = TimeSpan.FromSeconds(30) },
            TimeProvider.System, NullLoggerFactory.Instance);
        await using var session = await service.StartAsync("user-1", MaskedSpec("1111"), [], CancellationToken.None);
        var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.That(result.Success, Is.False);
        Assert.That(result.CredentialsRejected, Is.True);
        Assert.That(result.Error, Does.Contain("rejected"));
    }

    [Test]
    public async Task A_two_step_react_form_is_filled_and_submitted_page_by_page()
    {
        await using var browser = NewBrowser();
        var service = new WebLoginService(browser, new WebLoginOptions { SettleDelay = TimeSpan.FromMilliseconds(100), FormTimeout = TimeSpan.FromSeconds(10) },
            TimeProvider.System, NullLoggerFactory.Instance);
        var spec = new WebLoginSpec(new Uri(_baseUrl + "twostep"), ["127.0.0.1"], new Uri(_baseUrl + "home"))
        {
            Autofill = new WebLoginAutofill("alice@example.com", "8264",
            [
                new WebLoginStep("[data-testid=login-email-email], input[type=email], input[placeholder*=Mail i]", WebLoginCredential.Username,
                    "[data-testid=login-email-links]:not([disabled])"),
                new WebLoginStep([new WebLoginField("[data-testid=password-module-inputs-gap-{0}]", WebLoginCredential.Secret, 4)],
                    "[data-testid=login-gaps-button]:not([disabled])"),
            ]),
        };

        await using var session = await service.StartAsync("user-1", spec, [], CancellationToken.None);
        var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.That(result.Success, Is.True, result.Error);
        Assert.That(result.Cookies.Select(c => c.Name), Has.Member("sid"));
    }

    private BrowserService NewBrowser(bool headless = true) => new(NullLogger<BrowserService>.Instance, new BrowserOptions { ExecutablePath = _chrome, IdleShutdown = TimeSpan.FromSeconds(1), Headless = headless });

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
