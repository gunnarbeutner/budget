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

    /// <summary>Shown (instead of the page) while the bank waits after the form was submitted, e.g. "Please approve in the app".</summary>
    public string? WaitingMessage { get; init; }

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

/// <summary>
/// The bank's sign-in form: CSS selectors of its fields and the values to type. The values are never logged or shown
/// (<see cref="ToString"/> leaves them out).
/// </summary>
public sealed class WebLoginAutofill(string username, string password, string usernameSelector, string passwordSelector, string submitSelector)
{
    public string Username { get; } = username;

    public string Password { get; } = password;

    public string UsernameSelector { get; } = usernameSelector;

    public string PasswordSelector { get; } = passwordSelector;

    public string SubmitSelector { get; } = submitSelector;

    public override string ToString() => $"Autofill({UsernameSelector}, {PasswordSelector}, {SubmitSelector})";
}
