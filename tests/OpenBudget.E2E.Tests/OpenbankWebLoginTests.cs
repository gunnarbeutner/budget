using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using OpenBudget.Browser;
using OpenBudget.Connectors;
using OpenBudget.Connectors.Openbank;

namespace OpenBudget.E2E.Tests;

/// <summary>
/// Openbank's web login (the connector's real spec, check and repair scripts) against a real Chromium and a local page
/// that imitates the bank's React sign-in: controlled single-digit boxes whose state lives in a parent component reached
/// through <c>__reactFiber$</c> objects, auto-advance to the next box, a keyup handler that writes the box the focus landed
/// on, and the page's own login API call. No bank is contacted.
/// </summary>
#pragma warning disable CA1001 // the listener is closed in TearDown
[TestFixture]
public sealed class OpenbankWebLoginTests
{
    private const string Code = "8264";

    private HttpListener _listener = null!;
    private Task _serving = Task.CompletedTask;
    private string _baseUrl = "";
    private string _chrome = "";
    private string _mode = "ok";
    private int _loginCalls;

    [SetUp]
    public void SetUp()
    {
        _loginCalls = 0;
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
            string body;
            var type = "text/html";
            if (path == "/amazon/login")
            {
                body = Page.Replace("%MODE%", _mode, StringComparison.Ordinal);
            }
            else if (path == "/amazon/cb-api-auth/authentication/login")
            {
                Interlocked.Increment(ref _loginCalls);
                using var reader = new StreamReader(ctx.Request.InputStream);
                var sent = await reader.ReadToEndAsync();
                type = "application/json";
                if (sent == Code) body = "{}";
                else
                {
                    ctx.Response.StatusCode = 401;
                    body = """{"code":"INVALID_CREDENTIALS","message":"wrong","token":"secret-token-value-that-must-not-be-logged"}""";
                }
            }
            else
            {
                body = "<html><body><h1>Home</h1></body></html>";
            }
            var bytes = Encoding.UTF8.GetBytes(body);
            ctx.Response.ContentType = type;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
    }

    /// <summary>
    /// MODE: ok (state follows the input), lose-once (the first digit for position 2 never reaches the state, while the box
    /// shows it), lose-always (no input for position 2 ever reaches the state through the DOM path or the handlers).
    /// </summary>
    private const string Page = """
        <html><body><div id="root"></div><script>
        const MODE = '%MODE%';
        const root = document.getElementById('root');
        const email = document.createElement('input');
        email.setAttribute('data-testid', 'login-email-email'); email.placeholder = 'E-Mail-Adresse';
        const next = document.createElement('button');
        next.setAttribute('data-testid', 'login-email-links'); next.disabled = true; next.textContent = 'Weiter';
        email.addEventListener('input', () => { next.disabled = !email.value.includes('@'); });
        root.append(email, next);

        let state = ['', '', '', ''];
        let lost = 0;
        const boxes = [];
        const submit = document.createElement('button');
        submit.setAttribute('data-testid', 'login-gaps-button'); submit.textContent = 'Anmelden'; submit.disabled = true;

        // fiber-like chain: input -> wrapper -> L (cell, depth 2) -> ... -> Q (code, depth 9)
        const q = { memoizedProps: null, return: { memoizedProps: { app: true }, return: null } };
        let above = q;
        for (let d = 8; d > 2; d--) above = { memoizedProps: { depth: d }, return: above };
        const cellFibers = [], inputFibers = [];

        function setPos(position, value) {
          if (position === 2 && value && (MODE === 'lose-always' || (MODE === 'lose-once' && lost++ === 0))) return;
          const nextState = state.slice(); nextState[position] = value; // stale closure, as a React setter with a value
          setTimeout(() => { state = nextState; render(); }, 0);
        }
        function render() {
          q.memoizedProps = { positions: [], cellsAmount: 4, value: state.join(''), onChange: () => {}, onBlur: () => {} };
          boxes.forEach((el, i) => {
            if (state[i]) el.value = state[i]; // a lost digit stays visible in the box: the DOM and the state disagree
            const cell = { position: i, value: state[i], isBlocked: false,
              onChange: ({ value, position }) => setPos(position, value),
              onKeyUp: ({ value, position }) => setPos(position, value), onBlur: () => {} };
            cellFibers[i].memoizedProps = cell;
            const props = { type: 'tel', maxLength: 1, value: state[i],
              onChange: e => cell.onChange({ value: isNaN(+e.target.value) ? '' : e.target.value, position: i }),
              onKeyUp: e => e.code === 'Backspace' ? cell.onKeyUp({ value: e.target.value, position: i }) : cell.onChange({ value: e.target.value, position: i }),
              onBlur: () => {} };
            inputFibers[i].memoizedProps = props;
            el.__reactProps$fake = props;
          });
          submit.disabled = boxes.some(b => b.value.length !== 1);
        }
        function showCode() {
          root.innerHTML = '';
          for (let i = 0; i < 4; i++) {
            const el = document.createElement('input');
            el.setAttribute('type', 'tel'); el.setAttribute('maxlength', '1'); el.setAttribute('data-testid', 'password-module-inputs-gap-' + i);
            el.style.webkitTextSecurity = 'disc';
            cellFibers[i] = { memoizedProps: null, return: above };
            inputFibers[i] = { memoizedProps: null, return: { memoizedProps: { wrapper: true }, return: cellFibers[i] } };
            el.__reactFiber$fake = inputFibers[i];
            el.addEventListener('input', e => { el.__reactProps$fake.onChange(e); if (el.value && i < 3) boxes[i + 1].focus(); });
            el.addEventListener('keyup', e => el.__reactProps$fake.onKeyUp(e)); // lands on whichever box has the focus
            boxes.push(el);
          }
          const form = document.createElement('div');
          form.append(...boxes, submit);
          root.append(form);
          render();
        }
        next.onclick = () => setTimeout(showCode, 100);
        submit.onclick = async () => {
          const r = await fetch('/amazon/cb-api-auth/authentication/login', { method: 'POST', body: q.memoizedProps.value });
          if (r.ok) { document.cookie = 'accessToken=t; path=/; max-age=3600'; location.href = '/amazon/home'; return; }
          const box = document.createElement('div');
          box.innerHTML = '<div data-testid="login-error-title">Fehler</div><div data-testid="login-error-table-row-description">Vergewissere dich, dass die E-Mail-Adresse und der Zugangscode&nbsp;korrekt sind.</div>';
          root.append(box);
        };
        </script></body></html>
        """;

