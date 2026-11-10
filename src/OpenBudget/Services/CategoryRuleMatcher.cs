using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>The fields of a transaction a rule looks at. <paramref name="LocalAccount"/> is our account's IBAN.</summary>
public readonly record struct RuleSubject(
    long? AccountId, long Amount, string? Name, string? Purpose, string? BookingText,
    string? Comment = null, string? Note = null, string? CounterpartyIban = null, string? LocalAccount = null,
    string? Currency = null, string? BankReference = null)
{
    public static RuleSubject Of(Transaction t, string? localAccount = null) =>
        new(t.AccountId, t.Amount, t.Name, t.Purpose, t.BookingText, t.Comment, t.Note, t.CounterpartyIban, localAccount, t.Currency, t.BankReference);
}

/// <summary>
/// Pure matching logic of <see cref="CategoryRule"/>s: a rule matches when its expression (the search syntax,
/// <see cref="RuleExpression"/>) does. An empty or invalid expression never matches.
/// </summary>
public static class CategoryRuleMatcher
{
    public static bool HasCondition(CategoryRule rule) => !string.IsNullOrWhiteSpace(rule.Expression);

    public static bool Matches(CategoryRule rule, RuleSubject t) => RuleExpression.Matches(rule.Expression, t);

    /// <summary>The first enabled rule (in the given order) that matches, or null.</summary>
    public static CategoryRule? FirstMatch(IEnumerable<CategoryRule> orderedRules, RuleSubject t) =>
        orderedRules.FirstOrDefault(r => r.Enabled && Matches(r, t));
}
