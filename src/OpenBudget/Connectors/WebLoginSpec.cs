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

    /// <summary>
    /// An in-page check (a JavaScript function returning <c>{ ok, reason }</c>) that must also pass before the login counts
    /// as done, for sites whose landing page appears before a second factor is entered. While it fails on the landing
    /// page, the page is shown to the user.
    /// </summary>
    public string? SuccessCheck { get; init; }

    /// <summary>
    /// Keeps the browser open and signed in after the login succeeded and hands it to the connector
    /// (<see cref="ChallengeAnswer.Browser"/>), for sites whose session only works from the browser it was signed in with.
    /// </summary>
    public bool KeepBrowser { get; init; }

    /// <summary>
    /// A signed-in page tried first when the browser starts with stored cookies: when the success condition (including
    /// <see cref="SuccessCheck"/>) holds there, the login is done without the sign-in form; otherwise it goes on at
    /// <see cref="StartUrl"/>.
    /// </summary>
    public Uri? ResumeUrl { get; init; }

    /// <summary>
    /// Interstitials to click away before filling in the form (e.g. a "browser not supported" notice), as selectors;
    /// Puppeteer's text selectors such as <c>button::-p-text(…)</c> work too. Clicked only when visible.
    /// </summary>
    public IReadOnlyList<string> Dismiss { get; init; } = [];

    /// <summary>
    /// The bank's "wrong username or password" message on the sign-in page: a CSS selector and, optionally, text the element
    /// must contain. When it shows up after the app submitted the form, the web login ends at once with
    /// <see cref="RejectedMessage"/> and the credentials count as rejected (instead of waiting for an approval that never comes).
    /// </summary>
    public WebLoginError? CredentialsError { get; init; }

    /// <summary>
    /// URL fragments to observe throughout sign-in, including security checks: HTTP status, network failures and a short
    /// top-level error code are logged; never query strings, bodies or tokens.
    /// </summary>
    public IReadOnlyList<string> ObservedRequests { get; init; } = [];

    /// <summary>
    /// A JavaScript function run in the page at every poll while the sign-in is not done yet (after the form was
    /// submitted, or while the page is shown), with <see cref="WaitingArgument"/> as its argument, for steps between the
    /// sign-in form and the landing page (a device choice, say). It returns <c>{ ok, reason }</c>: ok with an empty reason
    /// means nothing to do; ok with a reason means the script acted (the reason is logged, and a page the script had
    /// shown is hidden again); ok with a reason starting with "waiting:" means the bank waits for a confirmation elsewhere
    /// (an app), so the page is hidden whichever way it was shown; not ok means the page needs the user (it is shown, the reason is logged); not ok with a
    /// reason starting with "end:" ends the sign-in as failed, the rest of the reason being the message. Reasons are logged:
    /// the script's own words with counts and the page's structure, never field values.
    /// </summary>
    public string? WaitingScript { get; init; }

    /// <summary>A recognized bank code form that can use the shared code prompt while the browser stays signed in.</summary>
    public WebLoginCodePrompt? CodePrompt { get; init; }

    /// <summary>The argument of <see cref="WaitingScript"/> (a remembered choice, say); never logged.</summary>
    public string WaitingArgument { get; init; } = "";

    /// <summary>
    /// When the sign-in fails (timed out, cancelled, ended by <see cref="WaitingScript"/>, or no browser on this server),
    /// the connector gets an answer with <see cref="ChallengeAnswer.Failure"/> instead of the step failing, so it can carry
    /// on without its web part. Rejected credentials still fail the step.
    /// </summary>
    public bool ReportFailure { get; init; }

    /// <summary>
    /// An address prefix (an app's callback, such as <c>com.example.app://callback</c>) that ends the sign-in as soon as the
    /// page is sent there: the full address goes to the connector (<see cref="ChallengeAnswer.CallbackUrl"/>) and is never
    /// logged. For OAuth sign-ins whose redirect target is not a web page.
    /// </summary>
    public string? CaptureRedirect { get; init; }

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

/// <summary>A code field (or one field per digit) and its submit button on a known security-check path.</summary>
public sealed record WebLoginCodePrompt(string PathPrefix, string FieldSelector, string SubmitSelector, string Message, int Length = 6, bool Numeric = true)
{
    /// <summary>
    /// The login's authenticator-app secret: the form gets the current TOTP code and is submitted without asking. When the
    /// form is shown again afterwards (the code was rejected), the code of the adjacent time window is tried once; after
    /// that the shared code prompt takes over with <see cref="TotpRejectedMessage"/>. Never logged.
    /// </summary>
    public TotpSecret? Totp { get; init; }

    /// <summary>
    /// The site's "use the authenticator app" choice on a security-check page that offers several methods (selectors,
    /// Puppeteer text selectors included): with a <see cref="Totp"/> secret, the first visible one is clicked once, followed
    /// by <see cref="SubmitSelector"/> when visible.
    /// </summary>
    public IReadOnlyList<string> ChooseAuthenticator { get; init; } = [];

    /// <summary>Put before <see cref="Message"/> when the stored secret's codes were not accepted.</summary>
    public string TotpRejectedMessage { get; init; } = "The stored TOTP secret was not accepted.";

    /// <summary>How long after submitting a TOTP code the form may still show before it counts as rejected.</summary>
    public TimeSpan TotpSettle { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Checkboxes ticked (when shown) before the code is submitted, such as "don't ask again on this browser".</summary>
    public IReadOnlyList<string> Tick { get; init; } = [];
}

/// <summary>How a sign-in page shows that it rejected the credentials (see <see cref="WebLoginSpec.CredentialsError"/>).</summary>
/// <param name="Selector">Candidate elements (e.g. "[role=alert]").</param>
/// <param name="Texts">The element must contain one of these (case-insensitive); empty: any visible match counts.</param>
/// <param name="Message">What the user is told.</param>
public sealed record WebLoginError(string Selector, IReadOnlyList<string> Texts, string Message);

/// <summary>
/// A check of a filled sign-in step before it is submitted. Both are JavaScript functions run in the page with the
/// credential's value as their single argument and must return <c>{ ok: boolean, reason: string }</c>; the comparison
/// happens in the page and the reason must never contain the value (only counts and lengths). When <see cref="Check"/>
/// fails, <see cref="Repair"/> (if any) gets one chance to put the value into the page's state, then the check runs
/// again; still failing, the step is not submitted and the user is shown the page.
/// </summary>
public sealed record WebLoginVerify(string Check, string? Repair = null, WebLoginCredential Credential = WebLoginCredential.Secret);

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
    /// <summary>A field the page may not show (a PIN asked on the same page or on the next one): skipped when it does not appear within a moment.</summary>
    public bool Optional { get; init; }

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
    /// <summary>Checked after the fields were filled and before the submit button is clicked (see <see cref="WebLoginVerify"/>).</summary>
    public WebLoginVerify? Verify { get; init; }

    /// <summary>Skipped when earlier steps already entered every credential this one asks for (an optional field found there).</summary>
    public bool SkipWhenEntered { get; init; }

    /// <summary>When no enabled submit button matches, pressing Enter in the last filled field submits the step instead.</summary>
    public bool EnterSubmits { get; init; }

    /// <summary>Checkboxes ticked (when shown) before the step is submitted, such as "keep me signed in".</summary>
    public IReadOnlyList<string> Tick { get; init; } = [];

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
