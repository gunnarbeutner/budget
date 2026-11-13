using Microsoft.Extensions.Logging.Abstractions;
using OpenBudget.Assist;
using Xunit;
using Xunit.Abstractions;

namespace OpenBudget.Tests.Assist;

/// <summary>Runs only when OPENBUDGET_OPENAI_API_KEY is set (otherwise skipped): a real round trip.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class LiveOpenAiFactAttribute : FactAttribute
{
    public LiveOpenAiFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AssistOptions.ApiKeyVariable)))
            Skip = "Set OPENBUDGET_OPENAI_API_KEY to run the live OpenAI smoke test.";
    }
}

/// <summary>
/// A real request to the configured model with a handful of made-up bookings, checking that the strict schemas are
/// accepted and the answers validate. Never runs with real data.
/// </summary>
[Trait("Category", "LiveOpenAI")]
public sealed class LiveOpenAiSmokeTests(ITestOutputHelper output)
{
    private sealed class Factory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new() { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static OpenAiAssistClient Client() =>
        new(new Factory(), AssistOptions.From(Environment.GetEnvironmentVariable), NullLogger<OpenAiAssistClient>.Instance);

    [LiveOpenAiFact]
    public async Task Category_schema_round_trip()
    {
        List<PromptCategory> categories =
        [
            new("c1", 1, "Haushalt › Lebensmittel", false),
            new("c2", 2, "Mobilität › Tanken", false),
            new("c3", 3, "Wohnen › Miete", false),
            new("c4", 4, "Einnahmen › Gehalt", true),
            new("c5", 5, "Freizeit › Streaming", false),
        ];
        var redactor = new AssistRedactor(["Erika Mustermann"]);
        var sources = new[]
        {
            new AssistBookingSource(1, new DateOnly(2026, 10, 2), -6342, "EUR", "Beispielmarkt Filiale 0815", "Einkauf vom 01.10.", "Kartenzahlung", null, "Giro"),
            new AssistBookingSource(2, new DateOnly(2026, 10, 3), -7810, "EUR", "Musterfrau Tankstelle", "Tankstelle Musterstadt", "Kartenzahlung", null, "Giro"),
            new AssistBookingSource(3, new DateOnly(2026, 10, 1), -95000, "EUR", "Beispiel Wohnbau GmbH", "Miete Oktober Wohnung 3", "Dauerauftrag", "152", "Giro", "DE02120300000000202051"),
            new AssistBookingSource(4, new DateOnly(2026, 9, 30), 312345, "EUR", "Fiktiv AG", "Lohn/Gehalt 09.2026 Erika Mustermann", "Gutschrift", "153", "Giro"),
            new AssistBookingSource(5, new DateOnly(2026, 10, 5), -1399, "EUR", "Streamdienst Beispiel", "Abo Oktober", "Lastschrift", "105", "Kreditkarte"),
        };
        var items = sources.Select(s => new PromptItem(redactor.Redact(s), [])).ToList();
        var client = Client();

        var response = await client.CompleteAsync(new AssistRequest(CategoryPrompt.SchemaName, CategoryPrompt.Schema(),
            CategoryPrompt.SystemPrompt(categories), CategoryPrompt.UserContent(items)));
        output.WriteLine($"model={response.Model} ms={response.Elapsed.TotalMilliseconds:0} in={response.InputTokens} cached={response.CachedTokens} out={response.OutputTokens}");
        output.WriteLine(response.Json ?? response.Refusal);

        var answers = CategoryPrompt.ParseAnswer(response, redactor, categories.ToDictionary(c => c.Handle, StringComparer.Ordinal));
        Assert.Equal(5, answers.Count);
        Assert.Equal(3, answers.Single(a => a.TransactionId == 3).CategoryId);
        Assert.Equal(4, answers.Single(a => a.TransactionId == 4).CategoryId);
    }

