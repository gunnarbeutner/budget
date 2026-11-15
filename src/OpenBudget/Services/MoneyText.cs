using System.Globalization;
using System.Text;

namespace OpenBudget.Services;

/// <summary>
/// Reads money amounts as web pages and exports show them, in German or English notation: "1.234,56 €", "-€10.60",
/// "−10,60 EUR", "1,234.50", "(12,50)", "0,30-". Currency signs, codes and spaces are ignored. A minus anywhere
/// (ASCII or typographic) or surrounding parentheses make the amount negative. With both separators the later one
/// marks the decimals; a single separator followed by exactly three digits groups thousands ("1.000" is 1000),
/// otherwise it marks the decimals ("12,5" is 12.5).
/// </summary>
public static class MoneyText
{
    /// <summary>The amount in <paramref name="text"/>; null when empty or unreadable.</summary>
    public static decimal? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = new string(text.Replace('−', '-').Where(c => char.IsDigit(c) || c is '.' or ',' or '-' or '+' or '(' or ')').ToArray());
        var negative = s.Contains('-', StringComparison.Ordinal) || (s.StartsWith('(', StringComparison.Ordinal) && s.EndsWith(')', StringComparison.Ordinal));
        s = s.Trim('(', ')').Replace("-", "", StringComparison.Ordinal).Replace("+", "", StringComparison.Ordinal);
        if (s.Length == 0) return null;
        var lastDot = s.LastIndexOf('.', StringComparison.Ordinal);
        var lastComma = s.LastIndexOf(',', StringComparison.Ordinal);
        char? decimalSep = null;
        if (lastDot >= 0 && lastComma >= 0) decimalSep = lastDot > lastComma ? '.' : ',';
        else if (lastDot >= 0 || lastComma >= 0)
        {
            var sep = lastDot >= 0 ? '.' : ',';
            var last = Math.Max(lastDot, lastComma);
            var single = s.Count(c => c == sep) == 1;
            if (single && s.Length - last - 1 != 3) decimalSep = sep;
        }
        var digits = new StringBuilder();
        foreach (var c in s)
        {
            if (char.IsDigit(c)) digits.Append(c);
            else if (c == decimalSep) digits.Append('.');
        }
        if (!decimal.TryParse(digits.ToString(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)) return null;
        return negative ? -value : value;
    }
}
