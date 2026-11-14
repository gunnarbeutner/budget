using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using OpenBudget.Services;

namespace OpenBudget.E2E.Tests;

/// <summary>
/// Scrolling the virtualized ledger list: arrow keys move the cursor row without a focus ring around the list, and
/// scrolling up past an expanded row keeps the visible rows in place.
/// </summary>
[TestFixture]
public sealed class BrowseListScrollTests : EndToEndTestBase
{
    [SetUp]
    public async Task SeedEditingWorld() =>
        await EditingSeed.SeedAsync(Factory.Services.GetRequiredService<SystemDbContextFactory>());

    // Two years of fictional daily bookings (one to three per day, so rows with and without a day header alternate).
    private async Task SeedHistoryAsync()
    {
        await using var db = await Factory.Services.GetRequiredService<SystemDbContextFactory>().CreateDbContextAsync();
        var id = 10_000L;
        for (var day = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc); day < new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc); day = day.AddDays(1))
        {
            var count = 1 + (int)(id % 3);
            for (var k = 0; k < count; k++)
            {
                EditingSeed.Add(db, id, EditingSeed.BankAccountId, -(100 + id % 900), $"Example Shop {id % 7}", day.AddHours(10 + k));
                id++;
            }
        }
        await db.SaveChangesAsync();
    }

    private async Task OpenAsync()
    {
        await SeedHistoryAsync();
        await Page.SetViewportSizeAsync(1300, 900);
        await GoToAsync($"/projects/{E2ESeed.ProjectId}/browse?year=2026&month=2&sel=acct-{EditingSeed.BankAccountId}");
        await Expect(Page.GetByTestId("tx-item").First).ToBeVisibleAsync();
    }

    [Test]
    public async Task Arrow_keys_move_the_cursor_without_a_focus_ring_around_the_list()
    {
        await OpenAsync();
        await Page.GetByTestId("tx-item").Nth(2).Locator(".tx-amount").ClickAsync();
        for (var i = 0; i < 3; i++) await Page.Keyboard.PressAsync("ArrowDown");
        await Expect(Page.Locator(".tx-row.tx-cursor")).ToHaveCountAsync(1);
        var state = await Page.EvaluateAsync<string[]>("""
            () => {
                const s = document.querySelector('.tx-scroll');
                return [String(document.activeElement === s), getComputedStyle(s).outlineStyle];
            }
            """);
        Assert.That(state, Is.EqualTo(new[] { "true", "none" }));
    }

    [Test]
    public async Task Scrolling_up_past_an_expanded_row_keeps_the_visible_rows_in_place()
    {
        await OpenAsync();
        var box = (await Page.Locator(".tx-scroll").BoundingBoxAsync())!;
        await Page.Mouse.MoveAsync(box.X + 300, box.Y + 300);
        for (var i = 0; i < 30; i++) { await Page.Mouse.WheelAsync(0, 400); await Page.WaitForTimeoutAsync(50); }
        await Page.WaitForTimeoutAsync(300);
        await Page.Mouse.ClickAsync(box.X + 400, box.Y + 200);
        await Expect(Page.Locator(".tx-item.expanded")).ToHaveCountAsync(1);
        // Move well below the expanded row so it leaves the rendered window, then wheel back up past it.
        for (var i = 0; i < 20; i++) { await Page.Mouse.WheelAsync(0, 400); await Page.WaitForTimeoutAsync(50); }
        await Page.WaitForTimeoutAsync(300);

        var jumps = new List<string>();
        for (var i = 0; i < 70; i++)
        {
            var before = await RowTopsAsync();
            await Page.Mouse.WheelAsync(0, -150);
            await Page.WaitForTimeoutAsync(120);
            var after = await RowTopsAsync();
            var shared = before.Keys.Where(after.ContainsKey).DefaultIfEmpty(-1).Min();
            if (shared < 0) continue;
            var moved = after[shared] - before[shared];
            if (Math.Abs(moved - 150) > 2) jumps.Add($"row {shared} moved {moved}");
        }
        Assert.That(jumps, Is.Empty);
    }

    private async Task<Dictionary<int, double>> RowTopsAsync() =>
        (await Page.EvaluateAsync<double[][]>(RowTops)).ToDictionary(r => (int)r[0], r => r[1]);

    // Top of each rendered row that overlaps the scroll frame, relative to the frame, keyed by list index.
    private const string RowTops = """
        () => {
            const s = document.querySelector('.tx-scroll').getBoundingClientRect(), out = [];
            for (const r of document.querySelectorAll('[data-testid="tx-item"]')) {
                const b = r.getBoundingClientRect();
                if (b.bottom > s.top && b.top < s.bottom) out.push([Number(r.dataset.txIndex), b.top - s.top]);
            }
            return out;
        }
        """;
}
