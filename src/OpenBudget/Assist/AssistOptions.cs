#pragma warning disable S1075, S6418 // the default endpoint is a fixed, published address; the constants name environment variables, not secrets
namespace OpenBudget.Assist;

/// <summary>How the AI chooses a booking's category: the Responses API (with rationale and rule proposals) or the
/// Decisions API (one choice question per booking, with probabilities, no rationale).</summary>
public enum CategoryRoute
{
    Responses,
    Decisions,
}

/// <summary>
/// Server-wide settings of the AI suggestions, read from the environment only (never stored in the database or the
/// data volume). Without an API key the AI is off: its UI is hidden and only the deterministic suggestions run.
/// <see cref="BaseUrl"/> also selects the processing region: https://eu.api.openai.com/v1 processes in Europe.
/// </summary>
public sealed class AssistOptions
{
    public const string ApiKeyVariable = "OPENBUDGET_OPENAI_API_KEY";
    public const string ModelVariable = "OPENBUDGET_OPENAI_MODEL";
    public const string BaseUrlVariable = "OPENBUDGET_OPENAI_BASE_URL";
    public const string CategoryRouteVariable = "OPENBUDGET_OPENAI_CATEGORY_ROUTE";
    public const string DecisionsModelVariable = "OPENBUDGET_OPENAI_DECISIONS_MODEL";
    public const string DefaultModel = "gpt-6.1-terra";
    public const string DefaultDecisionsModel = "gpt-6-luna";
    public const string DefaultBaseUrl = "https://api.openai.com/v1/";
    public const string HttpClientName = "openai";

    public string? ApiKey { get; init; }
    public string Model { get; init; } = DefaultModel;
    public string DecisionsModel { get; init; } = DefaultDecisionsModel;
    public CategoryRoute CategoryRoute { get; init; } = CategoryRoute.Responses;
    public Uri BaseUrl { get; init; } = new(DefaultBaseUrl);
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(90);
    public int MaxAttempts { get; init; } = 4;

    public bool Enabled => !string.IsNullOrWhiteSpace(ApiKey);

    public static AssistOptions From(Func<string, string?> read)
    {
        var model = read(ModelVariable);
        var decisionsModel = read(DecisionsModelVariable);
        var baseUrl = read(BaseUrlVariable);
        return new AssistOptions
        {
            ApiKey = read(ApiKeyVariable)?.Trim() is { Length: > 0 } key ? key : null,
            Model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim(),
            DecisionsModel = string.IsNullOrWhiteSpace(decisionsModel) ? DefaultDecisionsModel : decisionsModel.Trim(),
            CategoryRoute = string.Equals(read(CategoryRouteVariable)?.Trim(), "decisions", StringComparison.OrdinalIgnoreCase)
                ? CategoryRoute.Decisions : CategoryRoute.Responses,
            BaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? new Uri(DefaultBaseUrl) : new Uri(baseUrl.Trim().TrimEnd('/') + "/"),
        };
    }

    /// <summary>Never shows the key.</summary>
    public override string ToString() =>
        $"model={Model}, route={CategoryRoute}, decisionsModel={DecisionsModel}, baseUrl={BaseUrl}, enabled={Enabled}";
}
