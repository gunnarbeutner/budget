using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.E2E.Tests;

/// <summary>
/// A category the assistant applied after a sync: the row's pill carries a marker, the booking leaves Uncategorized,
/// and its details say where it came from and confirm it as the owner's own.
/// </summary>
[TestFixture]
public sealed class SuggestedCategoryTests : EndToEndTestBase
{
    private const long Booking = 140;

    [SetUp]
    public async Task SeedSuggestion()
    {
        var factory = Factory.Services.GetRequiredService<SystemDbContextFactory>();
        await EditingSeed.SeedAsync(factory);
        await using var db = await factory.CreateDbContextAsync();
        EditingSeed.Add(db, Booking, EditingSeed.BankAccountId, -18_40, "Example Bistro", new DateTime(2026, 3, 14, 0, 0, 0, DateTimeKind.Utc), category: 200);
        await db.SaveChangesAsync();
        await db.Transactions.Where(t => t.Id == Booking).ExecuteUpdateAsync(u => u.SetProperty(t => t.CategorySetBy, TransactionCategorySource.Suggested));
        db.CategorySuggestions.Add(new CategorySuggestion
        {
            TransactionId = Booking,
            CategoryId = 200,
            Confidence = 0.7,
            Source = "ai",
            Rationale = "Restaurant bill",
            TreeHash = "h",
            CreatedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    [Test]
    public async Task A_suggested_category_is_marked_left_out_of_uncategorized_and_confirmed_in_the_details()
    {
        await GoToAsync($"/projects/{E2ESeed.ProjectId}/browse?year=2026&month=3&sel=uncategorized");
        await Expect(Page.GetByTestId("tx-row").First).ToBeVisibleAsync();
        await Expect(Page.GetByTestId("tx-row").Filter(new() { HasText = "Example Bistro" })).ToHaveCountAsync(0);

        await GoToAsync($"/projects/{E2ESeed.ProjectId}/browse?year=2026&month=3");
        var row = Page.GetByTestId("tx-row").Filter(new() { HasText = "Example Bistro" });
        await Expect(row.GetByTestId("tx-category-suggested")).ToBeVisibleAsync();

        await row.ClickAsync();
        var details = Page.GetByTestId("tx-details-suggested");
        await Expect(details).ToContainTextAsync("the AI");
        await Expect(details).ToContainTextAsync("Restaurant bill");
        await details.GetByTestId("tx-details-confirm").ClickAsync();

        await Expect(row.GetByTestId("tx-category-suggested")).ToHaveCountAsync(0);
        await using var db = await Factory.Services.GetRequiredService<SystemDbContextFactory>().CreateDbContextAsync();
        var t = await db.Transactions.AsNoTracking().SingleAsync(x => x.Id == Booking);
        Assert.That((t.CategoryId, t.CategorySetBy), Is.EqualTo(((long?)200, TransactionCategorySource.Manual)));
    }
}
