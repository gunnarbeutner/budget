using System.Collections;

namespace OpenBudget.Connectors.Scalable;

/// <summary>
/// Endpoints and tunables of the Scalable connector. The GraphQL documents and inputs of the
/// 2FA steps that were never captured can be overridden by environment variables so they can be
/// corrected on a running container without a rebuild.
/// </summary>
public sealed class ScalableOptions
{
    public const string Is2faInputVariable = "OPENBUDGET_SCALABLE_IS2FA_INPUT";
    public const string Start2faQueryVariable = "OPENBUDGET_SCALABLE_START2FA_QUERY";
    public const string Start2faInputVariable = "OPENBUDGET_SCALABLE_START2FA_INPUT";
    public const string Validate2faQueryVariable = "OPENBUDGET_SCALABLE_VALIDATE2FA_QUERY";
    public const string Validate2faInputVariable = "OPENBUDGET_SCALABLE_VALIDATE2FA_INPUT";

    public Uri BaseUri { get; init; } = new("https://de.scalable.capital/");

    /// <summary>Time between two 2FA approval polls.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>JSON object sent as <c>$input</c> of is2faOnLoginEnabled (fields unknown: starts empty).</summary>
    public string Is2faEnabledInputJson { get; init; } = "{\"userId\":\"{userId}\"}";

    public string Start2faQuery { get; init; } = ScalableTwoFactorDocuments.Start2faOnLogin;

    /// <summary>JSON object sent as <c>$input</c> of start2faOnLogin.</summary>
    public string Start2faInputJson { get; init; } = "{\"userId\":\"{userId}\"}";

    public string Validate2faQuery { get; init; } = ScalableTwoFactorDocuments.Validate2faOnLogin;

    /// <summary>JSON object sent as <c>$input</c> of validate2faOnLogin; <c>{userId}</c> and <c>{mfaSessionId}</c> are substituted.</summary>
    public string Validate2faInputTemplate { get; init; } = ScalableTwoFactorDocuments.Validate2faInputTemplate;

    /// <summary>Defaults, overridden by <c>OPENBUDGET_SCALABLE_*</c> environment variables.</summary>
    public static ScalableOptions FromEnvironment() => FromVariables(
        Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => e.Value as string, StringComparer.Ordinal));

    internal static ScalableOptions FromVariables(IReadOnlyDictionary<string, string?> variables)
    {
        string? Get(string name) => variables.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
        var defaults = new ScalableOptions();
        return new ScalableOptions
        {
            Is2faEnabledInputJson = Get(Is2faInputVariable) ?? defaults.Is2faEnabledInputJson,
            Start2faQuery = Get(Start2faQueryVariable) ?? defaults.Start2faQuery,
            Start2faInputJson = Get(Start2faInputVariable) ?? defaults.Start2faInputJson,
            Validate2faQuery = Get(Validate2faQueryVariable) ?? defaults.Validate2faQuery,
            Validate2faInputTemplate = Get(Validate2faInputVariable) ?? defaults.Validate2faInputTemplate,
        };
    }
}
