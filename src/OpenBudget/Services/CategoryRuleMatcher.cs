using System.Text.RegularExpressions;
using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>The fields of a transaction a rule looks at.</summary>
public readonly record struct RuleSubject(long? AccountId, long Amount, string? Name, string? Purpose, string? BookingText)
{
    public static RuleSubject Of(Transaction t) => new(t.AccountId, t.Amount, t.Name, t.Purpose, t.BookingText);
}

/// <summary>
/// Pure matching logic of <see cref="CategoryRule"/>s. All conditions of a rule must hold; a rule without any condition
/// never matches (it would categorise everything). Text: case-insensitive "contains", or a case-insensitive regular
/// expression (with a timeout; an invalid or too slow pattern simply does not match).
/// </summary>
public static class CategoryRuleMatcher
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    public static bool HasCondition(CategoryRule rule) =>
        !string.IsNullOrWhiteSpace(rule.PayeeText) || !string.IsNullOrWhiteSpace(rule.PurposeText) || rule.AccountId != null
        || rule.AmountSign != RuleAmountSign.Any || rule.MinAmountCents != null || rule.MaxAmountCents != null;

    /// <summary>Whether <paramref name="pattern"/> is a usable regular expression.</summary>
    public static bool IsValidRegex(string pattern)
    {
        try
        {
            _ = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static bool Matches(CategoryRule rule, RuleSubject t)
    {
        if (!HasCondition(rule)) return false;
        if (rule.AccountId is { } account && t.AccountId != account) return false;
        if (rule.AmountSign == RuleAmountSign.Expense && t.Amount >= 0) return false;
        if (rule.AmountSign == RuleAmountSign.Income && t.Amount <= 0) return false;
        var abs = Math.Abs(t.Amount);
        if (rule.MinAmountCents is { } min && abs < min) return false;
        if (rule.MaxAmountCents is { } max && abs > max) return false;
        if (!string.IsNullOrWhiteSpace(rule.PayeeText) && !TextMatches(rule.PayeeText.Trim(), rule.UseRegex, t.Name)) return false;
        if (!string.IsNullOrWhiteSpace(rule.PurposeText)
            && !TextMatches(rule.PurposeText.Trim(), rule.UseRegex, t.Purpose)
            && !TextMatches(rule.PurposeText.Trim(), rule.UseRegex, t.BookingText)) return false;
        return true;
    }

    /// <summary>The first enabled rule (in the given order) that matches, or null.</summary>
    public static CategoryRule? FirstMatch(IEnumerable<CategoryRule> orderedRules, RuleSubject t) =>
        orderedRules.FirstOrDefault(r => r.Enabled && Matches(r, t));

    private static bool TextMatches(string pattern, bool regex, string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        if (!regex) return text.Contains(pattern, StringComparison.OrdinalIgnoreCase);
        try
        {
            return Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
