using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using NUnit.Framework;
using OpenBudget.Connectors;
using OpenBudget.Connectors.Sync;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.E2E.Tests;

/// <summary>
/// Splitting a booking into parts in its details (with "Assign rest") and removing the split.
/// </summary>
[TestFixture]
public sealed class SplitTests : EndToEndTestBase
{
    private static string GiroUrl => $"/projects/{E2ESeed.ProjectId}/browse?year=2026&month=3&sel=acct-{EditingSeed.BankAccountId}";

    [SetUp]
    public async Task SeedWorld()
    {
        var factory = Factory.Services.GetRequiredService<SystemDbContextFactory>();
        await EditingSeed.SeedAsync(factory);
    }

    private ILocator Row(string text) => Page.GetByTestId("tx-row").Filter(new() { HasText = text });

    private async Task<List<Transaction>> RowsAsync()
    {
        var factory = Factory.Services.GetRequiredService<SystemDbContextFactory>();
        await using var db = await factory.CreateDbContextAsync();
        return await db.Transactions.AsNoTracking().ToListAsync();
    }

    /// <summary>Opens a part's category picker and picks the category of that name.</summary>
    private async Task PickCategoryAsync(ILocator button, string name)
    {
        await button.ClickAsync();
        var picker = Page.GetByTestId("category-picker");
        await picker.GetByTestId("category-picker-option").Filter(new() { HasText = name }).First.ClickAsync();
        await Expect(picker).ToHaveCountAsync(0);
        await Expect(button).ToContainTextAsync(name);
        await Expect(button).ToBeFocusedAsync();
    }

    /// <summary>
    /// A part's category is a pill on a button that opens the category picker below it; the keyboard opens it too, Escape
    /// closes it and the focus goes back to the button. On a phone the button stays inside the editor.
    /// </summary>
    [TestCase(1280)]
    [TestCase(390)]
    public async Task A_parts_category_opens_the_category_picker(int width)
    {
        await Page.SetViewportSizeAsync(width, 844);
        await GoToAsync(GiroUrl);
        await Row("−25,00").ClickAsync();
        await Page.GetByTestId("tx-details-split").ClickAsync();
        var editor = Page.GetByTestId("split-editor");
        var empty = editor.GetByTestId("split-category").Nth(1);
        await Expect(empty).ToHaveTextAsync("No category");

        await empty.FocusAsync();
        await Page.Keyboard.PressAsync("Enter");
        var picker = Page.GetByTestId("category-picker");
        await Expect(picker).ToBeVisibleAsync();
        var button = (await empty.BoundingBoxAsync())!;
        var panel = (await picker.BoundingBoxAsync())!;
        Assert.That(panel.Y, Is.GreaterThanOrEqualTo(button.Y + button.Height).Or.LessThanOrEqualTo(button.Y), "the picker opens beside the button, not over it");
        await Page.Keyboard.PressAsync("Escape");
        await Expect(picker).ToHaveCountAsync(0);
        await Expect(empty).ToBeFocusedAsync();

        await PickCategoryAsync(empty, EditingSeed.EnergyCategory);
        await Expect(empty.Locator(".cat-pill")).ToBeVisibleAsync();
        var box = (await editor.BoundingBoxAsync())!;
        foreach (var b in await editor.GetByTestId("split-category").AllAsync())
        {
            var r = (await b.BoundingBoxAsync())!;
            Assert.That(r.X + r.Width, Is.LessThanOrEqualTo(box.X + box.Width + 1), "the category button stays inside the editor");
        }
        var dir = Environment.GetEnvironmentVariable("OB_SPLIT_SCREENSHOT_DIR");
        if (!string.IsNullOrEmpty(dir))
            await editor.ScreenshotAsync(new() { Path = Path.Combine(dir, $"split-{width}.png") });
    }

