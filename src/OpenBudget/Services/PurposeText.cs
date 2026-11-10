using System.Text.RegularExpressions;

namespace OpenBudget.Services;

/// <summary>Light clean-up of the bank's purpose text for the one-line list view: no parsing, only whitespace runs.</summary>
public static partial class PurposeText
{
    public static string Clean(string? purpose) =>
        string.IsNullOrWhiteSpace(purpose) ? "" : Whitespace().Replace(purpose.Trim(), " ");

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Whitespace();
}
