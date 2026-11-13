using Microsoft.EntityFrameworkCore;
using Xunit;

namespace OpenBudget.Tests.Assist;

/// <summary>Suggestions honour a category's "Restrict to": a restricted category is never suggested outside its scope.</summary>
public sealed partial class CategorySuggestionTests
{
    private static async Task RestrictAsync(TestDb t, long categoryId, long accountId)
    {
        await using var db = await t.System.CreateDbContextAsync();
        (await db.Categories.SingleAsync(c => c.Id == categoryId)).RestrictAccountId = accountId;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task A_category_restricted_to_another_account_is_not_suggested()
    {
        await using var t = await WorldAsync();
        await RestrictAsync(t, Groceries, AccessWorld.AccountPrivate);
        await RestrictAsync(t, Dining, AccessWorld.AccountPrivate);
        var s = For(t, AccessWorld.Alice);

        var result = await s.Service.SuggestAsync(AccessWorld.Shared, null, useAi: false);

        Assert.DoesNotContain(result.Suggestions, x => x.CategoryId == Groceries || x.CategoryId == Dining);
    }

    [Fact]
    public async Task A_category_restricted_to_the_bookings_account_is_still_suggested()
    {
        await using var t = await WorldAsync();
        await RestrictAsync(t, Groceries, AccessWorld.AccountWp);
        var s = For(t, AccessWorld.Alice);

        var result = await s.Service.SuggestAsync(AccessWorld.Shared, null, useAi: false);

        Assert.Equal(Groceries, result.Suggestions.Single(x => x.TransactionId == 40).CategoryId);
    }

    [Fact]
    public async Task Ai_answers_with_a_category_outside_its_scope_are_dropped()
    {
        await using var t = await WorldAsync();
        await OptInAsync(t, AccessWorld.Alice);
        await RestrictAsync(t, Dining, AccessWorld.AccountPrivate);
        var s = For(t, AccessWorld.Alice);

        var result = await s.Service.SuggestAsync(AccessWorld.Shared, null, useAi: true);

        Assert.NotEmpty(s.Ai.Requests);
        Assert.DoesNotContain(result.Suggestions, x => x.CategoryId == Dining);
    }
}
