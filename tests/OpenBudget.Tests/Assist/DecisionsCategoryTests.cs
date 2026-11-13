#pragma warning disable CA2000 // test objects live for the test
using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using OpenBudget.Assist;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests.Assist;

/// <summary>Answers choice questions from a function of the request and records every request; never touches the network.</summary>
internal sealed class FakeDecisionsClient(Func<DecisionRequest, ChoiceAnswer> answer) : IDecisionsClient
{
    public bool Enabled => true;

    public string Model => "fake-luna";

    public List<DecisionRequest> Requests { get; } = [];

    public Task<DecisionResponse> DecideAsync(DecisionRequest request, CancellationToken ct = default)
    {
        lock (Requests) Requests.Add(request);
        return Task.FromResult(new DecisionResponse([answer(request)], Model, 80, 0, 0, TimeSpan.FromMilliseconds(3)));
    }
}

public sealed class DecisionsCategoryTests
{
    private static readonly List<PromptCategory> Categories =
    [
        new("c1", 11, "Household › Groceries", false),
        new("c2", 12, "Leisure › Dining", false),
        new("c3", 13, "Income › Salary", true),
    ];

    private static PromptItem Item(decimal amount = -12.5m, string? neighbourHandle = null)
    {
        var redactor = new AssistRedactor([]);
        var booking = redactor.Redact(new AssistBookingSource(7, new DateOnly(2026, 10, 2), (long)(amount * 100), "EUR", "Example Market", "Einkauf", "Kartenzahlung", null, "Giro"));
        var neighbours = neighbourHandle == null
            ? new List<(AssistBooking, string)>()
            : [(redactor.Redact(new AssistBookingSource(8, new DateOnly(2026, 9, 2), -999, "EUR", "Example Market", "Einkauf", "Kartenzahlung", null, "Giro")), neighbourHandle)];
        return new PromptItem(booking, neighbours);
    }

    [Fact]
    public void The_request_body_has_one_choice_question_over_the_options()
    {
        var request = DecisionsCategoryChooser.BuildRequest(Item(neighbourHandle: "c1"), Categories);
        var body = OpenAiDecisionsClient.BuildBody("gpt-6-luna", request);

        Assert.Equal("gpt-6-luna", (string?)body["model"]);
        Assert.Contains("Example Market", (string?)body["input"], StringComparison.Ordinal);
        Assert.Contains("\"category_id\":\"c1\"", (string?)body["input"], StringComparison.Ordinal);
        var q = Assert.Single(body["questions"]!.AsArray())!;
        Assert.Equal(("choice", "category"), ((string?)q["type"], (string?)q["name"]));
        var choices = q["choices"]!.AsArray();
        Assert.Equal(["c1", "c2", "c3"], choices.Select(c => (string?)c!["value"]));
        Assert.Equal("Income › Salary (income)", (string?)choices[2]!["description"]);
    }

    [Fact]
    public void An_api_answer_maps_to_the_chosen_probability_and_the_runner_up()
    {
        const string json = """
            {"answers":[{"type":"choice","name":"category","choice":"c2","confidence":0.6,
              "probabilities":[{"value":"c1","probability":0.25},{"value":"c2","probability":0.7},{"value":"c3","probability":0.05}]}],
             "model":"gpt-6-luna","usage":{"input_tokens":321,"input_tokens_details":{"cached_tokens":100},"output_tokens":0}}
            """;
        var response = OpenAiDecisionsClient.Parse(json, TimeSpan.FromMilliseconds(40));
        Assert.Equal((321, 100, "gpt-6-luna"), (response.InputTokens, response.CachedTokens, response.Model));

        var result = DecisionsCategoryChooser.Parse(7, response, Categories.ToDictionary(c => c.Handle, StringComparer.Ordinal))!;

        Assert.Equal((12L, 0.7, 11L, 0.25), (result.CategoryId, result.Probability, result.SecondCategoryId!.Value, result.SecondProbability));
    }

    [Fact]
    public void A_refusal_or_an_unknown_value_gives_no_suggestion()
    {
        var options = Categories.ToDictionary(c => c.Handle, StringComparer.Ordinal);
        var refused = OpenAiDecisionsClient.Parse("""{"answers":[{"type":"refusal","name":"category"}],"model":"m"}""", TimeSpan.Zero);
        var unknown = new DecisionResponse([new ChoiceAnswer("category", "c99", 0.9, [("c99", 0.9)])], "m", 0, 0, 0, TimeSpan.Zero);

        Assert.Null(DecisionsCategoryChooser.Parse(7, refused, options));
        Assert.Null(DecisionsCategoryChooser.Parse(7, unknown, options));
    }

    [Fact]
    public void More_than_255_categories_keep_the_neighbours_and_the_bookings_direction_first()
    {
        var many = Enumerable.Range(1, 300).Select(i => new PromptCategory("c" + i.ToString(CultureInfo.InvariantCulture), i, "Cat " + i, Income: i <= 100)).ToList();

        var options = DecisionsCategoryChooser.Options(Item(neighbourHandle: "c5"), many);

        Assert.Equal(DecisionsCategoryChooser.MaxChoices, options.Count);
        Assert.Contains(options, c => c.Handle == "c5");
        Assert.Equal(200, options.Count(c => !c.Income));
        Assert.Equal(options.OrderBy(c => c.Id), options);
    }