    [LiveOpenAiFact]
    public async Task Decisions_choice_round_trip()
    {
        List<PromptCategory> categories =
        [
            new("c1", 1, "Haushalt › Lebensmittel", false),
            new("c2", 2, "Mobilität › Tanken", false),
            new("c3", 3, "Wohnen › Miete", false),
            new("c4", 4, "Einnahmen › Gehalt", true),
            new("c5", 5, "Freizeit › Streaming", false),
        ];
        var redactor = new AssistRedactor(["Erika Mustermann"]);
        var items = new[]
        {
            (Id: 3L, Expected: 3L, Source: new AssistBookingSource(3, new DateOnly(2026, 10, 1), -95000, "EUR", "Beispiel Wohnbau GmbH", "Miete Oktober Wohnung 3", "Dauerauftrag", "152", "Giro")),
            (Id: 4L, Expected: 4L, Source: new AssistBookingSource(4, new DateOnly(2026, 9, 30), 312345, "EUR", "Fiktiv AG", "Lohn/Gehalt 09.2026 Erika Mustermann", "Gutschrift", "153", "Giro")),
            (Id: 2L, Expected: 2L, Source: new AssistBookingSource(2, new DateOnly(2026, 10, 3), -7810, "EUR", "Beispiel Tankstelle", "Tankstelle Musterstadt", "Kartenzahlung", null, "Giro")),
        }.Select(x => (x.Expected, Item: new DecisionItem(x.Id, new PromptItem(redactor.Redact(x.Source), []), categories))).ToList();
        var client = new OpenAiDecisionsClient(new Factory(), AssistOptions.From(Environment.GetEnvironmentVariable), NullLogger<OpenAiDecisionsClient>.Instance);

        var answers = await new DecisionsCategoryChooser(client).ChooseAsync(items.Select(i => i.Item).ToList());

        foreach (var (result, response) in answers)
            output.WriteLine($"model={response.Model} ms={response.Elapsed.TotalMilliseconds:0} in={response.InputTokens} out={response.OutputTokens} " +
                $"choice={result?.CategoryId} p={result?.Probability:0.00} second={result?.SecondCategoryId} p2={result?.SecondProbability:0.00}");
        for (var i = 0; i < items.Count; i++)
        {
            Assert.Equal(items[i].Expected, answers[i].Result?.CategoryId);
            Assert.InRange(answers[i].Result!.Probability, 0.0, 1.0);
        }
    }

    [LiveOpenAiFact]
    public async Task Budget_schema_round_trip()
    {
        var redactor = new AssistRedactor([]);
        var outlier = redactor.Redact(new AssistBookingSource(9, new DateOnly(2025, 3, 2), -189000, "EUR", "Beispiel Möbelhaus", "Sofa", "Kartenzahlung", null, "Giro"));
        var content = "Categories:\n" + System.Text.Json.JsonSerializer.Serialize(new[]
        {
            new
            {
                category_id = "c1", name = "Wohnen › Einrichtung", income = false, months_of_data = 36, median_month = 80m, trend = 0m, basis = "median",
                baseline_monthly = Enumerable.Repeat(80m, 12).ToArray(), recurring = Array.Empty<object>(), outliers = new[] { outlier },
            },
        }, CategoryPrompt.Json);
        var response = await Client().CompleteAsync(new AssistRequest(BudgetSuggestionService.SchemaName, BudgetSuggestionService.Schema(),
            "You help plan a yearly household budget. Classify outliers as one_off, recurring or refund; return 12 monthly amounts.", content));
        output.WriteLine($"model={response.Model} ms={response.Elapsed.TotalMilliseconds:0} in={response.InputTokens} out={response.OutputTokens}");
        output.WriteLine(response.Json ?? response.Refusal);

        var row = new BudgetSuggestionRow((1, OpenBudget.Services.CategoryItemType.Category), "Einrichtung", false, Enumerable.Repeat(80m, 12).ToArray(),
            Enumerable.Repeat(80m, 12).ToArray(), "median", [new BudgetOutlier(9, new DateOnly(2025, 3, 2), 1890m, null, "one_off", null, true)], null, false, 36);
        var merged = BudgetSuggestionService.Merge(response, redactor, new Dictionary<string, int>(StringComparer.Ordinal) { ["c1"] = 0 }, [row]);
        Assert.Equal(12, merged[0].Suggested.Length);
        Assert.Contains(merged[0].Outliers[0].Kind, new[] { "one_off", "recurring", "refund" });
    }
}
