using System.Collections;
using System.Reflection;
using Microsoft.Playwright;
using NUnit.Framework;

namespace OpenBudget.E2E.Tests;

/// <summary>
/// A page whose circuit the server no longer knows (as after a restart of the server) reloads by itself, in Chromium
/// and WebKit, also while Get started holds the user. The test closes the page's websocket and makes the server forget
/// the circuit, so the page's reconnect attempt is rejected by the server.
/// </summary>
[TestFixture]
public sealed class ReconnectTests
{
    private IPlaywright _pw = null!;
    private KestrelWebApplicationFactory _factory = null!;

    [OneTimeSetUp]
    public async Task Start()
    {
        _factory = AppFixture.Rent();
        _pw = await Playwright.CreateAsync();
    }

    [OneTimeTearDown]
    public void Stop()
    {
        _pw.Dispose();
        AppFixture.Return(_factory);
    }

    [TestCase("chromium", "/")]
    [TestCase("webkit", "/")]
    [TestCase("chromium", "/get-started")]
    [TestCase("webkit", "/get-started")]
    public async Task A_page_whose_circuit_the_server_forgot_reloads(string engine, string path)
    {
        await _factory.ResetAndSeedAsync();
        if (path == "/get-started") await ReopenGetStartedAsync();
        IBrowser browser;
        try
        {
            browser = await (engine == "webkit" ? _pw.Webkit : _pw.Chromium).LaunchAsync();
        }
        catch (PlaywrightException ex)
        {
            Assert.Ignore($"{engine} not installed: {ex.Message}");
            return;
        }
        await using var _ = browser;
        var context = await browser.NewContextAsync(new() { BaseURL = _factory.BaseUrl, Locale = "en-US" });
        var page = await context.NewPageAsync();

        // Keep the page's websockets reachable, so the test can drop the Blazor connection.
        await page.AddInitScriptAsync(@"(() => {
            const Native = window.WebSocket;
            window.__sockets = [];
            window.WebSocket = function (...args) { const ws = new Native(...args); window.__sockets.push(ws); return ws; };
            window.WebSocket.prototype = Native.prototype;
            Object.assign(window.WebSocket, { CONNECTING: 0, OPEN: 1, CLOSING: 2, CLOSED: 3 });
        })()");

        await page.GotoAsync("/Account/Login");
        await page.GetByLabel("Username").FillAsync(E2ESeed.UserName);
        await page.GetByLabel("Password").FillAsync(E2ESeed.Password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();
        await page.WaitForURLAsync(url => !url.Contains("/Account/Login", StringComparison.Ordinal));
        await page.GotoAsync(path);
        await page.WaitForFunctionAsync("() => window.Blazor && document.querySelector('[data-testid=user-menu]')");
        await page.WaitForTimeoutAsync(500);
        await page.EvaluateAsync("() => { window.__beforeReload = true; }");

        // The server forgets the circuit first, then the connection drops: the reconnect is rejected.
        ForgetDisconnectedCircuits();
        var closed = await page.EvaluateAsync<int>("() => { const open = window.__sockets.filter(w => w.readyState === 1 && w.url.includes('/_blazor')); open.forEach(w => w.close()); return open.length; }");
        Assert.That(closed, Is.GreaterThan(0), "no Blazor websocket seen");

        // The reload clears the marker.
        await page.WaitForFunctionAsync("() => window.__beforeReload !== true", null, new() { Timeout = 30_000 });
        await page.WaitForFunctionAsync("() => document.querySelector('[data-testid=user-menu]')", null, new() { Timeout = 15_000 });
        await context.CloseAsync();
    }

    private async Task ReopenGetStartedAsync()
    {
        var db = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
            .GetRequiredService<OpenBudget.Services.SystemDbContextFactory>(_factory.Services);
        await using var ctx = await db.CreateDbContextAsync();
        foreach (var s in ctx.UserDataStates) s.GetStartedCompletedAt = null;
        await ctx.SaveChangesAsync();
    }

    /// <summary>Forgets every circuit, as a restarted server would not have them.</summary>
    private void ForgetDisconnectedCircuits()
    {
        var type = Type.GetType("Microsoft.AspNetCore.Components.Server.Circuits.CircuitRegistry, Microsoft.AspNetCore.Components.Server", throwOnError: true)!;
        var registry = _factory.BrowserHostServices.GetService(type) ?? throw new InvalidOperationException("No circuit registry.");
        var flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var cache = type.GetProperty("DisconnectedCircuits", flags)?.GetValue(registry) ?? type.GetField("DisconnectedCircuits", flags)?.GetValue(registry);
        if (cache is Microsoft.Extensions.Caching.Memory.MemoryCache memory) memory.Compact(1.0);
        else throw new InvalidOperationException("Unexpected circuit registry shape: " + cache?.GetType());
        var connected = type.GetProperty("ConnectedCircuits", flags)?.GetValue(registry) ?? type.GetField("ConnectedCircuits", flags)?.GetValue(registry);
        if (connected is IDictionary dict) dict.Clear();
    }
}