    internal sealed class Logs : ILoggerProvider, ILogger
    {
        public List<string> Lines { get; } = [];

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Lines) Lines.Add(formatter(state, exception));
        }

        public void Dispose()
        {
        }
    }

    private async Task<(WebLoginSession Session, Logs Logs, BrowserService Browser)> StartAsync(string mode, string code)
    {
        _mode = mode;
        var options = new OpenbankOptions { SiteBase = new Uri(_baseUrl), LoginHost = "127.0.0.1" };
        WebLoginSpec spec;
        using (var http = new BankHttpClient())
            spec = new OpenbankConnector(new ConnectorContext(http, TimeProvider.System, NullLogger.Instance), options)
                .WebLogin(new ConnectorCredentials("alice@example.com", code));
        var logs = new Logs();
        var browser = new BrowserService(NullLogger<BrowserService>.Instance, new BrowserOptions { ExecutablePath = _chrome, IdleShutdown = TimeSpan.FromSeconds(1), Headless = true });
#pragma warning disable CA2000 // the factory lives as long as the test
        var factory = LoggerFactory.Create(b => b.AddProvider(logs));
#pragma warning restore CA2000
        var service = new WebLoginService(browser,
            new WebLoginOptions { SettleDelay = TimeSpan.FromMilliseconds(100), FormTimeout = TimeSpan.FromSeconds(10), StuckAfter = TimeSpan.FromSeconds(30) },
            TimeProvider.System, factory);
        return (await service.StartAsync("user-1", spec, [], CancellationToken.None), logs, browser);
    }

    private static void AssertNoDigitsLogged(Logs logs)
    {
        lock (logs.Lines)
        {
            Assert.That(logs.Lines, Has.None.Contain(Code));
            Assert.That(logs.Lines, Has.None.Contain("secret-token"));
        }
    }

    [Test]
    public async Task A_verified_access_code_is_submitted_once()
    {
        var (session, logs, browser) = await StartAsync("ok", Code);
        await using (browser)
        await using (session)
        {
            var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(60));
            Assert.That(result.Success, Is.True, result.Error + "\n" + string.Join("\n", logs.Lines));
            Assert.That(_loginCalls, Is.EqualTo(1));
            lock (logs.Lines) Assert.That(logs.Lines, Has.Some.Contain("authentication/login answered 200"));
            AssertNoDigitsLogged(logs);
        }
    }

    [Test]
    public async Task A_lost_digit_is_put_into_the_state_through_the_page_s_handlers_before_submitting()
    {
        var (session, logs, browser) = await StartAsync("lose-once", Code);
        await using (browser)
        await using (session)
        {
            var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(60));
            Assert.That(result.Success, Is.True, result.Error + "\n" + string.Join("\n", logs.Lines));
            Assert.That(_loginCalls, Is.EqualTo(1));
            lock (logs.Lines)
            {
                Assert.That(logs.Lines, Has.Some.Contain("access code state has 3 characters"));
                Assert.That(logs.Lines, Has.Some.Contain("repaired"));
            }
            AssertNoDigitsLogged(logs);
        }
    }

    [Test]
    public async Task A_code_the_state_never_takes_is_not_submitted_and_the_page_is_shown()
    {
        var (session, logs, browser) = await StartAsync("lose-always", Code);
        await using (browser)
        await using (session)
        {
            var until = DateTime.UtcNow + TimeSpan.FromSeconds(60);
            while (session.Mode != WebLoginMode.Interactive && DateTime.UtcNow < until) await Task.Delay(100);
            Assert.That(session.Mode, Is.EqualTo(WebLoginMode.Interactive), string.Join("\n", logs.Lines));
            await Task.Delay(1000);
            Assert.That(_loginCalls, Is.Zero, "Login must never be clicked with an unverified code");
            Assert.That(session.Completion.IsCompleted, Is.False);
            lock (logs.Lines) Assert.That(logs.Lines, Has.Some.Contain("not submitted, access code state has 3 characters"));
            AssertNoDigitsLogged(logs);
        }
    }

    [Test]
    public async Task The_bank_s_error_box_ends_the_login_as_rejected_credentials()
    {
        var (session, logs, browser) = await StartAsync("ok", "1111");
        await using (browser)
        await using (session)
        {
            var result = await session.Completion.WaitAsync(TimeSpan.FromSeconds(60));
            Assert.That(result.CredentialsRejected, Is.True, result.Error + "\n" + string.Join("\n", logs.Lines));
            Assert.That(_loginCalls, Is.EqualTo(1));
            lock (logs.Lines) Assert.That(logs.Lines, Has.Some.Contain("answered 401 (code=INVALID_CREDENTIALS)"));
            AssertNoDigitsLogged(logs);
        }
    }
}
