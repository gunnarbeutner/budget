using System.Net;
using System.Text;
using NUnit.Framework;
using OpenBudget.Connectors.FakeBank;

namespace OpenBudget.E2E.Tests;

/// <summary>
/// The whole WebLogin path in the real app: the fake bank asks for a web login, the dialog streams a real Chromium
/// (WebSocket, canvas), the test clicks on the streamed page like a user, the login completes and the sync goes on.
/// The "bank" page is a local one; nothing leaves the machine. Skipped without a Chromium.
/// </summary>
#pragma warning disable CA1001 // the listener is closed in TearDown
[TestFixture]
[NonParallelizable] // points the fake bank at its login page through process environment variables
public sealed class WebLoginDialogTests : EndToEndTestBase
{
    private HttpListener? _listener;
    private Task _serving = Task.CompletedTask;

    [TearDown]
    public async Task StopServer()
    {
        Environment.SetEnvironmentVariable(FakeBankConnector.WebLoginUrlVariable, null);
        Environment.SetEnvironmentVariable(FakeBankConnector.WebLoginSuccessVariable, null);
        if (_listener == null) return;
        _listener.Close();
        try { await _serving; } catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { /* closed */ }
        _listener = null;
    }

    private async Task ServeAsync(HttpListener listener)
    {
        while (listener.IsListening)
        {
            var ctx = await listener.GetContextAsync();
            // The login page is one big link: any click on the streamed page "signs in".
            var html = ctx.Request.Url!.AbsolutePath == "/login"
                ? "<html><body style='margin:0'><a href='/home' style='display:block;position:fixed;inset:0;background:#cde;font:40px sans-serif'>Click to sign in</a></body></html>"
                : "<html><body><h1>Signed in</h1></body></html>";
            if (ctx.Request.Url.AbsolutePath == "/home") ctx.Response.Headers.Add("Set-Cookie", "bank_sid=abc; Path=/; Max-Age=3600; HttpOnly");
            var bytes = Encoding.UTF8.GetBytes(html);
            ctx.Response.ContentType = "text/html";
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
    }

    [Test]
    public async Task The_user_signs_in_on_the_streamed_page_and_the_sync_continues()
    {
        var chrome = ChromiumLocator.Find();
        if (chrome == null) Assert.Ignore("No Chromium found (set CHROME_PATH or run `playwright install chromium`).");
        Environment.SetEnvironmentVariable("CHROME_PATH", chrome);

        (_listener, var baseUrl) = LocalHttpListener.Start();
        _serving = Task.Run(() => ServeAsync(_listener));
        Environment.SetEnvironmentVariable(FakeBankConnector.WebLoginUrlVariable, baseUrl + "login");
        Environment.SetEnvironmentVariable(FakeBankConnector.WebLoginSuccessVariable, baseUrl + "home");

        await GoToAsync("/connections");
        await Page.GetByTestId("add-login-open").ClickAsync();
        // The real banks are offered too; pick the fake one explicitly.
        await Page.Locator("#new-login-connector").SelectOptionAsync("fake");
        await Page.Locator("#new-login-name").FillAsync("Web fake bank");
        await Page.Locator("#new-login-username").FillAsync("demo+web");
        await Page.Locator("#new-login-pin").FillAsync("1234");
        await Page.Locator("#new-login-add").ClickAsync();

        // The dialog shows the remote page; the first frame replaces the spinner.
        var challenge = Page.GetByTestId("bank-challenge");
        await Expect(challenge).ToHaveAttributeAsync("data-kind", "WebLogin", new() { Timeout = 60000 });
        var canvas = Page.Locator("[data-testid=remote-browser] canvas");
        await Expect(canvas).ToBeVisibleAsync(new() { Timeout = 60000 });

        // Click on the streamed page (the whole page is the sign-in link). Retry once the page has surely loaded.
        await Task.Delay(1500);
        await canvas.ClickAsync();

        await Expect(Page.GetByTestId("bank-challenge")).ToHaveCountAsync(0, new() { Timeout = 60000 });
        var card = Page.Locator("[data-bank-login='Web fake bank']");
        await Expect(card.Locator("[data-bank-account='FAKE-GIRO']")).ToBeVisibleAsync(new() { Timeout = 30000 });
        await Expect(card.GetByTestId("login-error")).ToHaveCountAsync(0);
    }
}
