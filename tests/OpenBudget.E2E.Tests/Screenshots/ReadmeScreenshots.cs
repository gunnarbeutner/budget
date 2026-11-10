using System.Diagnostics;
using Microsoft.Playwright;
using NUnit.Framework;

namespace OpenBudget.E2E.Tests.Screenshots;

/// <summary>
/// Regenerates the README screenshots in docs/screenshots/ from <see cref="DemoSeed"/>. Explicit, so normal test
/// runs skip it; run it with
/// <c>dotnet test tests/OpenBudget.E2E.Tests --filter Category=Screenshots</c>.
/// PNGs are run through oxipng when it is on PATH.
/// </summary>
[TestFixture, Explicit("Regenerates docs/screenshots; run on demand"), Category("Screenshots")]
public sealed class ReadmeScreenshots
{
    private static readonly string OutDir = Path.Combine(RepoRoot(), "docs", "screenshots");
    private IPlaywright _pw = null!;
    private IBrowser _browser = null!;
    private KestrelWebApplicationFactory _factory = null!;

    [OneTimeSetUp]
    public async Task Start()
    {
        _factory = AppFixture.Rent();
        await DemoSeed.SeedAsync(_factory.Services);
        Directory.CreateDirectory(OutDir);
        _pw = await Playwright.CreateAsync();
        _browser = await _pw.Chromium.LaunchAsync();
    }

    [OneTimeTearDown]
    public async Task Stop()
    {
        await _browser.DisposeAsync();
        _pw.Dispose();
    }

    private static string Browse => $"/projects/{DemoSeed.HouseholdId}/browse?year=2026&month=11";

    [Test]
    public async Task Capture()
    {
        foreach (var dark in new[] { false, true })
        {
            var suffix = dark ? "-dark" : "";
            var page = await NewPageAsync(1440, 900, dark);

            await Go(page, Browse);
            await page.GetByTestId("tx-row").Filter(new() { HasText = "Example Department Store" }).Filter(new() { HasText = "Groceries" }).ClickAsync();
            await page.WaitForTimeoutAsync(1000);
            // Expanding scrolls the row into view under the sticky selection bar; the row fits without scrolling.
            await page.EvaluateAsync("() => document.querySelectorAll('*').forEach(e => { if (e.scrollTop > 0) e.scrollTop = 0; })");
            await page.WaitForTimeoutAsync(300);
            await Shot(page, $"transactions{suffix}");

            await Go(page, $"/projects/{DemoSeed.HouseholdId}/budgets?year=2026&month=10");
            await Shot(page, $"budget{suffix}");

            await Go(page, $"/projects/{DemoSeed.HouseholdId}/portfolio");
            await Shot(page, $"portfolio{suffix}");

            await Go(page, $"/projects/{DemoSeed.HouseholdId}/documents");
            await page.Locator(".doc-thumb.loaded").First.WaitForAsync(new() { Timeout = 10000 });
            await page.WaitForTimeoutAsync(1000);
            await Shot(page, $"documents{suffix}");

            await page.Context.CloseAsync();
        }

        var phone = await NewPageAsync(390, 844, dark: false, mobile: true);
        await Go(phone, Browse);
        await Shot(phone, "transactions-phone");
        await phone.Context.CloseAsync();
    }

    private async Task<IPage> NewPageAsync(int width, int height, bool dark, bool mobile = false)
    {
        var context = await _browser.NewContextAsync(new()
        {
            BaseURL = _factory.BaseUrl,
            Locale = "en-US",
            TimezoneId = "Europe/Berlin",
            ViewportSize = new() { Width = width, Height = height },
            DeviceScaleFactor = 1,
            IsMobile = mobile,
            HasTouch = mobile,
            ColorScheme = dark ? ColorScheme.Dark : ColorScheme.Light,
        });
        var page = await context.NewPageAsync();
        await page.GotoAsync("/Account/Login");
        await page.GetByLabel("Username").FillAsync(DemoSeed.UserName);
        await page.GetByLabel("Password").FillAsync(DemoSeed.Password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in", Exact = true }).ClickAsync();
        await page.WaitForURLAsync(url => !url.Contains("/Account/Login", StringComparison.Ordinal));
        return page;
    }

    private static async Task Go(IPage page, string path)
    {
        await page.GotoAsync(path, new() { WaitUntil = WaitUntilState.NetworkIdle });
        await page.WaitForTimeoutAsync(800); // charts / Blazor render settle
    }

    private static async Task Shot(IPage page, string name)
    {
        // Hide the caret and any toast so repeated runs give the same picture.
        await page.AddStyleTagAsync(new() { Content = "*{caret-color:transparent!important} .toast,.blazor-error-ui{display:none!important}" });
        var file = Path.Combine(OutDir, name + ".png");
        await page.ScreenshotAsync(new() { Path = file });
        Optimise(file);
        Assert.That(new FileInfo(file).Length, Is.GreaterThan(0));
        await TestContext.Out.WriteLineAsync($"{file} ({new FileInfo(file).Length / 1024} KB)");
    }

    private static void Optimise(string file)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("oxipng", ["-o", "4", "--strip", "safe", "-q", file]) { UseShellExecute = false });
            p?.WaitForExit();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // oxipng not installed: keep Playwright's PNG.
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "budget.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("budget.sln not found above the test output");
    }
}
