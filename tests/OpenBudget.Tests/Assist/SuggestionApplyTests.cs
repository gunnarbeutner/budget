#pragma warning disable CA2000 // test objects live for the test
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenBudget.Assist;
using OpenBudget.Models;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests.Assist;

/// <summary>Suggestions applied after a sync: marked as suggested, only with consent, replaced by rules, confirmed by hand.</summary>
public sealed partial class CategorySuggestionTests
{
    private static async Task<Dictionary<long, (long? Category, string SetBy)>> CategoriesAsync(TestDb t, params long[] ids)
    {
        await using var db = await t.System.CreateDbContextAsync();
        return await db.Transactions.AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => (x.CategoryId, x.CategorySetBy));
    }

    [Fact]
    public async Task With_consent_suggestions_are_applied_marked_replaced_by_a_rule_and_confirmed_by_hand()
    {
        await using var t = await WorldAsync();
        await OptInAsync(t, AccessWorld.Alice);
        var s = For(t, AccessWorld.Alice);

        Assert.Equal(5, await s.Service.ApplySuggestionsAsync(AccessWorld.Shared));

        var rows = await CategoriesAsync(t, 40, 41, 42, 43, 44);
        Assert.All(new long[] { 40, 41, 42 }, id => Assert.Equal((Groceries, TransactionCategorySource.Suggested), (rows[id].Category!.Value, rows[id].SetBy)));
        Assert.Equal((Dining, TransactionCategorySource.Suggested), (rows[43].Category!.Value, rows[43].SetBy));
        Assert.Equal((Dining, TransactionCategorySource.Suggested), (rows[44].Category!.Value, rows[44].SetBy));
        Assert.Equal(("ai", "Restaurant"), await s.Service.ReasonAsync(44));
        // Nothing left to suggest: applying again changes nothing and asks the AI no more.
        Assert.Equal(0, await s.Service.ApplySuggestionsAsync(AccessWorld.Shared));
        Assert.Single(s.Ai.Requests);

        // A rule replaces a suggestion.
        var v = t.ForUser(AccessWorld.Alice);
        await using (var db = await t.System.CreateDbContextAsync())
        {
            db.CategoryRules.Add(new CategoryRule { ProjectId = AccessWorld.Shared, CategoryId = Dining, Expression = "name:\"Example Market\"", CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }
        Assert.Equal(3, await new CategoryRuleService(v.Factory, t.Notifier, new ChangeOrigin()).ApplyExistingAsync(AccessWorld.Shared));
        rows = await CategoriesAsync(t, 40, 44);
        Assert.Equal((Dining, TransactionCategorySource.Rule), (rows[40].Category!.Value, rows[40].SetBy));

        // Confirming keeps the category as the owner's own.
        await s.Service.AcceptAsync([44], Dining);
        Assert.Equal((Dining, TransactionCategorySource.Manual), (await CategoriesAsync(t, 44))[44] is var c ? (c.Category!.Value, c.SetBy) : default);
        Assert.Null(await s.Service.ReasonAsync(44));
    }

    /// <summary>No circuit and no request: the auth state cannot be read, as in a background sync.</summary>
    private sealed class NoAuthState : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => throw new InvalidOperationException("No auth state.");
    }

    private static ServiceProvider AppServices(TestDb t, FakeAssistClient ai)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(t.System);
        services.AddSingleton(t.Notifier);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IAssistClient>(ai);
        services.AddScoped<AuthenticationStateProvider>(_ => new NoAuthState());
        services.AddScoped<ActingUser>();
        services.AddScoped<ICurrentUser, AuthStateCurrentUser>();
        services.AddScoped<AccessScopeProvider>();
        services.AddScoped<IDbContextFactory<BudgetDbContext>, ScopedDbContextFactory>();
        services.AddScoped<ChangeOrigin>();
        services.AddScoped<CategoryHierarchyService>();
        services.AddScoped<ProjectService>();
        services.AddScoped<BudgetEntryService>();
        services.AddScoped<BudgetService>();
        services.AddScoped<CategorizationService>();
        services.AddScoped<CategoryRuleService>();
        services.AddScoped<TransactionEditService>();
        services.AddScoped<AssistSettingsService>();
        services.AddScoped<CategorySuggestionService>();
        services.AddSingleton<SuggestionAutoApplier>();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    [Fact]
    public async Task After_a_sync_the_applier_suggests_as_the_owner_in_its_own_scope()
    {
        await using var t = await WorldAsync();
        await OptInAsync(t, AccessWorld.Alice);
        var ai = new FakeAssistClient(AnswerAllWith("Dining"));
        await using var app = AppServices(t, ai);
        var applier = app.GetRequiredService<SuggestionAutoApplier>();

        // Bob did not opt in: nothing happens for him.
        await applier.CategorizeAsync(AccessWorld.Bob, default);
        Assert.All((await CategoriesAsync(t, 40, 44)).Values, r => Assert.Null(r.Category));

        await applier.CategorizeAsync(AccessWorld.Alice, default);
        Assert.All((await CategoriesAsync(t, 40, 41, 42, 43, 44)).Values, r => Assert.Equal(TransactionCategorySource.Suggested, r.SetBy));
        Assert.Single(ai.Requests);
    }

    [Fact]
    public async Task Without_consent_nothing_is_applied()
    {
        await using var t = await WorldAsync();
        var s = For(t, AccessWorld.Alice);

        Assert.Equal(0, await s.Service.ApplySuggestionsAsync(AccessWorld.Shared));

        Assert.All((await CategoriesAsync(t, 40, 41, 42, 43, 44)).Values, r => Assert.Null(r.Category));
        Assert.Empty(s.Ai.Requests);
    }

    [Fact]
    public async Task Only_the_owner_applies_suggestions_to_their_project()
    {
        await using var t = await WorldAsync();
        await OptInAsync(t, AccessWorld.Bob);
        var s = For(t, AccessWorld.Bob);

        Assert.Equal(0, await s.Service.ApplySuggestionsAsync(AccessWorld.Shared));
        Assert.All((await CategoriesAsync(t, 40, 41, 42, 43, 44)).Values, r => Assert.Null(r.Category));
    }

    [Fact]
    public async Task A_booking_whose_category_was_removed_by_hand_gets_no_suggestion()
    {
        await using var t = await WorldAsync();
        await OptInAsync(t, AccessWorld.Alice);
        await using (var db = await t.System.CreateDbContextAsync())
        {
            (await db.Transactions.SingleAsync(x => x.Id == 43)).CategorySetBy = TransactionCategorySource.Manual;
            await db.SaveChangesAsync();
        }
        var s = For(t, AccessWorld.Alice);

        await s.Service.ApplySuggestionsAsync(AccessWorld.Shared);

        Assert.Equal((null, TransactionCategorySource.Manual), (await CategoriesAsync(t, 43))[43]);
    }

    [Fact]
    public async Task A_booking_the_ai_placed_nowhere_is_not_asked_about_again()
    {
        await using var t = await WorldAsync();
        await OptInAsync(t, AccessWorld.Alice);
        var s = For(t, AccessWorld.Alice, new FakeAssistClient(_ => "{\"suggestions\":[]}"));

        await s.Service.SuggestAsync(AccessWorld.Shared, null, useAi: true);
        var again = await s.Service.SuggestAsync(AccessWorld.Shared, null, useAi: true);

        Assert.Single(s.Ai.Requests);
        Assert.DoesNotContain(again.Suggestions, x => x.TransactionId == 44);
        Assert.Null(await s.Service.ReasonAsync(44));
    }
}
