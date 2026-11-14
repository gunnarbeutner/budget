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

    /// <summary>
    /// The request body. With tools, earlier rounds follow the user message as they came back (reasoning items carry
    /// their encrypted content, as nothing is stored by the provider), and the tools stay listed in the final round,
    /// which only forbids calling them.
    /// </summary>
    public static JsonObject BuildBody(string model, AssistRequest request)
    {
        var input = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = request.SystemPrompt },
            new JsonObject { ["role"] = "user", ["content"] = request.UserContent },
        };
        foreach (var turn in request.Turns ?? []) input.Add(turn?.DeepClone());
        var body = new JsonObject
        {
            ["model"] = model,
            ["store"] = false,
            ["input"] = input,
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
        if (request.Tools.Count > 0)
        {
            body["tools"] = new JsonArray(request.Tools.Select(t => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["parameters"] = t.Parameters.DeepClone(),
                ["strict"] = true,
            }).ToArray());
            body["tool_choice"] = request.ToolsDisabled ? "none" : "auto";
            body["include"] = new JsonArray("reasoning.encrypted_content");
        }
        return body;
    }

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
        var calls = new List<AssistToolCall>();
        foreach (var item in root?["output"]?.AsArray() ?? [])
        {
            var itemType = item?["type"]?.GetValue<string>();
            if (itemType == "function_call")
            {
                calls.Add(new AssistToolCall(item!["call_id"]?.GetValue<string>() ?? "", item["name"]?.GetValue<string>() ?? "", item["arguments"]?.GetValue<string>() ?? "{}"));
                continue;
            }
            if (itemType != "message") continue;
            foreach (var part in item!["content"]?.AsArray() ?? [])
            {
                var type = part?["type"]?.GetValue<string>();
                if (type == "output_text") text = (text ?? "") + part!["text"]?.GetValue<string>();
                else if (type == "refusal") refusal = part!["refusal"]?.GetValue<string>() ?? "refused";
            }
        }
        var usage = root?["usage"];
        static int Int(JsonNode? n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;
        return new AssistResponse(text, refusal, root?["model"]?.GetValue<string>() ?? "",
            Int(usage?["input_tokens"]), Int(usage?["input_tokens_details"]?["cached_tokens"]), Int(usage?["output_tokens"]), elapsed)
        {
            ToolCalls = calls,
            OutputItems = root?["output"] is JsonArray output ? (JsonArray)output.DeepClone() : null,
        };
    }
}
