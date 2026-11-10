using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenBudget.Services;

/// <summary>A node of a parsed rule expression; <see cref="Print"/> gives its canonical text.</summary>
public interface IRuleNode
{
    string Print();
}

public sealed record OrNode(IReadOnlyList<IRuleNode> Items) : IRuleNode
{
    public string Print() => string.Join(" OR ", Items.Select(i => i.Print()));
}

public sealed record AndNode(IReadOnlyList<IRuleNode> Items) : IRuleNode
{
    public string Print() => string.Join(" AND ", Items.Select(i => i is OrNode ? "(" + i.Print() + ")" : i.Print()));
}

public sealed record NotNode(IRuleNode Item) : IRuleNode
{
    public string Print() => "NOT " + (Item is OrNode or AndNode ? "(" + Item.Print() + ")" : Item.Print());
}

public enum TermKind { Word, Phrase, Regex }

/// <summary>
/// A text term. <paramref name="Field"/> null = the default fields; a field starting with '?' is unknown (never
/// matches). <paramref name="Op"/> is ':' / '=' / '==' (contains) or '!=' (does not contain).
/// </summary>
public sealed record TextTerm(string? Field, string Op, TermKind Kind, string Value) : IRuleNode
{
    public string Print()
    {
        var v = Kind switch
        {
            TermKind.Phrase => "\"" + Value + "\"",
            TermKind.Regex => "/" + Value + "/",
            TermKind.Word => Value,
            _ => Value,
        };
        return Field == null ? v : Field + Op + v;
    }
}

/// <summary><c>amount</c> compared with <paramref name="Op"/> (= != &lt; &gt; &lt;= &gt;=), signed, in currency units.</summary>
public sealed record AmountTerm(string Op, decimal Value) : IRuleNode
{
    public string Print() => "amount" + Op + RuleExpression.FormatAmount(Value);
}

/// <summary><c>account_id=N</c>: restricts to one of our accounts (for accounts without IBAN).</summary>
public sealed record AccountTerm(string Op, long Value) : IRuleNode
{
    public string Print() => "account_id" + Op + Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>A rule expression that cannot be parsed.</summary>
public sealed class ParseException : Exception
{
    public ParseException()
    {
    }

    public ParseException(string message) : base(message)
    {
    }

    public ParseException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>
/// The rule language, a transaction search syntax.
/// <list type="bullet">
/// <item>Terms: bare words (<c>Bakery</c>, <c>xyz/123</c>), quoted phrases (<c>"Example Market"</c>), regular
/// expressions between slashes (<c>/REF [0-9]{4}/</c>). <c>*</c> and <c>?</c> are wildcards in words and phrases.
/// Text matching is a case-insensitive "contains"; umlauts and ß match their transliterations (ä = ae, ß = ss).</item>
/// <item>Operators (upper case): <c>NOT</c> binds tighter than <c>AND</c>, which binds tighter than <c>OR</c>; terms
/// next to each other are AND-ed; parentheses group.</item>
/// <item>Fields: <c>field:value</c>, <c>field="value"</c>, <c>field!=value</c> (does not contain). Text fields:
/// name, purpose, comment, booking_text, currency, reference, end_to_end, mandate, creditor_id, local_account (our
/// account's IBAN), remote_account (the counterparty's IBAN); account fields ignore spaces. <c>account_id=N</c>: our account.</item>
/// <item><c>amount</c> with <c>= == != &lt; &gt; &lt;= &gt;=</c> (or <c>:</c> for =), signed, decimal comma or point.</item>
/// <item>Without a field a term is looked for in name, purpose, comment, note and booking text.</item>
/// </list>
/// <c>end_to_end</c>, <c>mandate</c> and <c>creditor_id</c> look at the stored SEPA end-to-end reference, mandate
/// reference and creditor id; for a booking without the field they fall back to the purpose (where SEPA puts EREF /
/// MREF / CRED). <c>reference</c> looks at the bank reference and the end-to-end reference (the purpose when both are
/// empty). A field that is not known (e.g. <c>category</c>) never matches and is logged once.
/// </summary>
public static class RuleExpression
{
    public static readonly IReadOnlyList<string> TextFields =
        ["name", "purpose", "comment", "booking_text", "currency", "reference", "end_to_end", "mandate", "creditor_id", "local_account", "remote_account"];

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly ConcurrentDictionary<string, IRuleNode?> Cache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, byte> LoggedUnknown = new(StringComparer.Ordinal);

