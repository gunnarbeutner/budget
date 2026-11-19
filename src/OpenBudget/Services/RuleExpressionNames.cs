using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenBudget.Services;

/// <summary>
/// The readable form of a rule expression for the text field: a stored <c>account_id=22</c> shows as
/// <c>account:"PayPal"</c> and <c>account_group=5</c> as <c>account_group:"Family"</c>. Rules keep the ids, so renaming
/// an account does not break them. A name that several accounts (or groups) share stays an id.
/// </summary>
public static partial class RuleExpressionNames
{
    [GeneratedRegex("""\G(?<f>account_id|account_group)\s*(?<op>!=|==|=|:)\s*(?<v>\d+)\b""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex StoredTerm();

    [GeneratedRegex("""\G(?<f>account|account_group)\s*(?<op>!=|==|=|:)\s*"(?<v>[^"]*)" """, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.IgnorePatternWhitespace, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NamedTerm();

    /// <summary>The stored expression with unique account and group names in place of their ids.</summary>
    public static string ToDisplay(string? expression, IReadOnlyCollection<RestrictionTarget> targets)
    {
        if (string.IsNullOrEmpty(expression)) return expression ?? "";
        return Rewrite(expression, StoredTerm(), m =>
        {
            var isGroup = m.Groups["f"].Value.Equals("account_group", StringComparison.OrdinalIgnoreCase);
            var id = long.Parse(m.Groups["v"].Value, CultureInfo.InvariantCulture);
            var target = targets.FirstOrDefault(t => t.IsGroup == isGroup && t.Id == id);
            if (target == null || target.Name.Contains('"', StringComparison.Ordinal) || !Unique(targets, isGroup, target.Name)) return null;
            var op = m.Groups["op"].Value == "!=" ? "!=" : ":";
            return (isGroup ? "account_group" : "account") + op + "\"" + target.Name + "\"";
        }, out _);
    }

    /// <summary>The expression to store for what was typed: names back to ids. <paramref name="error"/> names an
    /// unknown or ambiguous name; the text is then returned unchanged.</summary>
    public static string FromDisplay(string? text, IReadOnlyCollection<RestrictionTarget> targets, out string? error)
    {
        if (string.IsNullOrEmpty(text)) { error = null; return text ?? ""; }
        var result = Rewrite(text, NamedTerm(), m =>
        {
            var isGroup = m.Groups["f"].Value.Equals("account_group", StringComparison.OrdinalIgnoreCase);
            var name = m.Groups["v"].Value;
            var matches = targets.Where(t => t.IsGroup == isGroup && t.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count != 1) return "\u0000" + name;
            var op = m.Groups["op"].Value == "!=" ? "!=" : "=";
            return (isGroup ? "account_group" : "account_id") + op + matches[0].Id.ToString(CultureInfo.InvariantCulture);
        }, out var failed);
        error = failed == null ? null : $"no single account or group named \"{failed}\"";
        return failed == null ? result : text;
    }

    private static bool Unique(IReadOnlyCollection<RestrictionTarget> targets, bool isGroup, string name) =>
        targets.Count(t => t.IsGroup == isGroup && t.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) == 1;

    // Replaces the terms outside quoted values and regexes; a replacement starting with \0 marks a failed name.
    private static string Rewrite(string s, Regex term, Func<Match, string?> replace, out string? failed)
    {
        failed = null;
        var sb = new StringBuilder(s.Length);
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            var atTokenStart = i == 0 || char.IsWhiteSpace(s[i - 1]) || s[i - 1] is '(' or ':' or '=';
            if (c == '"' || (c == '/' && atTokenStart))
            {
                var end = i + 1;
                while (end < s.Length && (s[end] != c || s[end - 1] == '\\')) end++;
                end = Math.Min(end + 1, s.Length);
                sb.Append(s, i, end - i);
                i = end;
                continue;
            }
            if (atTokenStart && term.Match(s, i) is { Success: true } m && m.Index == i && replace(m) is { } r)
            {
                if (r.Length > 0 && r[0] == '\u0000') { failed ??= r[1..]; sb.Append(m.Value); }
                else sb.Append(r);
                i += m.Length;
                continue;
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }
}
