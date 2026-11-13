namespace OpenBudget.Services;

/// <summary>What a rule expression's structure says in plain words; see <see cref="RuleDescription.Of"/>.</summary>
public enum RuleShape { Mandate, Creditor, PayeeAmount, Text }

/// <summary>The shape of a rule and its values (<see cref="Amount"/> only for <see cref="RuleShape.PayeeAmount"/>).</summary>
public sealed record RuleDescription(RuleShape Shape, string Value, decimal? Amount = null)
{
    /// <summary>
    /// Recognises the common rule shapes: <c>mandate:"…"</c> (also with an <c>OR "MREF+…"</c> fallback for bookings
    /// whose mandate is only in the purpose, as MoneyMoney rules have it), <c>creditor_id:"…"</c> (likewise with
    /// <c>"CRED+…"</c>), <c>name:"…" AND amount=…</c> and a single positive text term on the payee or the default
    /// fields. Null for anything else.
    /// </summary>
    public static RuleDescription? Of(string? expression)
    {
        var node = RuleExpression.TryParse(expression, out _);
        switch (node)
        {
            case TextTerm t when Positive(t) && t.Field == "mandate":
                return new(RuleShape.Mandate, t.Value);
            case TextTerm t when Positive(t) && t.Field == "creditor_id":
                return new(RuleShape.Creditor, t.Value);
            case TextTerm t when Positive(t) && t.Field is null or "name":
                return new(RuleShape.Text, t.Value);
            case OrNode { Items: [TextTerm a, TextTerm b] }:
                return KeyWithFallback(a, b) ?? KeyWithFallback(b, a);
            case AndNode { Items: [TextTerm { Field: "name" } n, AmountTerm { Op: "=" } amount] } when Positive(n):
                return new(RuleShape.PayeeAmount, n.Value, amount.Value);
            case AndNode { Items: [AmountTerm { Op: "=" } amount, TextTerm { Field: "name" } n] } when Positive(n):
                return new(RuleShape.PayeeAmount, n.Value, amount.Value);
            default:
                return null;
        }
    }

    private static bool Positive(TextTerm t) => t.Op != "!=" && t.Kind != TermKind.Regex && t.Value.Length > 0;

    private static RuleDescription? KeyWithFallback(TextTerm key, TextTerm fallback)
    {
        if (!Positive(key) || !Positive(fallback) || fallback.Field != null) return null;
        string? tag = key.Field switch
        {
            "mandate" => "MREF+",
            "creditor_id" => "CRED+",
            _ => null,
        };
        if (tag == null || !string.Equals(fallback.Value, tag + key.Value, StringComparison.OrdinalIgnoreCase)) return null;
        return new(key.Field == "mandate" ? RuleShape.Mandate : RuleShape.Creditor, key.Value);
    }
}
