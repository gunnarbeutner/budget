using System.Text.Json.Nodes;

namespace OpenBudget.Assist;

/// <summary>One structured-output request: a stable system prompt (first, so the provider can cache it), the variable
/// user content and the strict JSON schema the answer must follow.</summary>
public sealed record AssistRequest(string SchemaName, JsonObject Schema, string SystemPrompt, string UserContent);

/// <summary>The model's answer: the JSON text, or a refusal. Token counts are for cost reporting.</summary>
public sealed record AssistResponse(string? Json, string? Refusal, string Model, int InputTokens, int CachedTokens, int OutputTokens, TimeSpan Elapsed);

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