    [Test]
    public async Task A_booking_is_split_into_parts_with_assign_rest_and_the_split_can_be_removed()
    {
        await GoToAsync(GiroUrl);
        await Row("−25,00").ClickAsync();
        await Page.GetByTestId("tx-details-split").ClickAsync();

        var editor = Page.GetByTestId("split-editor");
        await Expect(editor).ToBeVisibleAsync();
        var amounts = editor.GetByTestId("split-amount");
        await Expect(amounts).ToHaveCountAsync(2);
        await amounts.Nth(0).FillAsync("15");
        await Expect(editor.GetByTestId("split-remaining")).ToContainTextAsync("10,00");
        await Expect(editor.GetByTestId("split-save")).ToBeDisabledAsync();
        await PickCategoryAsync(editor.GetByTestId("split-category").Nth(0), EditingSeed.EnergyCategory);
        await editor.GetByTestId("split-assign-rest").Nth(1).ClickAsync();
        await Expect(amounts.Nth(1)).ToHaveValueAsync("10,00");
        await Expect(editor.GetByTestId("split-remaining")).ToContainTextAsync("0,00");
        await editor.GetByTestId("split-note").Nth(1).FillAsync("Example part note");
        await editor.GetByTestId("split-save").ClickAsync();

        await Expect(editor).ToHaveCountAsync(0);
        await Expect(Row("−15,00")).ToContainTextAsync("Split");
        await Expect(Row("−10,00")).ToContainTextAsync("Example part note");
        var rows = await RowsAsync();
        Assert.That(rows.Single(t => t.Id == 101).Splitted, Is.EqualTo(SplitState.Parent));
        var parts = rows.Where(t => t.SplitParentId == 101).OrderBy(t => t.Id).ToList();
        Assert.That(parts.Select(p => p.Amount), Is.EqualTo(new long[] { -15_00, -10_00 }));
        Assert.That(parts[0].CategoryId, Is.EqualTo(202));

        // A part's details offer editing the split, which can also remove it.
        await Row("−15,00").ClickAsync();
        await Page.GetByTestId("tx-details-split").ClickAsync();
        await Page.GetByTestId("split-remove").ClickAsync();
        await Expect(Row("−25,00")).ToBeVisibleAsync();
        await Expect(Row("−15,00")).ToHaveCountAsync(0);
        rows = await RowsAsync();
        Assert.That(rows.Any(t => t.SplitParentId == 101), Is.False);
        Assert.That(rows.Single(t => t.Id == 101).Splitted, Is.EqualTo(SplitState.Normal));
    }

    [Test]
    public async Task A_split_pending_booking_keeps_its_split_when_booked_at_another_amount()
    {
        var factory = Factory.Services.GetRequiredService<SystemDbContextFactory>();
        var day = new DateOnly(2026, 3, 20);
        async Task SyncAsync(RemoteTransaction row)
        {
            await using var db = await factory.CreateDbContextAsync();
            await new BankTransactionStore().ApplyAsync(db, EditingSeed.BankAccountId, new AccountRefresh(null, [row], []), day, DateTime.UtcNow, CancellationToken.None);
        }
        await SyncAsync(new(-40m, "EUR", day, null, "Example Hardware", "Card payment", false));

        await GoToAsync(GiroUrl);
        await Row("−40,00").ClickAsync();
        await Page.GetByTestId("tx-details-split").ClickAsync();
        var editor = Page.GetByTestId("split-editor");
        await editor.GetByTestId("split-amount").Nth(0).FillAsync("32");
        await PickCategoryAsync(editor.GetByTestId("split-category").Nth(0), EditingSeed.EnergyCategory);
        await editor.GetByTestId("split-assign-rest").Nth(1).ClickAsync();
        await editor.GetByTestId("split-save").ClickAsync();
        await Expect(Row("−32,00")).ToContainTextAsync("Split");

        // The bank books it a day later at 44.00.
        await SyncAsync(new(-44m, "EUR", day.AddDays(1), null, "Example Hardware", "Card payment", true, BankTransactionId: "E2E-1"));
        await GoToAsync(GiroUrl);
        await Expect(Row("−35,20")).ToContainTextAsync("Split changed");
        await Expect(Row("−8,80")).ToBeVisibleAsync();
        var rows = await RowsAsync();
        var parent = rows.Single(t => t.ExternalId == "id:E2E-1");
        Assert.That(parent.SplitCheck, Is.True);
        var parts = rows.Where(t => t.SplitParentId == parent.Id).OrderBy(t => t.Id).ToList();
        Assert.That(parts.Select(p => p.Amount), Is.EqualTo(new long[] { -35_20, -8_80 }));
        Assert.That(parts[0].CategoryId, Is.EqualTo(202));
    }

    [Test]
    public async Task Rows_of_an_imported_account_offer_a_split()
    {
        await GoToAsync($"/projects/{E2ESeed.ProjectId}/browse?year=2026&month=3&sel=acct-{EditingSeed.ImportedAccountId}");
        await Row("Example Bakery").ClickAsync();
        await Expect(Page.GetByTestId("tx-details")).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("tx-details-split")).ToBeVisibleAsync();
    }
}
