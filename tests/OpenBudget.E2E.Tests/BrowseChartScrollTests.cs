using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using OpenBudget.Services;

namespace OpenBudget.E2E.Tests;

/// <summary>
/// The transactions chart over a long daily history: the drawing surface stays about as wide as the visible frame
/// while the scroll area spans every bar, and scrolling to the far left draws the oldest bars.
/// </summary>
[TestFixture]
public sealed class BrowseChartScrollTests : EndToEndTestBase
{
    // About eleven years of fictional daily balances, the selected period at the right end.
    private const string RenderLongHistory = """
        ({ n, base, noAnimate }) => {
            const starts = [], labels = [], values = [], anchors = [];
            const t0 = Date.UTC(2015, 7, 1);
            const p = x => String(x).padStart(2, '0');
            for (let i = 0; i < n; i++) {
                const dt = new Date(t0 + i * 86400000);
                const y = dt.getUTCFullYear(), m = dt.getUTCMonth() + 1, d = dt.getUTCDate();
                starts.push(y + '-' + p(m) + '-' + p(d));
                labels.push(p(d) + '.' + p(m));
                values.push(base + 1500 * Math.sin(i / 45) + (i % 30) * 20);
                anchors.push(i >= n - 30);
            }
            const data = { granularity: 'D', values, starts, labels, anchors, balance: true, today: starts[n - 1],
                monthNames: ['Jan','Feb','Mar','Apr','May','Jun','Jul','Aug','Sep','Oct','Nov','Dec'],
                invertY: false, budget: null, budgetLabel: 'Budget', noAnimate };
            return window.renderBrowseChart('browseChart', data, null);
        }
        """;

    [SetUp]
    public async Task SeedEditingWorld() =>
        await EditingSeed.SeedAsync(Factory.Services.GetRequiredService<SystemDbContextFactory>());

    private async Task<bool> RenderAsync(int n, double baseValue, bool noAnimate) =>
        await Page.EvaluateAsync<bool>(RenderLongHistory, new { n, @base = baseValue, noAnimate });

    private async Task OpenLongHistoryAsync()
    {
        await Page.SetViewportSizeAsync(1100, 900);
        await GoToAsync($"/projects/{E2ESeed.ProjectId}/browse?year=2026&month=3&sel=acct-{EditingSeed.BankAccountId}");
        await Page.WaitForFunctionAsync("() => !!Chart.getChart('browseChart')");
        Assert.That(await RenderAsync(4000, 2000, true), Is.True);
    }

    private Task<double> CanvasWidthAsync() =>
        Page.EvaluateAsync<double>("() => document.getElementById('browseChart').getBoundingClientRect().width");

    // Without any scrolling, the drawn window starts at the left edge of the frame and holds the
    // full series' values for its bars, also when the series is replaced right after the first render.
    [TestCase(4000, false)]
    [TestCase(380, false)]
    [TestCase(4000, true)]
    public async Task The_first_draw_shows_the_full_series_in_the_visible_window(int n, bool replaced)
    {
        await OpenLongHistoryAsync();
        if (replaced) Assert.That(await RenderAsync(n, 3000, false), Is.True);
        Assert.That(await RenderAsync(n, 1_500_000, false), Is.True);
        await Page.EvaluateAsync("() => new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r)))");
        await Page.WaitForTimeoutAsync(400);
        var problems = await Page.EvaluateAsync<string[]>("""
            () => {
                const c = Chart.getChart('browseChart'), full = browseChartData('browseChart');
                const meta = c.getDatasetMeta(0), area = c.chartArea, y = c.scales.y, out = [];
                const r = c.canvas.getBoundingClientRect(), s = document.getElementById('browseChartScroll').getBoundingClientRect();
                const data = c.data.datasets[0].data;
                if (data.length !== c.data.labels.length) out.push('labels/data length');
                for (let k = 0; k < data.length; k++) {
                    if (data[k] !== full.values[full.first + k]) { out.push('value ' + k); break; }
                }
                let leftBars = 0;
                for (const bar of meta.data) {
                    const x = r.left + bar.x;
                    if (x >= s.left && x <= s.left + s.width / 2) {
                        leftBars++;
                        if (Math.abs(bar.y - y.getPixelForValue(full.values[full.first + meta.data.indexOf(bar)])) > 1) { out.push('height at ' + bar.x); break; }
                    }
                }
                if (leftBars < 20) out.push('left bars ' + leftBars);
                return out;
            }
            """);
        Assert.That(problems, Is.Empty);
    }