    /// <summary>Set at startup; unknown fields are reported once each.</summary>
    public static Action<string>? UnknownFieldLog { get; set; }

    public static string FormatAmount(decimal value) => value.ToString(decimal.Truncate(value) == value ? "0" : "0.00", CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------- parsing

    /// <summary>Parses an expression; null when it is empty or invalid (<paramref name="error"/> says why).</summary>
    public static IRuleNode? TryParse(string? text, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "empty";
            return null;
        }
        try
        {
            return new Parser(text).ParseAll();
        }
        catch (ParseException ex)
        {
            error = ex.Message;
            return null;
        }
    }

    public static bool IsValid(string? text) => TryParse(text, out _) != null;

    private static IRuleNode? Cached(string text)
    {
        if (Cache.TryGetValue(text, out var node)) return node;
        node = TryParse(text, out _);
        if (Cache.Count > 5000) Cache.Clear();
        Cache[text] = node;
        return node;
    }

    private enum Tok { LParen, RParen, And, Or, Not, Term }

    private readonly record struct Token(Tok Kind, IRuleNode? Term);

    private sealed class Parser
    {
        private static readonly Regex FieldPrefix = new(@"\G(?<f>[A-Za-z_]+)(?<op>==|!=|<=|>=|:|=|<|>)",
            RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, TimeSpan.FromMilliseconds(100));

        private readonly List<Token> _tokens;
        private int _i;

        public Parser(string text) => _tokens = Tokenize(text);

        public IRuleNode ParseAll()
        {
            if (_tokens.Count == 0) throw new ParseException("empty");
            var node = ParseOr();
            if (_i < _tokens.Count) throw new ParseException(_tokens[_i].Kind == Tok.RParen ? "unbalanced ')'" : "unexpected token");
            return node;
        }

        private Token? Peek => _i < _tokens.Count ? _tokens[_i] : null;

        private IRuleNode ParseOr()
        {
            var items = new List<IRuleNode> { ParseAnd() };
            while (Peek is { Kind: Tok.Or })
            {
                _i++;
                items.Add(ParseAnd());
            }
            return items.Count == 1 ? items[0] : new OrNode(items.SelectMany(n => n is OrNode o ? o.Items : [n]).ToList());
        }

        private IRuleNode ParseAnd()
        {
            var items = new List<IRuleNode> { ParseUnary() };
            while (Peek is { } t && t.Kind is Tok.And or Tok.Not or Tok.LParen or Tok.Term)
            {
                if (t.Kind == Tok.And) _i++;
                items.Add(ParseUnary());
            }
            return items.Count == 1 ? items[0] : new AndNode(items.SelectMany(n => n is AndNode a ? a.Items : [n]).ToList());
        }

        private IRuleNode ParseUnary()
        {
            var t = Peek ?? throw new ParseException("expression ends too early");
            switch (t.Kind)
            {
                case Tok.Not:
                    _i++;
                    return new NotNode(ParseUnary());
                case Tok.LParen:
                    _i++;
                    var inner = ParseOr();
                    if (Peek is not { Kind: Tok.RParen }) throw new ParseException("missing ')'");
                    _i++;
                    return inner;
                case Tok.Term:
                    _i++;
                    return t.Term!;
                case Tok.RParen:
                case Tok.And:
                case Tok.Or:
                default:
                    throw new ParseException("unexpected " + t.Kind.ToString().ToUpperInvariant());
            }
        }

