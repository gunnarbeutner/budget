using System.Globalization;
using System.Text;

namespace OpenBudget.Services;

/// <summary>
/// Folding for user-facing search: lower case (invariant), umlauts spelled out (ä→ae, ö→oe, ü→ue, ß→ss) and other
/// diacritics stripped (é→e). Folding both sides makes "Nürnberg", "Nuernberg" and "NUERNBERG" match each other, and
/// "strasse" match "Straße". The same fold runs inside SQLite as <c>ob_fold</c> (see <see cref="SqlFunction"/>).
/// </summary>
public static class SearchText
{
    /// <summary>Name of the SQLite function that applies <see cref="Fold"/>.</summary>
    public const string SqlFunction = "ob_fold";

    public static string Fold(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length + 4);
        foreach (var ch in text.Normalize(NormalizationForm.FormD))
        {
            if (ch is 'ß' or 'ẞ')
            {
                sb.Append("ss");
                continue;
            }
            if (ch == '̈')
            {
                // A combining diaeresis after a, o or u (a decomposed ä, ö or ü) spells out as the trailing "e".
                if (sb.Length > 0 && sb[^1] is 'a' or 'o' or 'u') sb.Append('e');
                continue;
            }
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString();
    }

    /// <summary>True when the folded, trimmed <paramref name="query"/> occurs in the folded <paramref name="haystack"/>; an empty query matches everything.</summary>
    public static bool Matches(string? haystack, string? query)
    {
        var q = Fold(query?.Trim());
        return q.Length == 0 || Fold(haystack).Contains(q, StringComparison.Ordinal);
    }
}
