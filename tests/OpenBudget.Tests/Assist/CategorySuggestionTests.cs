#pragma warning disable CA2000 // test objects live for the test
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OpenBudget.Assist;
using OpenBudget.Models;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests.Assist;

/// <summary>The suggestion pipeline over a small world: rules, IBAN, kNN, the AI (fake) with consent, rule gating, accepting.</summary>
public sealed partial class CategorySuggestionTests
{
    private static readonly long Groceries = AccessWorld.Id(AccessWorld.SourceAlice, 210);
    private static readonly long Dining = AccessWorld.Id(AccessWorld.SourceAlice, 211);
    private const string CounterIban = "DE02120300000000202051";

    private static Transaction Row(long id, string payee, long? category = null, string? iban = null, string? purpose = null, long amount = -1250) => new()
    {
        Id = id,
        OwnerUserId = AccessWorld.Alice,
        AccountId = AccessWorld.AccountWp,
        Amount = amount,
        Name = payee,
        Purpose = purpose,
        CounterpartyIban = iban,
        CategoryId = category,
        CategorySetBy = category == null ? TransactionCategorySource.Import : TransactionCategorySource.Manual,
        Timestamp = new DateTimeOffset(2026, 3, 12, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds() + id,
    };

    private static async Task<TestDb> WorldAsync()
    {
        var t = await AccessWorld.BuildAsync();
        await using var db = await t.System.CreateDbContextAsync();
        (await db.Accounts.SingleAsync(a => a.Id == AccessWorld.AccountWp)).Provider = "fake";
        db.Categories.Add(new Category { Id = Groceries, OwnerUserId = AccessWorld.Alice, Name = "Groceries", GroupId = AccessWorld.SharedGroup, Type = 1 });
        db.Categories.Add(new Category { Id = Dining, OwnerUserId = AccessWorld.Alice, Name = "Dining", GroupId = AccessWorld.SharedGroup, Type = 1 });
        // History
        db.Transactions.AddRange(Row(30, "Example Market", Groceries), Row(31, "Example Market", Groceries), Row(32, "Example Market", Groceries));
        db.Transactions.AddRange(Row(33, "Sample Bistro", Dining, CounterIban), Row(34, "Sample Bistro Two", Dining, CounterIban));
        // Uncategorized
        db.Transactions.AddRange(Row(40, "Example Market"), Row(41, "Example Market"), Row(42, "Example Market"));
        db.Transactions.Add(Row(43, "Some Other Name", iban: CounterIban));
        db.Transactions.Add(Row(44, "Unknown Shop", purpose: "EREF+REF-778899 SVWZ+Order 12 alice IBAN " + CounterIban));
        await db.SaveChangesAsync();
        return t;
    }

    private sealed record Setup(CategorySuggestionService Service, AssistSettingsService Settings, FakeAssistClient Ai);

    private static Setup For(TestDb t, string user, FakeAssistClient? ai = null)
    {
        var v = t.ForUser(user);
        ai ??= new FakeAssistClient(AnswerAllWith("Dining"));
        var settings = new AssistSettingsService(v.Factory, t.System, ai);
        var service = new CategorySuggestionService(v.Factory, t.System, new CategorizationService(v.Factory),
            new CategoryRuleService(v.Factory, t.Notifier, new ChangeOrigin()), v.Spending,
            new TransactionEditService(v.Factory, t.Notifier, new ChangeOrigin()), settings, ai,
            NullLogger<CategorySuggestionService>.Instance, TimeProvider.System);
        return new Setup(service, settings, ai);
    }

    /// <summary>Answers every booking of the request with the category whose path ends in <paramref name="name"/>.</summary>
    private static Func<AssistRequest, string?> AnswerAllWith(string name) => r =>
    {
        var handle = Regex.Match(r.SystemPrompt, @"^(c\d+) \| [^\n]*" + name, RegexOptions.Multiline).Groups[1].Value;
        var txs = Regex.Matches(r.UserContent, "\"tx\":\"(t\\d+)\"").Select(m => m.Groups[1].Value);
        return "{\"suggestions\":[" + string.Join(",", txs.Select(tx =>
            $"{{\"tx\":\"{tx}\",\"categoryId\":\"{handle}\",\"confidence\":0.7,\"rationale\":\"Restaurant\",\"rule\":{{\"expression\":\"name:\\\"Unknown Shop\\\"\",\"scope\":\"payee\"}}}}")) + "]}";
    };

    private static async Task OptInAsync(TestDb t, string user)
    {
        await using var db = await t.System.CreateDbContextAsync();
        (await db.UserDataStates.SingleAsync(s => s.UserId == user)).UseAiSuggestions = true;
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Deterministic_suggestions_work_without_ai_and_nothing_is_sent_without_consent()
    {
        await using var t = await WorldAsync();
        var s = For(t, AccessWorld.Alice);

        var result = await s.Service.SuggestAsync(AccessWorld.Shared, null, useAi: true);

        Assert.Empty(s.Ai.Requests);
        var byTx = result.Suggestions.ToDictionary(x => x.TransactionId);
        Assert.All(new long[] { 40, 41, 42 }, id => Assert.Equal((Groceries, "knn"), (byTx[id].CategoryId!.Value, byTx[id].Source)));
        Assert.True(byTx[40].Confidence >= 0.75);
        Assert.Equal((Dining, "iban"), (byTx[43].CategoryId!.Value, byTx[43].Source));
        Assert.False(byTx.ContainsKey(44));
        Assert.Equal(1, result.SkippedNoConsent);
        // Three identical uncategorized bookings with one suggested category make a rule candidate.
        var rule = Assert.Single(result.Rules);
        Assert.Equal(("name:\"Example Market\"", Groceries, 6, 3, 0), (rule.Expression, rule.CategoryId, rule.Matches, rule.Uncategorized, rule.Conflicts));
    }

    [Fact]
    public async Task With_consent_only_the_unsure_rest_goes_to_the_ai_redacted()
    {
        await using var t = await WorldAsync();
        await OptInAsync(t, AccessWorld.Alice);
        var s = For(t, AccessWorld.Alice);
        var progress = new List<(int, int)>();

        var result = await s.Service.SuggestAsync(AccessWorld.Shared, null, useAi: true, new SyncProgress(progress));

        var request = Assert.Single(s.Ai.Requests);
        Assert.Contains("Unknown Shop", request.UserContent, StringComparison.Ordinal);
        Assert.DoesNotContain("Example Market", request.UserContent, StringComparison.Ordinal);
        foreach (var secret in new[] { CounterIban, "REF-778899", "alice", "Giro Shared" })
            Assert.DoesNotContain(secret, request.UserContent + request.SystemPrompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Group2 › Dining", request.SystemPrompt, StringComparison.Ordinal);
        var ai = Assert.Single(result.Suggestions, x => x.TransactionId == 44);
        Assert.Equal((Dining, "ai", "Restaurant"), (ai.CategoryId!.Value, ai.Source, ai.Rationale));
        Assert.Equal([(0, 1), (1, 1)], progress);
        // The AI's rule matches only one booking: not offered.
        Assert.DoesNotContain(result.Rules, r => r.Expression.Contains("Unknown Shop", StringComparison.Ordinal));

        // Cached: asking again does not call the AI.
        await s.Service.SuggestAsync(AccessWorld.Shared, null, useAi: true);
        Assert.Single(s.Ai.Requests);
    }

    [Fact]
    public async Task Bookings_of_an_owner_who_did_not_opt_in_are_not_sent()
    {
        await using var t = await WorldAsync();
        await OptInAsync(t, AccessWorld.Bob);
        var s = For(t, AccessWorld.Bob);

        var result = await s.Service.SuggestAsync(AccessWorld.Shared, null, useAi: true);

        Assert.Empty(s.Ai.Requests);
        Assert.Equal(1, result.SkippedNoConsent);
    }

    [Fact]
    public async Task A_broken_ai_answer_keeps_the_deterministic_suggestions()
    {
        await using var t = await WorldAsync();
        await OptInAsync(t, AccessWorld.Alice);
        var s = For(t, AccessWorld.Alice, new FakeAssistClient(r => new AssistResponse(null, "I cannot help with that.", "fake", 1, 0, 1, TimeSpan.Zero)));

        var result = await s.Service.SuggestAsync(AccessWorld.Shared, null, useAi: true);

        Assert.NotNull(result.AiError);
        Assert.Equal(4, result.Suggestions.Count);
    }

    [Fact]
    public async Task Suggested_rules_are_parsed_validated_and_gated_by_matches_and_conflicts()
    {
        await using var t = await WorldAsync();
        var s = For(t, AccessWorld.Alice);

        var ok = await s.Service.CheckRuleAsync(AccessWorld.Shared, "name:\"example market\"  ", Groceries, "ai");
        Assert.NotNull(ok);
        Assert.Equal("name:\"example market\"", ok.Expression);
        Assert.True(CategorySuggestionService.Offerable(ok));

        var conflicting = await s.Service.CheckRuleAsync(AccessWorld.Shared, "name:\"example market\"", Dining, "ai");
        Assert.Equal(3, conflicting!.Conflicts);
        Assert.False(CategorySuggestionService.Offerable(conflicting));

        Assert.Null(await s.Service.CheckRuleAsync(AccessWorld.Shared, "(\"unclosed", Groceries, "ai"));
        Assert.Null(await s.Service.CheckRuleAsync(AccessWorld.Shared, "remote_account:ACC_1", Groceries, "ai"));
        Assert.Null(await s.Service.CheckRuleAsync(AccessWorld.Shared, "name:Example", 999, "ai"));
        Assert.False(CategorySuggestionService.Offerable(ok with { Matches = 1, Uncategorized = 1, ConflictIds = [] }));
    }

    [Fact]
    public async Task Accepting_sets_the_category_by_hand_and_creating_a_rule_applies_it()
    {
        await using var t = await WorldAsync();
        var s = For(t, AccessWorld.Alice);
        var result = await s.Service.SuggestAsync(AccessWorld.Shared, [43], useAi: false);
        Assert.Single(result.Suggestions);

        await s.Service.AcceptAsync([43], Dining);
        var applied = await s.Service.CreateRuleAndApplyAsync(AccessWorld.Shared, (await s.Service.CheckRuleAsync(AccessWorld.Shared, "name:\"Example Market\"", Groceries, "knn"))!);

        await using var db = await t.System.CreateDbContextAsync();
        var row = await db.Transactions.SingleAsync(x => x.Id == 43);
        Assert.Equal((Dining, TransactionCategorySource.Manual), (row.CategoryId!.Value, row.CategorySetBy));
        Assert.False(await db.CategorySuggestions.AnyAsync(x => x.TransactionId == 43));
        Assert.Equal(3, applied);
        Assert.Single(await db.CategoryRules.Where(r => r.ProjectId == AccessWorld.Shared).ToListAsync());
    }

    private sealed class SyncProgress(List<(int, int)> seen) : IProgress<(int Done, int Total)>
    {
        public void Report((int Done, int Total) value) => seen.Add(value);
    }
}
