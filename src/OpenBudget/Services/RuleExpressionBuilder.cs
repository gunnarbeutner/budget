using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>The convenience inputs of the rule editor; amounts are absolute values in currency units.</summary>
public sealed record RuleParts
{
    public string? Payee { get; init; }
    public string? Purpose { get; init; }
    public bool Regex { get; init; }
    public long? AccountId { get; init; }
    public RuleAmountSign Sign { get; init; }
    public decimal? Min { get; init; }
    public decimal? Max { get; init; }
}

/// <summary>An account as the builder sees it: its id and (if any) IBAN.</summary>
public sealed record RuleAccount(long Id, string? Iban);

/// <summary>
/// Composes rule expressions from the editor's convenience inputs and takes simple expressions apart again. Only
/// expressions exactly in the composed shape decompose; anything else is edited as text.
/// </summary>
public static class RuleExpressionBuilder
{
    public static string Compose(RuleParts p, IReadOnlyCollection<RuleAccount> accounts) => ComposeNode(p, accounts)?.Print() ?? "";

    private static IRuleNode? ComposeNode(RuleParts p, IReadOnlyCollection<RuleAccount> accounts)
    {
        var items = new List<IRuleNode>();
        if (!string.IsNullOrWhiteSpace(p.Payee)) items.Add(Term("name", p.Payee, p.Regex));
        if (!string.IsNullOrWhiteSpace(p.Purpose))
            items.Add(new OrNode([Term("purpose", p.Purpose, p.Regex), Term("booking_text", p.Purpose, p.Regex)]));
        if (p.AccountId is { } id)
        {
            var iban = accounts.FirstOrDefault(a => a.Id == id)?.Iban;
            items.Add(string.IsNullOrWhiteSpace(iban) ? new AccountTerm("=", id) : new TextTerm("local_account", "=", TermKind.Phrase, Compact(iban)));
        }
        switch (p.Sign)
        {
            case RuleAmountSign.Expense:
                items.Add(new AmountTerm("<", 0));
                if (p.Min is { } emin) items.Add(new AmountTerm("<=", -emin));
                if (p.Max is { } emax) items.Add(new AmountTerm(">=", -emax));
                break;
            case RuleAmountSign.Income:
                items.Add(new AmountTerm(">", 0));
                if (p.Min is { } imin) items.Add(new AmountTerm(">=", imin));
                if (p.Max is { } imax) items.Add(new AmountTerm("<=", imax));
                break;
            case RuleAmountSign.Any:
                if (p.Min != null || p.Max != null)
                {
                    var pos = new List<IRuleNode>();
                    var neg = new List<IRuleNode>();
                    if (p.Min is { } min) { pos.Add(new AmountTerm(">=", min)); neg.Add(new AmountTerm("<=", -min)); }
                    if (p.Max is { } max) { pos.Add(new AmountTerm("<=", max)); neg.Add(new AmountTerm(">=", -max)); }
                    items.Add(new OrNode([Wrap(pos), Wrap(neg)]));
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(p), p.Sign, null);
        }
        return items.Count == 0 ? null : Wrap(items);
    }

    private static IRuleNode Wrap(List<IRuleNode> items) => items.Count == 1 ? items[0] : new AndNode(items);

    private static TextTerm Term(string field, string value, bool regex) =>
        regex ? new TextTerm(field, ":", TermKind.Regex, value.Trim())
              : new TextTerm(field, ":", TermKind.Phrase, value.Replace("\"", "", StringComparison.Ordinal).Trim());

    private static string Compact(string iban) => iban.Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();

    /// <summary>The inputs that compose exactly to <paramref name="expression"/>, or null when it has another shape.</summary>
    public static RuleParts? TryDecompose(string? expression, IReadOnlyCollection<RuleAccount> accounts)
    {
        if (string.IsNullOrWhiteSpace(expression)) return new RuleParts();
        if (RuleExpression.TryParse(expression, out _) is not { } node) return null;
        IReadOnlyList<IRuleNode> items = node is AndNode a ? a.Items : [node];
        var p = new RuleParts();
        bool? regex = null;
        foreach (var item in items)
        {
            switch (item)
            {
                case TextTerm { Field: "name", Op: ":", Kind: TermKind.Phrase or TermKind.Regex } t when p.Payee == null:
                    p = p with { Payee = t.Value };
                    regex = t.Kind == TermKind.Regex;
                    break;
                case OrNode { Items: [TextTerm { Field: "purpose" } x, TextTerm { Field: "booking_text" } y] }
                    when p.Purpose == null && x.Value == y.Value && x.Kind == y.Kind:
                    if (regex != null && regex != (x.Kind == TermKind.Regex)) return null;
                    p = p with { Purpose = x.Value };
                    regex = x.Kind == TermKind.Regex;
                    break;
                case TextTerm { Field: "local_account", Kind: TermKind.Phrase } t when p.AccountId == null:
                    var match = accounts.FirstOrDefault(acc => acc.Iban != null && Compact(acc.Iban) == Compact(t.Value));
                    if (match == null) return null;
                    p = p with { AccountId = match.Id };
                    break;
                case AccountTerm { Op: "=" } id when p.AccountId == null:
                    p = p with { AccountId = id.Value };
                    break;
                case AmountTerm { Op: "<", Value: 0 }:
                    p = p with { Sign = RuleAmountSign.Expense };
                    break;
                case AmountTerm { Op: ">", Value: 0 }:
                    p = p with { Sign = RuleAmountSign.Income };
                    break;
                case AmountTerm { Op: "<=" or ">=" } amt when p.Sign != RuleAmountSign.Any:
                    var abs = Math.Abs(amt.Value);
                    var isMin = p.Sign == RuleAmountSign.Expense ? amt.Op == "<=" : amt.Op == ">=";
                    p = isMin ? p with { Min = abs } : p with { Max = abs };
                    break;
                case OrNode { Items: [var positive, _] } when p.Sign == RuleAmountSign.Any && p.Min == null && p.Max == null:
                    foreach (var b in positive is AndNode pa ? pa.Items : [positive])
                    {
                        if (b is not AmountTerm { Op: "<=" or ">=" } bound || bound.Value < 0) return null;
                        p = bound.Op == ">=" ? p with { Min = bound.Value } : p with { Max = bound.Value };
                    }
                    break;
                default:
                    return null;
            }
        }
        p = p with { Regex = regex ?? false };
        // Only a faithful round trip counts.
        return ComposeNode(p, accounts)?.Print() == node.Print() ? p : null;
    }
}