    [Test]
    public async Task A_long_daily_history_draws_only_the_visible_window()
    {
        await OpenLongHistoryAsync();
        var scroll = Page.GetByTestId("browse-chart-scroll");
        var geo = await scroll.EvaluateAsync<double[]>("el => [el.scrollWidth, el.clientWidth, el.scrollLeft]");
        Assert.That(geo[0], Is.GreaterThanOrEqualTo(4000 * 5));
        Assert.That(geo[2] + geo[1], Is.EqualTo(geo[0]).Within(2));   // opens on the selected period at the right end
        Assert.That(await CanvasWidthAsync(), Is.LessThan(geo[1] + 100));
        Assert.That(await Page.EvaluateAsync<int>("() => browseChartData('browseChart').first + Chart.getChart('browseChart').data.labels.length"), Is.EqualTo(4000));

        await scroll.EvaluateAsync("el => el.scrollLeft = 0");
        await Page.WaitForFunctionAsync("() => browseChartData('browseChart').first === 0");
        Assert.That(await CanvasWidthAsync(), Is.LessThan(geo[1] + 100));
        // The oldest bar is drawn at the left edge of the visible frame.
        var drawn = await Page.EvaluateAsync<bool>("""
            () => {
                const c = Chart.getChart('browseChart'), bar = c.getDatasetMeta(0).data[0];
                const r = c.canvas.getBoundingClientRect(), s = document.getElementById('browseChartScroll').getBoundingClientRect();
                return bar.x > 0 && r.left + bar.x >= s.left && r.left + bar.x <= s.left + 20;
            }
            """);
        Assert.That(drawn, Is.True);
    }

    [TestCase(false), TestCase(true), Explicit("Scroll smoothness measurement; prints frame and long-task numbers")]
    public async Task Measure_scrolling(bool pointerOver)
    {
        await Page.AddInitScriptAsync("Object.defineProperty(window, 'devicePixelRatio', { get: () => 2 })");
        await OpenLongHistoryAsync();
        var r = await Page.EvaluateAsync<double[]>("""
            async (pointerOver) => {
                const el = document.getElementById('browseChartScroll');
                const longs = [];
                const po = new PerformanceObserver(l => { for (const e of l.getEntries()) longs.push(e.duration); });
                po.observe({ type: 'longtask', buffered: false });
                const deltas = [];
                const max = el.scrollWidth - el.clientWidth;
                let last = performance.now();
                for (let f = 0; f < 300; f++) {
                    el.scrollLeft = max - (max * f / 299);
                    if (pointerOver) {
                        const b = el.getBoundingClientRect(), cv = document.getElementById('browseChart');
                        cv.dispatchEvent(new MouseEvent('mousemove', { clientX: b.left + b.width / 2 + (f % 7), clientY: b.top + b.height / 2, bubbles: true }));
                    }
                    await new Promise(res => requestAnimationFrame(res));
                    const now = performance.now(); deltas.push(now - last); last = now;
                }
                await new Promise(res => setTimeout(res, 200));
                po.disconnect();
                deltas.sort((a, b) => a - b);
                const sum = deltas.reduce((a, b) => a + b, 0);
                return [sum / deltas.length, deltas[Math.floor(deltas.length * 0.95)], deltas[deltas.length - 1],
                    deltas.filter(x => x > 20).length, longs.length, longs.reduce((a, b) => a + b, 0),
                    document.getElementById('browseChart').width];
            }
            """, pointerOver);
        await TestContext.Out.WriteLineAsync($"MEASURE pointerOver={pointerOver} mean={r[0]:F1}ms p95={r[1]:F1}ms max={r[2]:F1}ms frames>20ms={r[3]} longtasks={r[4]} longtaskTotal={r[5]:F0}ms canvasBackingWidth={r[6]}");
    }
}
