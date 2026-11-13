using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenBudget.Assist;

/// <summary>One option of a choice question: the value the model returns and what it means.</summary>
public sealed record DecisionChoice(string Value, string Description);

/// <summary>A choice question of the Decisions API (2 to 255 options).</summary>
public sealed record ChoiceQuestion(string Name, string Instructions, IReadOnlyList<DecisionChoice> Choices);

/// <summary>One Decisions request: questions that share one input.</summary>
public sealed record DecisionRequest(string Input, IReadOnlyList<ChoiceQuestion> Questions);

/// <summary>The answer to a choice question; <see cref="Choice"/> is null when the model refused it.</summary>
public sealed record ChoiceAnswer(string? Name, string? Choice, double Confidence, IReadOnlyList<(string Value, double Probability)> Probabilities);

public sealed record DecisionResponse(IReadOnlyList<ChoiceAnswer> Answers, string Model, int InputTokens, int CachedTokens, int OutputTokens, TimeSpan Elapsed);

/// <summary>Talks to the Decisions API. <see cref="OpenAiDecisionsClient"/> in production; tests use a fake.</summary>
public interface IDecisionsClient
{
    bool Enabled { get; }

    string Model { get; }

    Task<DecisionResponse> DecideAsync(DecisionRequest request, CancellationToken ct = default);
}

/// <summary>
/// The OpenAI Decisions API (POST /decisions). Uses the same endpoint, key and retries as the Responses client; the
/// model is <see cref="AssistOptions.DecisionsModel"/>.
/// </summary>
public sealed class OpenAiDecisionsClient : IDecisionsClient
{
    private readonly IHttpClientFactory _http;
    private readonly AssistOptions _options;
    private readonly ILogger<OpenAiDecisionsClient> _log;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public OpenAiDecisionsClient(IHttpClientFactory http, AssistOptions options, ILogger<OpenAiDecisionsClient> log)
        : this(http, options, log, Task.Delay)
    {
    }

    public OpenAiDecisionsClient(IHttpClientFactory http, AssistOptions options, ILogger<OpenAiDecisionsClient> log, Func<TimeSpan, CancellationToken, Task> delay)
    {
        _http = http;
        _options = options;
        _log = log;
        _delay = delay;
    }

    public bool Enabled => _options.Enabled;

    public string Model => _options.DecisionsModel;

    public static JsonObject BuildBody(string model, DecisionRequest request)
    {
        var questions = new JsonArray();
        foreach (var q in request.Questions)
        {
            var choices = new JsonArray();
            foreach (var c in q.Choices) choices.Add(new JsonObject { ["value"] = c.Value, ["description"] = c.Description });
            questions.Add(new JsonObject { ["type"] = "choice", ["name"] = q.Name, ["instructions"] = q.Instructions, ["choices"] = choices });
        }
        return new JsonObject { ["model"] = model, ["input"] = request.Input, ["questions"] = questions };
    }

    public async Task<DecisionResponse> DecideAsync(DecisionRequest request, CancellationToken ct = default)
    {
        if (!Enabled) throw new AssistException("AI suggestions are not configured.");
        var (text, elapsed) = await OpenAiTransport.PostAsync(_http, _options, _log, _delay, "decisions", BuildBody(Model, request).ToJsonString(), ct);
        return Parse(text, elapsed);
    }

    /// <summary>Reads the choice answers (a refusal becomes an answer without choice) and the usage.</summary>
    public static DecisionResponse Parse(string body, TimeSpan elapsed)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(body);
        }
        catch (JsonException ex)
        {
            throw new AssistException("The AI service sent an unreadable answer.", ex);
        }
        var answers = new List<ChoiceAnswer>();
        foreach (var a in root?["answers"]?.AsArray() ?? [])
        {
            if (a is not JsonObject o) continue;
            var name = Str(o["name"]);
            if (Str(o["type"]) != "choice")
            {
                answers.Add(new ChoiceAnswer(name, null, 0, []));
                continue;
            }
            var probabilities = (o["probabilities"]?.AsArray() ?? [])
                .Select(p => (Value: Str(p?["value"]), Probability: Num(p?["probability"])))
                .Where(p => p.Value != null)
                .Select(p => (p.Value!, p.Probability))
                .ToList();
            answers.Add(new ChoiceAnswer(name, Str(o["choice"]), Num(o["confidence"]), probabilities));
        }
        var usage = root?["usage"];
        return new DecisionResponse(answers, Str(root?["model"]) ?? "",
            (int)Num(usage?["input_tokens"]), (int)Num(usage?["input_tokens_details"]?["cached_tokens"]), (int)Num(usage?["output_tokens"]), elapsed);
    }

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static double Num(JsonNode? n) => n is JsonValue v && v.TryGetValue<double>(out var d) && double.IsFinite(d) ? d : 0;
}

/// <summary>A booking for the Decisions route: its prompt (redacted, with neighbours) and the categories allowed for it.</summary>
public sealed record DecisionItem(long TransactionId, PromptItem Prompt, IReadOnlyList<PromptCategory> Allowed);

