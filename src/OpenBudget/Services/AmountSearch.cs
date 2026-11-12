using System.Globalization;
using System.Text.RegularExpressions;

namespace OpenBudget.Services;

/// <summary>
/// Reads a search text as an amount, so the transaction search can also match bookings by amount. Accepts German and
/// plain notations ("10,60", "10.60", "1.234,56", "1234.56", "229") with an optional euro sign before or after and an
/// optional leading sign. A separator followed by exactly three digits groups thousands ("1.234" is 1234); otherwise
/// the last separator marks the decimals (at most two). Without a sign the amount matches either direction.
/// </summary>
public static partial class AmountSearch
{
    [GeneratedRegex(@"^(?<sign>[+\-−]?)\s*(?<num>\d{1,3}(?<sep>[.,])\d{3}(?:\k<sep>\d{3})*(?:[.,]\d{1,2})?|\d+(?:[.,]\d{1,2})?)$", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NumberRegex();

    public static AmountSearchMatch? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Trim();
        var sign = "";
        if (s[0] is '+' or '-' or '−') { sign = s[..1]; s = s[1..].TrimStart(); }
        if (s.Length > 0 && s[0] == '€') s = s[1..].TrimStart();
        else if (s.Length > 0 && s[^1] == '€') s = s[..^1].TrimEnd();

        var m = NumberRegex().Match(sign + s);
        if (!m.Success) return null;
        var num = m.Groups["num"].Value;
        var signValue = m.Groups["sign"].Value switch { "+" => 1, "" => 0, _ => -1 };

        // The last separator marks the decimals unless three digits follow it (then it groups thousands).
        var last = num.LastIndexOfAny(['.', ',']);
        string whole = num, frac = "";
        if (last >= 0 && num.Length - last - 1 <= 2)
        {
            whole = num[..last];
            frac = num[(last + 1)..];
        }
        whole = whole.Replace(".", "", StringComparison.Ordinal).Replace(",", "", StringComparison.Ordinal);
        if (whole.Length > 15) return null;
        var cents = long.Parse(whole, CultureInfo.InvariantCulture) * 100
                    + (frac.Length == 0 ? 0 : int.Parse(frac.PadRight(2, '0'), CultureInfo.InvariantCulture));
        return new AmountSearchMatch(cents, signValue);
    }
}

/// <summary>An amount search: the amount in cents (always ≥ 0) and the sign it is restricted to: +1, −1, or 0 for either.</summary>
public sealed record AmountSearchMatch(long Cents, int Sign);
