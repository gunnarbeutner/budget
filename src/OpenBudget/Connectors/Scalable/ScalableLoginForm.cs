using System.Net;
using System.Text.RegularExpressions;

namespace OpenBudget.Connectors.Scalable;

/// <summary>The sign-in form of Scalable's Auth0 Universal Login page: where to post and what to send.</summary>
internal sealed partial class ScalableLoginForm
{
    private ScalableLoginForm(Uri action, List<KeyValuePair<string, string>> fixedFields, string? usernameField, string? passwordField)
    {
        Action = action;
        FixedFields = fixedFields;
        UsernameField = usernameField;
        PasswordField = passwordField;
    }

    public Uri Action { get; }

    /// <summary>Hidden fields (state, ...) and the submit button, to be sent as they are.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> FixedFields { get; }

    public string? UsernameField { get; }

    public string? PasswordField { get; }

    /// <summary>Everything to post: the fixed fields plus whichever of username and password this page asks for.</summary>
    public List<KeyValuePair<string, string>> Fill(string username, string password)
    {
        var fields = new List<KeyValuePair<string, string>>(FixedFields);
        if (UsernameField != null) fields.Add(new(UsernameField, username));
        if (PasswordField != null) fields.Add(new(PasswordField, password));
        return fields;
    }

    /// <summary>Finds the credentials form in <paramref name="html"/>; null when the page has none.</summary>
    public static ScalableLoginForm? Parse(string html, Uri pageUri)
    {
        foreach (Match form in FormPattern().Matches(html))
        {
            var inputs = InputPattern().Matches(form.Groups["body"].Value).Select(m => Attributes(m.Value)).ToList();
            var password = inputs.FirstOrDefault(a => IsType(a, "password") && a.ContainsKey("name"));
            var username = inputs.FirstOrDefault(a => (IsType(a, "text") || IsType(a, "email") || !a.ContainsKey("type")) && a.ContainsKey("name") && !IsHidden(a));
            if (password == null && username == null) continue;

            var fixedFields = inputs
                .Where(a => IsHidden(a) && a.ContainsKey("name"))
                .Select(a => new KeyValuePair<string, string>(a["name"], a.GetValueOrDefault("value", "")))
                .ToList();
            var button = ButtonPattern().Matches(form.Groups["body"].Value).Select(m => Attributes(m.Value))
                .Where(a => a.ContainsKey("name") && (!a.ContainsKey("type") || IsType(a, "submit")))
                .OrderBy(a => string.Equals(a.GetValueOrDefault("value"), "default", StringComparison.Ordinal) ? 0 : 1)
                .FirstOrDefault();
            if (button != null) fixedFields.Add(new(button["name"], button.GetValueOrDefault("value", "")));

            var attributes = Attributes(form.Groups["open"].Value);
            var action = attributes.GetValueOrDefault("action");
            var actionUri = string.IsNullOrEmpty(action) ? pageUri : new Uri(pageUri, action);
            return new ScalableLoginForm(actionUri, fixedFields, username?["name"], password?["name"]);
        }
        return null;
    }

    private static bool IsType(Dictionary<string, string> a, string type) =>
        string.Equals(a.GetValueOrDefault("type"), type, StringComparison.OrdinalIgnoreCase);

    private static bool IsHidden(Dictionary<string, string> a) => IsType(a, "hidden");

    private static Dictionary<string, string> Attributes(string tag)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in AttributePattern().Matches(tag))
        {
            var value = m.Groups["dq"].Success ? m.Groups["dq"].Value : m.Groups["sq"].Success ? m.Groups["sq"].Value : m.Groups["bare"].Value;
            result.TryAdd(m.Groups["name"].Value, WebUtility.HtmlDecode(value));
        }
        return result;
    }

    private const int TimeoutMs = 2000;

    [GeneratedRegex("(?<open><form\\b[^>]*>)(?<body>.*?)</form>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex FormPattern();

    [GeneratedRegex("<input\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex InputPattern();

    [GeneratedRegex("<button\\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex ButtonPattern();

    [GeneratedRegex("(?<name>[a-zA-Z_:][-a-zA-Z0-9_:.]*)\\s*=\\s*(?:\"(?<dq>[^\"]*)\"|'(?<sq>[^']*)'|(?<bare>[^\\s>]+))", RegexOptions.CultureInvariant, TimeoutMs)]
    private static partial Regex AttributePattern();
}