/// <summary>The chosen category with its probability, and the runner-up.</summary>
public sealed record DecisionChoiceResult(long TransactionId, long CategoryId, double Probability, long? SecondCategoryId, double SecondProbability);

/// <summary>
/// Chooses categories with the Decisions API: one request per booking (the redacted booking and its similar
/// categorized neighbours as input), one choice question over the categories allowed for it. Confidence is the
/// returned probability of the chosen option. A question takes at most <see cref="MaxChoices"/> options; larger trees
/// keep the neighbours' categories, then those of the booking's direction (income or spending).
/// </summary>
public sealed class DecisionsCategoryChooser(IDecisionsClient client)
{
    public const int MaxChoices = 255;
    public const int Parallelism = 4;
    public const string QuestionName = "category";

    public const string Instructions = "Pick the category of this bank booking of a German household. The options describe each category: "
        + "path, income or expense, the only own accounts it is for (when limited) and the household's rules for it.\n" + CategoryPrompt.Guidance;

    /// <summary>The options for one booking, in the category tree's order, cut to <see cref="MaxChoices"/>.</summary>
    public static List<PromptCategory> Options(PromptItem item, IReadOnlyList<PromptCategory> allowed)
    {
        if (allowed.Count <= MaxChoices) return allowed.ToList();
        var neighbours = item.Neighbours.Select(n => n.CategoryHandle).ToHashSet(StringComparer.Ordinal);
        var incoming = item.Booking.Amount > 0;
        var keep = allowed.Select((c, i) => (c, i))
            .OrderBy(x => neighbours.Contains(x.c.Handle) ? 0 : x.c.Income == incoming ? 1 : 2)
            .ThenBy(x => x.i)
            .Take(MaxChoices)
            .ToHashSet();
        return keep.OrderBy(x => x.i).Select(x => x.c).ToList();
    }

    public static DecisionRequest BuildRequest(PromptItem item, IReadOnlyList<PromptCategory> options) =>
        new(CategoryPrompt.UserContent([item]),
            [new ChoiceQuestion(QuestionName, Instructions, options.Select(c => new DecisionChoice(c.Handle, CategoryPrompt.Describe(c))).ToList())]);

    /// <summary>The chosen category (null for a refusal or an unknown value) and the runner-up among the options.</summary>
    public static DecisionChoiceResult? Parse(long transactionId, DecisionResponse response, IReadOnlyDictionary<string, PromptCategory> options)
    {
        var answer = response.Answers.FirstOrDefault(a => a.Name == QuestionName) ?? (response.Answers.Count > 0 ? response.Answers[0] : null);
        if (answer?.Choice is not { } choice || !options.TryGetValue(choice, out var chosen)) return null;
        var probabilities = answer.Probabilities.Where(p => options.ContainsKey(p.Value)).ToList();
        var probability = probabilities.Where(p => p.Value == choice).Select(p => p.Probability).DefaultIfEmpty(answer.Confidence).First();
        var second = probabilities.Where(p => p.Value != choice).OrderByDescending(p => p.Probability).Select(p => ((string Value, double Probability)?)p).FirstOrDefault();
        return new DecisionChoiceResult(transactionId, chosen.Id, Math.Clamp(probability, 0, 1),
            second is { } s ? options[s.Value].Id : null, second is { } t ? Math.Clamp(t.Probability, 0, 1) : 0);
    }

    /// <summary>Asks for every item with up to <see cref="Parallelism"/> requests at a time. Progress counts finished items.</summary>
    public async Task<List<(DecisionChoiceResult? Result, DecisionResponse Response)>> ChooseAsync(
        IReadOnlyList<DecisionItem> items, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var results = new (DecisionChoiceResult?, DecisionResponse)[items.Count];
        var done = 0;
        await Parallel.ForEachAsync(Enumerable.Range(0, items.Count), new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = ct }, async (i, token) =>
        {
            var item = items[i];
            var options = Options(item.Prompt, item.Allowed);
            if (options.Count < 2)
            {
                results[i] = (null, new DecisionResponse([], client.Model, 0, 0, 0, TimeSpan.Zero));
            }
            else
            {
                var response = await client.DecideAsync(BuildRequest(item.Prompt, options), token);
                results[i] = (Parse(item.TransactionId, response, options.ToDictionary(c => c.Handle, StringComparer.Ordinal)), response);
            }
            progress?.Report(Interlocked.Increment(ref done));
        });
        return results.ToList();
    }

    /// <summary>The stored rationale of a Decisions suggestion: "decision|0.87|path of the runner-up" (see <see cref="ReadRationale"/>).</summary>
    public static string Rationale(double probability, string? secondPath) =>
        string.Create(CultureInfo.InvariantCulture, $"decision|{probability:0.00}|{secondPath}");

    public static (double Probability, string? SecondPath)? ReadRationale(string? rationale)
    {
        if (rationale == null || !rationale.StartsWith("decision|", StringComparison.Ordinal)) return null;
        var parts = rationale.Split('|', 3);
        if (parts.Length < 3 || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var p)) return null;
        return (p, parts[2].Length == 0 ? null : parts[2]);
    }
}
