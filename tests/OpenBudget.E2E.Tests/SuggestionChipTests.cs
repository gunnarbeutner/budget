using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using NUnit.Framework;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.E2E.Tests;

/// <summary>
/// Category suggestions on uncategorized rows: confident ones in the row's main column with the category name and
/// confidence, accept and dismiss; weak ones only as the preselected "Suggested" entry of the category picker; split
/// suggestions with their parts. Earlier "Example Grocer" bookings filed under Groceries make the grocer rows suggest it.
/// </summary>
[TestFixture]
public sealed class SuggestionChipTests : EndToEndTestBase
{
    private static string UncategorizedUrl => $"/projects/{E2ESeed.ProjectId}/browse?year=2026&month=3&sel=uncategorized";

    [SetUp]
    public async Task SeedHistory()
    {
        var factory = Factory.Services.GetRequiredService<SystemDbContextFactory>();
        await EditingSeed.SeedAsync(factory);
        await using var db = await factory.CreateDbContextAsync();
        for (var i = 0; i < 4; i++)
            EditingSeed.Add(db, 120 + i, EditingSeed.BankAccountId, -20_00 - i * 100, "Example Grocer",
                new DateTime(2026, 2, 3 + i * 5, 0, 0, 0, DateTimeKind.Utc), category: 200);
        EditingSeed.Add(db, 130, EditingSeed.BankAccountId, -16_47, "Example Shop", new DateTime(2026, 3, 13, 0, 0, 0, DateTimeKind.Utc));
        await db.SaveChangesAsync();
    }

    private ILocator Row(string amount) => Page.GetByTestId("tx-row").Filter(new() { HasText = amount });

    /// <summary>Runs the suggestions, then rewrites the cached ones: −12,50 weak, the shop a split with an unclear shipment.</summary>
    private async Task SuggestAsync()
    {
        await GoToAsync(UncategorizedUrl);
        await Page.GetByTestId("suggest-run").ClickAsync();
        await Expect(Row("−25,00").GetByTestId("suggest-chip")).ToBeVisibleAsync();

        var factory = Factory.Services.GetRequiredService<SystemDbContextFactory>();
        await using (var db = await factory.CreateDbContextAsync())
        {
            var hash = (await db.CategorySuggestions.FirstAsync()).TreeHash;
            await db.CategorySuggestions.Where(s => s.TransactionId == 103).ExecuteUpdateAsync(u => u.SetProperty(s => s.Confidence, 0.3));
            await db.CategorySuggestions.Where(s => s.TransactionId == 130).ExecuteDeleteAsync();
            db.CategorySuggestions.Add(new CategorySuggestion
            {
                TransactionId = 130,
                CategoryId = 200,
                Confidence = 0.9,
                Source = "order",
                Rationale = "2 items of order 302-1",
                SplitJson = """{"Parts":[{"AmountCents":-1247,"CategoryId":200,"CategoryPath":null,"Label":"Bread","ItemIds":[]},{"AmountCents":-400,"CategoryId":202,"CategoryPath":null,"Label":"Lamp","ItemIds":[]}],"Unclear":true}""",
                TreeHash = hash,
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        await GoToAsync(UncategorizedUrl);
        await Page.GetByTestId("suggest-run").ClickAsync();
        await Expect(Row("−16,47").GetByTestId("suggest-chip")).ToBeVisibleAsync();
    }

    [Test]
    public async Task Confident_suggestions_show_in_the_row_and_weak_ones_in_the_picker()
    {
        await SuggestAsync();

        var chip = Row("−25,00").GetByTestId("suggest-chip");
        await Expect(chip.GetByTestId("suggest-chip-name")).ToHaveTextAsync("Groceries");
        await Expect(chip.GetByTestId("suggest-chip-conf")).ToContainTextAsync("%");
        await Expect(chip).ToHaveAttributeAsync("title", new System.Text.RegularExpressions.Regex("Living › Groceries\\n.*similar bookings"));
        // The source is reachable without hover too.
        await chip.GetByTestId("suggest-chip-why").ClickAsync();
        await Expect(chip.GetByTestId("suggest-chip-why-text")).ToHaveTextAsync("From 4 similar bookings");
        // In the main column, not under the date.
        await Expect(Page.Locator(".tx-date-col [data-testid=suggest-chip]")).ToHaveCountAsync(0);

        var split = Row("−16,47").GetByTestId("suggest-chip-split");
        await Expect(split).ToContainTextAsync("Groceries 12,47 €");
        await Expect(split).ToContainTextAsync("Energy 4,00 €");
        await Expect(Row("−16,47").GetByTestId("suggest-chip-unclear")).ToBeVisibleAsync();

        // The weak one stays out of the row; the picker offers it first and preselected.
        await Expect(Row("−12,50").GetByTestId("suggest-chip")).ToHaveCountAsync(0);
        await Row("−12,50").GetByTestId("tx-category").ClickAsync();
        var picker = Page.GetByTestId("category-picker");
        await Expect(picker.GetByTestId("category-picker-suggested")).ToContainTextAsync("Groceries");
        await Expect(picker.GetByTestId("category-picker-suggested")).ToContainTextAsync("30 %");
        await Expect(picker.Locator(".cp-active")).ToHaveAttributeAsync("data-testid", "category-picker-suggested");
        await Page.Keyboard.PressAsync("Enter");
        await Expect(Row("−12,50")).ToHaveCountAsync(0);

        await chip.GetByTestId("suggest-chip-reject").ClickAsync();
        await Expect(Row("−25,00").GetByTestId("suggest-chip")).ToHaveCountAsync(0);
        await Row("−30,00").GetByTestId("suggest-chip-accept").ClickAsync();
        await Expect(Row("−30,00")).ToHaveCountAsync(0);
    }

    /// <summary>Screenshots of the rows at desktop and phone width, light and dark, into $OB_SHOT_DIR.</summary>
    [Test, Explicit("Visual check; run on demand")]
    public async Task Screenshots()
    {
        var dir = Environment.GetEnvironmentVariable("OB_SHOT_DIR") ?? Path.GetTempPath();
        await SuggestAsync();
        foreach (var width in new[] { 1440, 390 })
            foreach (var dark in new[] { false, true })
            {
                await Page.SetViewportSizeAsync(width, width > 500 ? 900 : 844);
                await Page.EmulateMediaAsync(new() { ColorScheme = dark ? ColorScheme.Dark : ColorScheme.Light });
                await Page.WaitForTimeoutAsync(300);
                await Page.ScreenshotAsync(new() { Path = Path.Combine(dir, $"suggest-{width}-{(dark ? "dark" : "light")}.png") });
            }
        await Page.SetViewportSizeAsync(1440, 900);
        await Row("−12,50").GetByTestId("tx-category").ClickAsync();
        await Page.ScreenshotAsync(new() { Path = Path.Combine(dir, "suggest-picker.png") });
    }
}