        private static List<Token> Tokenize(string s)
        {
            var tokens = new List<Token>();
            var i = 0;
            while (i < s.Length)
            {
                var c = s[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }
                if (c == '(') { tokens.Add(new Token(Tok.LParen, null)); i++; continue; }
                if (c == ')') { tokens.Add(new Token(Tok.RParen, null)); i++; continue; }
                if (c is '"' or '/')
                {
                    tokens.Add(new Token(Tok.Term, ReadValue(s, ref i, null, ":")));
                    continue;
                }
                // A bare word, possibly a field prefix followed by a quoted / regex / bare value.
                var m = FieldPrefix.Match(s, i);
                if (m.Success)
                {
                    var raw = m.Groups["f"].Value;
                    var op = m.Groups["op"].Value;
                    var field = raw.Equals("amount", StringComparison.OrdinalIgnoreCase) ? "amount"
                        : raw.Equals("account_id", StringComparison.OrdinalIgnoreCase) ? "account_id"
                        : TextFields.FirstOrDefault(f => f.Equals(raw, StringComparison.OrdinalIgnoreCase));
                    var after = i + m.Length;
                    var quotedNext = after < s.Length && s[after] is '"' or '/';
                    if (field != null)
                    {
                        i = after;
                        tokens.Add(new Token(Tok.Term, FieldTerm(field, op, s, ref i)));
                        continue;
                    }
                    if (quotedNext)
                    {
                        i = after;
                        _ = ReadValue(s, ref i, null, op);
                        tokens.Add(new Token(Tok.Term, new TextTerm("?" + raw, op, TermKind.Phrase, "")));
                        continue;
                    }
                }
                var word = ReadWord(s, ref i);
                tokens.Add(word switch
                {
                    "AND" => new Token(Tok.And, null),
                    "OR" => new Token(Tok.Or, null),
                    "NOT" => new Token(Tok.Not, null),
                    _ => new Token(Tok.Term, new TextTerm(null, ":", TermKind.Word, word)),
                });
            }
            return tokens;
        }

        private static IRuleNode FieldTerm(string field, string op, string s, ref int i)
        {
            if (field is "amount" or "account_id")
            {
                var raw = i < s.Length && s[i] == '"' ? ReadValue(s, ref i, null, op).Value : ReadWord(s, ref i);
                if (op is ":" or "==") op = "=";
                if (field == "account_id")
                {
                    if (op is not ("=" or "!=") || !long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                        throw new ParseException("account_id needs = and a number");
                    return new AccountTerm(op, id);
                }
                if (!TryParseAmount(raw, out var value)) throw new ParseException("not an amount: " + raw);
                return new AmountTerm(op, value);
            }
            if (op is not (":" or "=" or "==" or "!=")) throw new ParseException(field + " cannot be compared with " + op);
            if (i >= s.Length || char.IsWhiteSpace(s[i]) || s[i] == ')') throw new ParseException(field + " needs a value");
            return ReadValue(s, ref i, field, op);
        }

        private static TextTerm ReadValue(string s, ref int i, string? field, string op)
        {
            if (s[i] == '"')
            {
                var rel = s.AsSpan(i + 1).IndexOf('"');
                var end = rel < 0 ? -1 : i + 1 + rel;
                if (end < 0) throw new ParseException("missing closing quote");
                var v = s[(i + 1)..end];
                i = end + 1;
                return new TextTerm(field, op, TermKind.Phrase, v);
            }
            if (s[i] == '/')
            {
                var end = i + 1;
                while (end < s.Length && (s[end] != '/' || s[end - 1] == '\\')) end++;
                if (end < s.Length && end > i + 1)
                {
                    var pattern = s[(i + 1)..end];
                    try
                    {
                        _ = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
                    }
                    catch (ArgumentException)
                    {
                        throw new ParseException("invalid regular expression");
                    }
                    i = end + 1;
                    return new TextTerm(field, op, TermKind.Regex, pattern);
                }
            }
            return new TextTerm(field, op, TermKind.Word, ReadWord(s, ref i));
        }

        private static string ReadWord(string s, ref int i)
        {
            var start = i;
            while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] is not ('(' or ')' or '"')) i++;
            if (i == start) throw new ParseException("a value is missing");
            return s[start..i];
        }
    }

