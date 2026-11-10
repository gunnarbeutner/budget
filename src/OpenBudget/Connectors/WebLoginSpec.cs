namespace OpenBudget.Connectors;

/// <summary>
/// What a connector asks for when the user has to sign in on the bank's own web page (a <see cref="ChallengeKind.WebLogin"/>
/// challenge): where to start, when the sign-in counts as done, and which cookies the connector needs afterwards. The
/// app opens an isolated browser seeded with the login's stored cookies for those domains, lets the user sign in
/// (2FA included), then copies all cookies of those domains back into the login's cookie jar.
/// </summary>
public sealed class WebLoginSpec
{
    public WebLoginSpec(
        Uri startUrl, IReadOnlyList<string> cookieDomains, Uri? successUrlPrefix = null,
        Func<Uri, IReadOnlyList<CookieState>, bool>? successWhen = null)
    {
        // https only (a bank page); plain http is accepted for the machine's own loopback address, which tests use.
        if (!startUrl.IsAbsoluteUri || !(startUrl.Scheme == Uri.UriSchemeHttps || (startUrl.Scheme == Uri.UriSchemeHttp && startUrl.IsLoopback)))
            throw new ArgumentException("The start URL must be an absolute https URL.", nameof(startUrl));
        if (cookieDomains.Count == 0 || cookieDomains.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("At least one cookie domain is required.", nameof(cookieDomains));
        if (successUrlPrefix == null && successWhen == null)
            throw new ArgumentException("A success condition (URL prefix and/or predicate) is required.", nameof(successUrlPrefix));
        StartUrl = startUrl;
        CookieDomains = cookieDomains.Select(d => d.Trim().TrimStart('.')).ToList();
        SuccessUrlPrefix = successUrlPrefix;
        SuccessWhen = successWhen;
    }

    public Uri StartUrl { get; }

    /// <summary>When set, the app fills in and submits the bank's sign-in form itself; the user only sees the page if it gets stuck.</summary>
    public WebLoginAutofill? Autofill { get; init; }


    /// <summary>Cookies of these domains (and their sub-/parent domains' cookies that reach them) travel between the jar and the browser.</summary>
    public IReadOnlyList<string> CookieDomains { get; }

    /// <summary>The sign-in is done once the page URL starts with this (case-insensitive).</summary>
    public Uri? SuccessUrlPrefix { get; }

    /// <summary>Extra condition on the page URL and the browser's cookies; when both conditions are set, both must hold.</summary>
    public Func<Uri, IReadOnlyList<CookieState>, bool>? SuccessWhen { get; }

    /// <summary>True when a cookie belongs to one of <see cref="CookieDomains"/> (a cookie for ".scalable.capital" reaches "de.scalable.capital" and vice versa).</summary>
    public bool Matches(CookieState cookie)
    {
        var d = cookie.Domain.TrimStart('.');
        return CookieDomains.Any(s => string.Equals(s, d, StringComparison.OrdinalIgnoreCase)
            || s.EndsWith("." + d, StringComparison.OrdinalIgnoreCase) || d.EndsWith("." + s, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True when the page URL satisfies the URL-prefix condition (or none is set).</summary>
    public bool UrlMatches(Uri url) => SuccessUrlPrefix == null || url.ToString().StartsWith(SuccessUrlPrefix.ToString(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the sign-in is complete; <paramref name="cookies"/> (the browser's cookies) is only needed when a predicate is set.</summary>
    public bool IsSuccess(Uri url, IReadOnlyList<CookieState> cookies)
    {
        if (!UrlMatches(url)) return false;
        if (SuccessWhen == null) return true;
        return SuccessWhen(url, cookies);
    }
}

/// <summary>Which stored credential a sign-in field receives.</summary>
public enum WebLoginCredential
{
    Username,
    Secret,
}

/// <summary>
/// One field of a sign-in step: a CSS selector and the credential typed into it. With <paramref name="CharacterBoxes"/>
/// &gt; 0 the credential goes one character per box (an access code split over single-digit inputs):
/// <paramref name="Selector"/> is then a format string whose {0} is the box index (0-based), and a credential of any other
/// length is not entered at all.
/// </summary>
public sealed record WebLoginField(string Selector, WebLoginCredential Credential, int CharacterBoxes = 0)
{
    /// <summary>The selector of each box (or just <see cref="Selector"/> for a plain field).</summary>
    public IReadOnlyList<string> BoxSelectors() =>
        CharacterBoxes > 0
            ? Enumerable.Range(0, CharacterBoxes).Select(i => Selector.Replace("{0}", i.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)).ToList()
            : [Selector];
}

/// <summary>
/// One page of a sign-in form: the fields to fill, the button that submits them and, optionally, an element to wait for
/// before filling (the step's first field is always waited for).
/// </summary>
public sealed record WebLoginStep(IReadOnlyList<WebLoginField> Fields, string SubmitSelector, string? WaitFor = null)
{
    public WebLoginStep(string fieldSelector, WebLoginCredential credential, string submitSelector, string? waitFor = null)
        : this([new WebLoginField(fieldSelector, credential)], submitSelector, waitFor)
    {
    }
}

/// <summary>
/// The bank's sign-in form: an ordered list of steps (one page with username and password, or e.g. an e-mail page
/// followed by a password page) and the values to type. The values are never logged or shown (<see cref="ToString"/>
/// leaves them out).
/// </summary>
public sealed class WebLoginAutofill
{
    /// <summary>A single page with a username field, a password field and a submit button.</summary>
    public WebLoginAutofill(string username, string password, string usernameSelector, string passwordSelector, string submitSelector)
        : this(username, password, [new WebLoginStep(
            [new WebLoginField(usernameSelector, WebLoginCredential.Username), new WebLoginField(passwordSelector, WebLoginCredential.Secret)],
            submitSelector)])
    {
    }

    public WebLoginAutofill(string username, string password, IReadOnlyList<WebLoginStep> steps)
    {
        if (steps.Count == 0 || steps.Any(s => s.Fields.Count == 0))
            throw new ArgumentException("At least one step with at least one field is required.", nameof(steps));
        Username = username;
        Password = password;
        Steps = steps;
    }

    public string Username { get; }

    public string Password { get; }

    public IReadOnlyList<WebLoginStep> Steps { get; }

    /// <summary>The field that receives the username.</summary>
    public string UsernameSelector => Steps.SelectMany(s => s.Fields).First(f => f.Credential == WebLoginCredential.Username).Selector;

    /// <summary>The field that receives the password (still visible after submitting means the sign-in got stuck).</summary>
    public string PasswordSelector => Steps.SelectMany(s => s.Fields).Last(f => f.Credential == WebLoginCredential.Secret).BoxSelectors()[0];

    public string SubmitSelector => Steps[^1].SubmitSelector;

    public string ValueOf(WebLoginCredential credential) => credential == WebLoginCredential.Username ? Username : Password;

    public override string ToString() =>
        "Autofill(" + string.Join(" | ", Steps.Select(s => string.Join(", ", s.Fields.Select(f => f.Selector)) + " -> " + s.SubmitSelector)) + ")";
}
