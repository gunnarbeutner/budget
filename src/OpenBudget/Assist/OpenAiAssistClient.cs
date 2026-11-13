using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenBudget.Assist;

/// <summary>
/// The OpenAI Responses API with Structured Outputs (strict JSON schema). Retries 429 and 5xx with exponential backoff
/// (honouring Retry-After); every attempt has its own timeout. Requests are not stored by the provider
/// (<c>store: false</c>). The key is only ever put into the Authorization header and never logged.
/// </summary>
public sealed class OpenAiAssistClient : IAssistClient
{
    private readonly IHttpClientFactory _http;
    private readonly AssistOptions _options;
    private readonly ILogger<OpenAiAssistClient> _log;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public OpenAiAssistClient(IHttpClientFactory http, AssistOptions options, ILogger<OpenAiAssistClient> log)
        : this(http, options, log, Task.Delay)
    {
    }

    public OpenAiAssistClient(IHttpClientFactory http, AssistOptions options, ILogger<OpenAiAssistClient> log, Func<TimeSpan, CancellationToken, Task> delay)
    {
        _http = http;
        _options = options;
        _log = log;
        _delay = delay;
    }

    public bool Enabled => _options.Enabled;

    public string Model => _options.Model;

    public static JsonObject BuildBody(string model, AssistRequest request) => new()
    {
        ["model"] = model,
        ["store"] = false,
        ["input"] = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = request.SystemPrompt },
            new JsonObject { ["role"] = "user", ["content"] = request.UserContent },
        },
        ["text"] = new JsonObject
        {
            ["format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["name"] = request.SchemaName,
                ["strict"] = true,
                ["schema"] = request.Schema.DeepClone(),
            },
        },
    };

    public async Task<AssistResponse> CompleteAsync(AssistRequest request, CancellationToken ct = default)
    {
        if (!Enabled) throw new AssistException("AI suggestions are not configured.");
        var (text, elapsed) = await OpenAiTransport.PostAsync(_http, _options, _log, _delay, "responses", BuildBody(_options.Model, request).ToJsonString(), ct);
        return Parse(text, elapsed);
    }

    /// <summary>Reads the output text (or refusal) and the usage of a Responses API answer.</summary>
    public static AssistResponse Parse(string body, TimeSpan elapsed)
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
        string? text = null, refusal = null;
        foreach (var item in root?["output"]?.AsArray() ?? [])
        {
            if (item?["type"]?.GetValue<string>() != "message") continue;
            foreach (var part in item["content"]?.AsArray() ?? [])
            {
                var type = part?["type"]?.GetValue<string>();
                if (type == "output_text") text = (text ?? "") + part!["text"]?.GetValue<string>();
                else if (type == "refusal") refusal = part!["refusal"]?.GetValue<string>() ?? "refused";
            }
        }
        var usage = root?["usage"];
        static int Int(JsonNode? n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;
        return new AssistResponse(text, refusal, root?["model"]?.GetValue<string>() ?? "",
            Int(usage?["input_tokens"]), Int(usage?["input_tokens_details"]?["cached_tokens"]), Int(usage?["output_tokens"]), elapsed);
    }
}