    [Fact]
    public void The_rationale_round_trips_probability_and_runner_up()
    {
        var text = DecisionsCategoryChooser.Rationale(0.873, "Leisure › Dining");
        Assert.Equal((0.87, "Leisure › Dining"), DecisionsCategoryChooser.ReadRationale(text)!.Value);
        Assert.Null(DecisionsCategoryChooser.ReadRationale("Restaurant"));
    }

    [Fact]
    public void The_route_comes_from_the_environment()
    {
        var on = AssistOptions.From(n => n switch { AssistOptions.CategoryRouteVariable => "Decisions", AssistOptions.ApiKeyVariable => "sk-secret", _ => null });
        var off = AssistOptions.From(_ => null);
        Assert.Equal((CategoryRoute.Decisions, "gpt-6-luna"), (on.CategoryRoute, on.DecisionsModel));
        Assert.Equal(CategoryRoute.Responses, off.CategoryRoute);
        Assert.DoesNotContain("sk-secret", on.ToString(), StringComparison.Ordinal);
    }
}

/// <summary>The suggestion pipeline with OPENBUDGET_OPENAI_CATEGORY_ROUTE=decisions.</summary>
public sealed partial class CategorySuggestionTests
{
    private static (CategorySuggestionService Service, FakeAssistClient Ai, FakeDecisionsClient Decisions) WithDecisions(TestDb t, string user)
    {
        var v = t.ForUser(user);
        var ai = new FakeAssistClient(AnswerAllWith("Dining"));
        // Picks the option described as Dining with 0.8, Groceries second.
        var decisions = new FakeDecisionsClient(r =>
        {
            var choices = r.Questions[0].Choices;
            var dining = choices.FirstOrDefault(c => c.Description.EndsWith("Dining", StringComparison.Ordinal))?.Value ?? choices[0].Value;
            var probabilities = choices.Select(c => (c.Value, c.Value == dining ? 0.8 : c.Description.EndsWith("Groceries", StringComparison.Ordinal) ? 0.15 : 0.05 / choices.Count)).ToList();
            return new ChoiceAnswer("category", dining, 0.8, probabilities);
        });
        var settings = new AssistSettingsService(v.Factory, t.System, ai);
        var service = new CategorySuggestionService(v.Factory, t.System, new CategorizationService(v.Factory),
            new CategoryRuleService(v.Factory, t.Notifier, new ChangeOrigin()), v.Spending,
            new TransactionEditService(v.Factory, t.Notifier, new ChangeOrigin()), settings, ai,
            NullLogger<CategorySuggestionService>.Instance, TimeProvider.System, null,
            decisions, new AssistOptions { ApiKey = "test", CategoryRoute = CategoryRoute.Decisions });
        return (service, ai, decisions);
    }

    [Fact]
    public async Task The_decisions_route_asks_one_redacted_question_per_unsure_booking()
    {
        await using var t = await WorldAsync();
        await OptInAsync(t, AccessWorld.Alice);
        var (service, ai, decisions) = WithDecisions(t, AccessWorld.Alice);

        var result = await service.SuggestAsync(AccessWorld.Shared, null, useAi: true);

        Assert.Empty(ai.Requests);
        var request = Assert.Single(decisions.Requests);
        Assert.Contains("Unknown Shop", request.Input, StringComparison.Ordinal);
        foreach (var secret in new[] { CounterIban, "REF-778899", "alice", "Giro Shared" })
            Assert.DoesNotContain(secret, request.Input, StringComparison.OrdinalIgnoreCase);
        var s = Assert.Single(result.Suggestions, x => x.TransactionId == 44);
        Assert.Equal((Dining, "ai", 0.8), (s.CategoryId!.Value, s.Source, s.Confidence));
        Assert.Equal("Group2 › Groceries", DecisionsCategoryChooser.ReadRationale(s.Rationale)!.Value.SecondPath);
        Assert.DoesNotContain(result.Rules, r => r.Source == "ai");
    }

    [Fact]
    public async Task The_decisions_route_offers_only_categories_allowed_for_the_account()
    {
        await using var t = await WorldAsync();
        await OptInAsync(t, AccessWorld.Alice);
        await RestrictAsync(t, Dining, AccessWorld.AccountPrivate);
        var (service, _, decisions) = WithDecisions(t, AccessWorld.Alice);

        var result = await service.SuggestAsync(AccessWorld.Shared, null, useAi: true);

        Assert.NotEmpty(decisions.Requests);
        Assert.All(decisions.Requests, r => Assert.DoesNotContain(r.Questions[0].Choices, c => c.Description.EndsWith("Dining", StringComparison.Ordinal)));
        Assert.DoesNotContain(result.Suggestions, x => x.CategoryId == Dining);
    }
}