    public static bool TryParseAmount(string? raw, out decimal value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var t = raw.Trim().Replace("€", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal);
        if (t.Contains(',', StringComparison.Ordinal)) t = t.Replace(".", "", StringComparison.Ordinal).Replace(',', '.');
        return decimal.TryParse(t, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
    }

    // ---------------------------------------------------------------- evaluation

    public static bool Matches(string? expression, RuleSubject t) =>
        !string.IsNullOrWhiteSpace(expression) && Cached(expression) is { } node && Evaluate(node, t);

    public static bool Evaluate(IRuleNode node, RuleSubject t) => node switch
    {
        OrNode o => o.Items.Any(i => Evaluate(i, t)),
        AndNode a => a.Items.All(i => Evaluate(i, t)),
        NotNode n => !Evaluate(n.Item, t),
        AmountTerm a => CompareAmount(t.Amount / 100m, a.Op, a.Value),
        AccountTerm a => (t.AccountId == a.Value) == (a.Op == "="),
        TextTerm x => EvaluateText(x, t),
        _ => false,
    };

    private static bool CompareAmount(decimal actual, string op, decimal value) => op switch
    {
        "=" => actual == value,
        "!=" => actual != value,
        "<" => actual < value,
        ">" => actual > value,
        "<=" => actual <= value,
        ">=" => actual >= value,
        _ => false,
    };

    private static bool EvaluateText(TextTerm x, RuleSubject t)
    {
        if (x.Field is { } f && f.Length > 0 && f[0] == '?')
        {
            if (LoggedUnknown.TryAdd(f, 0)) UnknownFieldLog?.Invoke(f[1..]);
            return false;
        }
        var account = x.Field is "local_account" or "remote_account";
        IEnumerable<string?> haystacks = x.Field switch
        {
            null => [t.Name, t.Purpose, t.Comment, t.Note, t.BookingText],
            "name" => [t.Name],
            "purpose" => [t.Purpose],
            "comment" => [t.Comment, t.Note],
            "booking_text" => [t.BookingText],
            "currency" => [t.Currency],
            "reference" => string.IsNullOrEmpty(t.BankReference) && string.IsNullOrEmpty(t.EndToEndReference)
                ? [t.Purpose] : [t.BankReference, t.EndToEndReference],
            "end_to_end" => [OrPurpose(t.EndToEndReference, t)],
            "mandate" => [OrPurpose(t.MandateReference, t)],
            "creditor_id" => [OrPurpose(t.CreditorId, t)],
            "local_account" => [t.LocalAccount],
            "remote_account" => [t.CounterpartyIban],
            _ => [],
        };
        var found = haystacks.Any(h => TextMatches(x, h, account));
        return x.Op == "!=" ? !found : found;
    }

    private static string? OrPurpose(string? field, RuleSubject t) => string.IsNullOrEmpty(field) ? t.Purpose : field;

    private static bool TextMatches(TextTerm x, string? haystack, bool account)
    {
        if (string.IsNullOrEmpty(haystack)) return false;
        try
        {
            if (x.Kind == TermKind.Regex)
                return Regex.IsMatch(haystack, x.Value, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout)
                    || Regex.IsMatch(Fold(haystack), x.Value, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
            var needle = Fold(x.Value);
            var hay = Fold(haystack);
            if (account)
            {
                needle = needle.Replace(" ", "", StringComparison.Ordinal);
                hay = hay.Replace(" ", "", StringComparison.Ordinal);
            }
            if (needle.Length == 0) return false;
            if (needle.Contains('*', StringComparison.Ordinal) || needle.Contains('?', StringComparison.Ordinal))
            {
                var pattern = Regex.Escape(needle).Replace(@"\*", @"\S*", StringComparison.Ordinal).Replace(@"\?", @"\S", StringComparison.Ordinal);
                return Regex.IsMatch(hay, pattern, RegexOptions.CultureInvariant, RegexTimeout);
            }
            return hay.Contains(needle, StringComparison.Ordinal);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>Upper case, umlauts and ß transliterated, whitespace runs collapsed (for comparing text).</summary>
    public static string Fold(string text)
    {
        var sb = new StringBuilder(text.Length + 8);
        var space = false;
        foreach (var ch in text.ToUpperInvariant())
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!space) sb.Append(' ');
                space = true;
                continue;
            }
            space = false;
            switch (ch)
            {
                case 'Ä': sb.Append("AE"); break;
                case 'Ö': sb.Append("OE"); break;
                case 'Ü': sb.Append("UE"); break;
                case 'ß' or 'ẞ': sb.Append("SS"); break;
                default: sb.Append(ch); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>Whether the expression restricts to our account <paramref name="accountId"/> (account_id=N).</summary>
    public static bool ReferencesAccount(string? expression, long accountId) =>
        TryParse(expression, out _) is { } node && Walk(node).Any(n => n is AccountTerm { Op: "=" } a && a.Value == accountId);

    private static IEnumerable<IRuleNode> Walk(IRuleNode node)
    {
        yield return node;
        IReadOnlyList<IRuleNode> children = node switch
        {
            OrNode o => o.Items,
            AndNode a => a.Items,
            NotNode n => [n.Item],
            _ => [],
        };
        foreach (var c in children)
            foreach (var d in Walk(c)) yield return d;
    }
}
