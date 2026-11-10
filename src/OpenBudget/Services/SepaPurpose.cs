using System.Text.RegularExpressions;

namespace OpenBudget.Services;

/// <summary>
/// The SEPA structured tags banks put into the purpose text ("EREF+… MREF+… CRED+… SVWZ+…", as in MT940 :86: and in
/// MoneyMoney's unformatted purpose). Each tag's value runs to the next tag.
/// </summary>
public static partial class SepaPurpose
{
    [GeneratedRegex(@"(?<tag>EREF|KREF|MREF|CRED|DEBT|SVWZ|ABWA|ABWE|IBAN|BIC|COAM|OAMT|ORDP|BENM|PURP|RTRN)\+", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Tag();

    /// <summary>Tag → value (first occurrence; empty and "NOTPROVIDED" values left out).</summary>
    public static Dictionary<string, string> Parse(string? text)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(text)) return result;
        var matches = Tag().Matches(text);
        for (var i = 0; i < matches.Count; i++)
        {
            var start = matches[i].Index + matches[i].Length;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
            var value = text[start..end].Trim();
            if (value.Length == 0 || value.Equals("NOTPROVIDED", StringComparison.OrdinalIgnoreCase)) continue;
            result.TryAdd(matches[i].Groups["tag"].Value, value);
        }
        return result;
    }

    /// <summary>The value of one tag (e.g. "MREF"), or null.</summary>
    public static string? Get(string? text, string tag) => Parse(text).GetValueOrDefault(tag);
}
