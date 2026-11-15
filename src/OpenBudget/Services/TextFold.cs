using System.Globalization;
using System.Text;

namespace OpenBudget.Services;

/// <summary>The case <see cref="TextFold.Fold"/> folds to.</summary>
public enum TextCase
{
    Lower,
    Upper,
}

/// <summary>
/// The one text fold for comparing names and text. Umlauts are spelled out (ä→ae, ö→oe, ü→ue, ß→ss) in either case,
/// so "Nürnberg" and "Nuernberg" fold alike. Other accents are stripped (é→e) unless <c>stripAccents</c> is false,
/// which keeps them as they are (user rule patterns are matched against that form). Whitespace runs optionally
/// collapse to one space.
/// </summary>
public static class TextFold
{
    public static string Fold(string? text, TextCase textCase = TextCase.Lower, bool stripAccents = true, bool collapseWhitespace = false)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var upper = textCase == TextCase.Upper;
        var sb = new StringBuilder(text.Length + 8);
        var space = false;
        foreach (var raw in stripAccents ? text.Normalize(NormalizationForm.FormD) : text)
        {
            if (collapseWhitespace && char.IsWhiteSpace(raw))
            {
                if (!space) sb.Append(' ');
                space = true;
                continue;
            }
            space = false;
            var ch = upper ? char.ToUpperInvariant(raw) : char.ToLowerInvariant(raw);
            switch (ch)
            {
                case 'ß' or 'ẞ': sb.Append(upper ? "SS" : "ss"); continue;
                case 'Ä' or 'ä': sb.Append(upper ? "AE" : "ae"); continue;
                case 'Ö' or 'ö': sb.Append(upper ? "OE" : "oe"); continue;
                case 'Ü' or 'ü': sb.Append(upper ? "UE" : "ue"); continue;
                case '̈' when stripAccents:
                    // A combining diaeresis after a, o or u (a decomposed ä, ö or ü) spells out as the trailing "e".
                    if (sb.Length > 0 && sb[^1] is 'a' or 'o' or 'u' or 'A' or 'O' or 'U') sb.Append(upper ? 'E' : 'e');
                    continue;
            }
            if (stripAccents && CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(ch);
        }
        return sb.ToString();
    }
}
