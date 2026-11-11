using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using NUnit.Framework;
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
        await editor.GetByTestId("split-category").Nth(0).SelectOptionAsync("202");
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
        await Expect(Page.GetByTestId("tx-details-split-part")).ToBeVisibleAsync();
        await Page.GetByTestId("tx-details-split").ClickAsync();
        await Page.GetByTestId("split-remove").ClickAsync();
        await Expect(Row("−25,00")).ToBeVisibleAsync();
        await Expect(Row("−15,00")).ToHaveCountAsync(0);
        rows = await RowsAsync();
        Assert.That(rows.Any(t => t.SplitParentId == 101), Is.False);
        Assert.That(rows.Single(t => t.Id == 101).Splitted, Is.EqualTo(SplitState.Normal));
    }

    [Test]
    public async Task Moneymoney_rows_offer_no_split()
    {
        await GoToAsync($"/projects/{E2ESeed.ProjectId}/browse?year=2026&month=3&sel=acct-{EditingSeed.MoneyMoneyAccountId}");
        await Row("Example Bakery").ClickAsync();
        await Expect(Page.GetByTestId("tx-details")).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("tx-details-split")).ToHaveCountAsync(0);
    }
}
