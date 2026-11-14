#pragma warning disable CA2000 // test objects live for the test
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OpenBudget.Assist;
using OpenBudget.Models;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests.Assist;

/// <summary>"Suggest budget" end to end over a test database, with and without the (fake) AI.</summary>
public sealed partial class BudgetSuggestionTests
{
    private const long Group = 10;
    private const long Leaf = 11;
    private static readonly (long, CategoryItemType) Key = (Leaf, CategoryItemType.Category);

    private static async Task<TestDb> WorldAsync()
    {
        var t = new TestDb();
        t.AddGroup(Group);
        t.AddLeaf(Leaf, Group, "Household");
        t.AddProject(1);
        var id = 100;
        for (var i = 0; i < 24; i++)
        {
            var month = new DateTime(2024, 1, 10).AddMonths(i);
            t.AddTransaction(id++, Leaf, -20000, month, name: "Example Market");
        }
        t.AddTransaction(id, Leaf, -289000, new DateTime(2025, 3, 2), name: "Example Furniture");
        await t.Db.SaveChangesAsync();
        return t;
    }

    private static BudgetSuggestionService Service(TestDb t, IAssistClient ai)
    {
        var v = t.ForUser(TestDb.DefaultOwner);
        return new BudgetSuggestionService(v.Factory, v.Hierarchy, v.Projects, new AssistSettingsService(v.Factory, t.System, ai), ai,
            NullLogger<BudgetSuggestionService>.Instance, TimeProvider.System);
    }

    private static readonly DateOnly AsOf = new(2026, 1, 1);

    [Fact]
    public async Task Without_ai_the_baseline_is_the_suggestion_and_the_one_off_is_excluded()
    {
        await using var t = await WorldAsync();
        var ai = new FakeAssistClient(_ => "{}");
        var result = await Service(t, ai).SuggestAsync(1, 2026, [new BudgetSuggestionInput(Key, "Household", false)], useAi: true, AsOf);

        Assert.Empty(ai.Requests); // nobody opted in
        var row = Assert.Single(result.Rows);
        Assert.All(row.Suggested, m => Assert.Equal(200m, m));
        var outlier = Assert.Single(row.Outliers);
        Assert.Equal((2890m, "one_off", true), (outlier.Amount, outlier.Kind, outlier.Excluded));
        Assert.False(result.AiUsed);
    }

    [Fact]
    public async Task The_ai_classifies_outliers_and_a_big_deviation_is_flagged()
    {
        await using var t = await WorldAsync();
        await using (var db = await t.System.CreateDbContextAsync())
        {
            (await db.UserDataStates.SingleAsync(s => s.UserId == TestDb.DefaultOwner)).UseAiSuggestions = true;
            await db.SaveChangesAsync();
        }
        var ai = new FakeAssistClient(r =>
        {
            var tx = TxId().Match(r.UserContent).Groups["tx"].Value;
            return "{\"categories\":[{\"categoryId\":\"c1\",\"monthly\":[400,400,400,400,400,400,400,400,400,400,400,400],\"basis\":\"trend\"," +
                $"\"outliers\":[{{\"tx\":\"{tx}\",\"kind\":\"refund\",\"note\":\"Rückgabe\"}}],\"explanation\":\"Steigt.\"}}," +
                "{\"categoryId\":\"c9\",\"monthly\":[],\"basis\":\"none\",\"outliers\":[],\"explanation\":\"unknown\"}]}";
        });

        var result = await Service(t, ai).SuggestAsync(1, 2026, [new BudgetSuggestionInput(Key, "Household", false)], useAi: true, AsOf);

        var request = Assert.Single(ai.Requests);
        Assert.DoesNotContain("Example Furniture\",\"purpose", request.SystemPrompt, StringComparison.Ordinal);
        var row = Assert.Single(result.Rows);
        Assert.True(result.AiUsed);
        Assert.Equal(4800m, row.Suggested.Sum());
        Assert.True(row.Flagged);
        Assert.Equal(("trend", "Steigt."), (row.Basis, row.Explanation));
        Assert.Equal(("refund", "Rückgabe", true), (row.Outliers[0].Kind, row.Outliers[0].Note, row.Outliers[0].Excluded));
    }

    [Fact]
    public void The_request_carries_monthly_history_and_redacted_payee_summaries()
    {
        long id = 0;
        Transaction Tx(DateTime date, long cents, string name) => new()
        {
            Id = ++id,
            OwnerUserId = TestDb.DefaultOwner,
            CategoryId = Leaf,
            Amount = cents,
            Name = name,
            Timestamp = ((DateTimeOffset)DateTime.SpecifyKind(date, DateTimeKind.Utc)).ToUnixTimeSeconds(),
        };
        var txs = Enumerable.Range(0, 24).Select(i => Tx(new DateTime(2024, 1, 10).AddMonths(i), -3000, "Example Marketplace")).ToList();
        txs.Add(Tx(new DateTime(2025, 8, 12), -140000, "Example Marketplace"));
        txs.Add(Tx(new DateTime(2024, 6, 1), -5000, "Erika Mustermann"));
        txs.Add(Tx(new DateTime(2025, 6, 1), -5000, "Erika Mustermann"));
        var bookings = txs.Select(t => new BaselineBooking(t.Id, DateOnly.FromDateTime(t.Date), -t.Amount / 100m, CategoryMatching.PayeeKey(t.Name))).ToList();
        var baseline = BudgetBaselineMath.Compute(bookings, [], 2026, AsOf);
        var redactor = new AssistRedactor(["Erika Mustermann"]);

        var (request, _) = BudgetSuggestionService.BuildRequest(2026,
            [(new BudgetSuggestionInput(Key, "Hobbies", false), baseline, txs.ToDictionary(t => t.Id))], redactor);

        var content = request.UserContent;
        Assert.Contains("\"monthly_history\":{\"2024-01\":30,", content, StringComparison.Ordinal);
        Assert.Contains("\"2025-08\":1430", content, StringComparison.Ordinal);
        Assert.Contains("\"n\":25", content, StringComparison.Ordinal);
        Assert.Contains("\"months\":\"2024-06, 2025-06\"", content, StringComparison.Ordinal);
        Assert.Contains("payee has 24 other bookings", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Mustermann", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("PERSON_A", content, StringComparison.Ordinal);
    }

    [GeneratedRegex("\"tx\":\"(?<tx>t\\d+)\"", RegexOptions.ExplicitCapture)]
    private static partial Regex TxId();
}
