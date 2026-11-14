using System.Text.Json.Nodes;

namespace OpenBudget.Assist;

/// <summary>One structured-output request: a stable system prompt (first, so the provider can cache it), the variable
/// user content and the strict JSON schema the answer must follow. With <see cref="Tools"/> the model may call them
/// instead of answering; <see cref="Turns"/> holds the earlier rounds (the model's output items and the tool results,
/// in the provider's input format) and <see cref="ToolsDisabled"/> asks for the final answer without further calls.</summary>
public sealed record AssistRequest(string SchemaName, JsonObject Schema, string SystemPrompt, string UserContent)
{
    public IReadOnlyList<AssistTool> Tools { get; init; } = [];

    public JsonArray? Turns { get; init; }

    public bool ToolsDisabled { get; init; }
}

/// <summary>A read-only function the model may call; <see cref="Parameters"/> is a strict JSON schema.</summary>
public sealed record AssistTool(string Name, string Description, JsonObject Parameters);

/// <summary>A function call of the model: its id (to answer it), the tool name and the JSON arguments.</summary>
public sealed record AssistToolCall(string CallId, string Name, string Arguments);

/// <summary>The model's answer: the JSON text, or a refusal, or tool calls. Token counts are for cost reporting.
/// <see cref="OutputItems"/> are the raw output items, sent back as they are in the next round.</summary>
public sealed record AssistResponse(string? Json, string? Refusal, string Model, int InputTokens, int CachedTokens, int OutputTokens, TimeSpan Elapsed)
{
    public IReadOnlyList<AssistToolCall> ToolCalls { get; init; } = [];

    public JsonArray? OutputItems { get; init; }
}

/// <summary>Talks to the language model. <see cref="OpenAiAssistClient"/> in production; tests use a fake.</summary>
public interface IAssistClient
{
    bool Enabled { get; }

    string Model { get; }

    Task<AssistResponse> CompleteAsync(AssistRequest request, CancellationToken ct = default);
}

public sealed class AssistException : Exception
{
    public AssistException()
    {
    }

    public AssistException(string message) : base(message)
    {
    }

    public AssistException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
