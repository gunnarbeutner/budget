using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>
/// The fields of a transaction a rule looks at. <paramref name="LocalAccount"/> is our account's IBAN;
/// <paramref name="AccountGroups"/> the account groups the account lies in (own group and ancestors).
/// </summary>
public readonly record struct RuleSubject(
    long? AccountId, long Amount, string? Name, string? Purpose, string? BookingText,
    string? Comment = null, string? Note = null, string? CounterpartyIban = null, string? LocalAccount = null,
    string? Currency = null, string? BankReference = null,
    string? EndToEndReference = null, string? MandateReference = null, string? CreditorId = null, string? OrderItems = null,
    IReadOnlySet<long>? AccountGroups = null)
{
    public static RuleSubject Of(Transaction t, string? localAccount = null, IReadOnlySet<long>? accountGroups = null) =>
        new(t.AccountId, t.Amount, t.Name, t.Purpose, t.BookingText, t.Comment, t.Note, t.CounterpartyIban, localAccount, t.Currency, t.BankReference,
            t.EndToEndReference, t.MandateReference, t.CreditorId, t.OrderItems, accountGroups);
}

/// <summary>
/// Pure matching logic of <see cref="CategoryRule"/>s: a rule matches when its expression (the search syntax,
/// <see cref="RuleExpression"/>) does and its category's "Restrict to" allows the booking's account. An empty or
/// invalid expression never matches.
/// </summary>
public static class CategoryRuleMatcher
{
    public static bool HasCondition(CategoryRule rule) => !string.IsNullOrWhiteSpace(rule.Expression);

    public static bool Matches(CategoryRule rule, RuleSubject t, CategoryRestrictions? restrictions = null) =>
        (restrictions == null || restrictions.Allows(rule.CategoryId, t.AccountId)) && RuleExpression.Matches(rule.Expression, t);

    /// <summary>The first enabled rule (in the given order) that matches, or null.</summary>
    public static CategoryRule? FirstMatch(IEnumerable<CategoryRule> orderedRules, RuleSubject t, CategoryRestrictions? restrictions = null) =>
        orderedRules.FirstOrDefault(r => r.Enabled && Matches(r, t, restrictions));
}
