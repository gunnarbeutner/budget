using System.Text.RegularExpressions;
using System.Globalization;
using System.Text.Json.Nodes;

namespace OpenBudget.Connectors.Scalable;

/// <summary>
/// Scalable Capital (broker + Verrechnungskonto) over its web client's GraphQL interface. The sign-in
/// (Auth0 login, captcha, 2FA) is done by the user themselves on Scalable's own page in the app's remote
/// browser (a web login); the cookies that produces are the session. They are persisted, so later syncs
/// skip the sign-in until Scalable ends the session.
/// Every broker portfolio becomes a depot account (positions) plus a cash account (balance,
/// bookings), identified as "&lt;portfolioId&gt;:depot" / "&lt;portfolioId&gt;:cash".
/// </summary>
public sealed partial class ScalableConnector : IBankConnector
{
    public const string TypeName = "scalable";
    internal const string WebLoginReference = "scalable-web-login";
    private const string LoginAtKey = "loginAt";
    private const string LastOkAtKey = "lastOkAt";

    private readonly ConnectorContext _context;
    private readonly ScalableOptions _options;
    private readonly ScalableGraphQl _graphQl;
    private ConnectorSession _session = new();
    private string? _personId;
    private bool _loggedInAtAll;

    public ScalableConnector(ConnectorContext context, ScalableOptions options)
    {
        _context = context;
        _options = options;
        _graphQl = new ScalableGraphQl(context.Http, context.Logger, options.BaseUri);
    }

    public static ConnectorRegistration CreateRegistration(ScalableOptions options) => new(
        TypeName,
        "Scalable Capital",
        "Scalable Capital broker portfolios and their cash account: positions, bookings and mailbox documents. "
        + "No credentials are stored: you sign in on Scalable's own page (shown in a browser window inside OpenBudget, 2FA included) "
        + "whenever the session has expired. Needs the server's browser (Chromium).",
        ctx => new ScalableConnector(ctx, options),
        CredentialRequirement.None);

    public string ConnectorType => TypeName;

    private ILogger Log => _context.Logger;

    private DateTime Now => _context.Clock.GetUtcNow().UtcDateTime;

    // ---- login ----

    /// <summary>The page the user signs in on and what counts as signed in: Scalable's login redirects into the cockpit when done.</summary>
    private WebLoginSpec WebLogin => new(
        new Uri(_options.BaseUri, "auth/login"),
        [_options.BaseUri.Host, "secure.scalable.capital"],
        successUrlPrefix: new Uri(_options.BaseUri, "cockpit/"));

    public async Task<ConnectorStep<ConnectorSession?>> LoginAsync(
        ConnectorCredentials credentials, ConnectorSession? storedSession, ChallengeAnswer? answer, CancellationToken ct)
    {
        _session = storedSession ?? new ConnectorSession();
        _loggedInAtAll = false;
        try
        {
            if (answer == null)
            {
                if (storedSession is { Cookies.Count: > 0 } && await TryReuseSessionAsync(ct))
                    return ConnectorStep.Done<ConnectorSession?>(_session);
                // The app's stale session cookies would send the browser straight to the cockpit, ending the web login
                // before the user can sign in. The sign-in domain's cookies (device id, SSO) stay.
                var appHost = _options.BaseUri.Host;
                _context.Http.RemoveCookies(c => c.Domain.TrimStart('.').Equals(appHost, StringComparison.OrdinalIgnoreCase));
                return ConnectorStep.Ask<ConnectorSession?>(ConnectorChallenge.WebLogin(
                    "Bitte melden Sie sich auf der Scalable-Seite an. / Please sign in on the Scalable page.", WebLogin, WebLoginReference));
            }

            // Back from the web login: the browser's cookies are in the jar now.
            _personId = await FetchPersonIdAsync(ct);
            await ProbeApiAsync(ct);
        }
        catch (ScalableSessionExpiredException ex)
        {
            Log.LogWarning(ex, "Scalable rejected the session: {Reason}", ex.Message);
            throw new ConnectorException("Scalable does not accept the session (UNAUTHENTICATED); the sign-in did not complete.", ex);
        }
        _loggedInAtAll = true;
        _session.Values[LoginAtKey] = Now.ToString("O", CultureInfo.InvariantCulture);
        LogSessionCookies();
        Log.LogInformation("Signed in to Scalable Capital through the web login.");
        return ConnectorStep.Done<ConnectorSession?>(_session);
    }

    /// <summary>True when the stored cookies still authenticate; otherwise logs how long the session lasted.</summary>
    private async Task<bool> TryReuseSessionAsync(CancellationToken ct)
    {
        try
        {
            _personId = await FetchPersonIdAsync(ct);
            await ProbeApiAsync(ct);
        }
        catch (ScalableSessionExpiredException)
        {
            LogSessionLifetime();
            return false;
        }
        Log.LogInformation("Reusing the stored Scalable session (created {Age} ago).", DescribeAge(LoginAtKey));
        _loggedInAtAll = true;
        return true;
    }

    /// <summary>The cockpit page can still render the user after the API token has expired; this asks the API itself.</summary>
    private Task<System.Text.Json.JsonElement> ProbeApiAsync(CancellationToken ct) =>
        _graphQl.ExecuteAsync(ScalableGraphQl.CockpitPath, "fetchCashAccountDetails", ScalableQueries.FetchCashAccountDetails,
            new System.Text.Json.Nodes.JsonObject { ["userId"] = _personId }, ct);

    private void LogSessionLifetime()
    {
        Log.LogInformation(
            "The stored Scalable session has expired: it was created {Created} ago and last confirmed valid {LastOk} ago, so it lasted between those two durations. Logging in again.",
            DescribeAge(LoginAtKey), DescribeAge(LastOkAtKey));
    }

    private string DescribeAge(string key)
    {
        if (!_session.Values.TryGetValue(key, out var text) || !DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at))
            return "an unknown time";
        var age = Now - at.ToUniversalTime();
        return age.TotalHours >= 48 ? $"{age.TotalDays:0.0} days" : age.TotalMinutes >= 120 ? $"{age.TotalHours:0.0} hours" : $"{age.TotalMinutes:0} minutes";
    }

    /// <summary>
    /// Loads the cockpit page with the stored cookies and reads the user id from its server-rendered Next.js data
    /// (<c>props.pageProps.middlewareProps.*.session.user.userId</c>); every GraphQL query needs it. A redirect away
    /// from the cockpit, or a page without a signed-in user, means the session is gone.
    /// </summary>
    private async Task<string> FetchPersonIdAsync(CancellationToken ct)
    {
        var url = new Uri(_options.BaseUri, "cockpit/");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await _context.Http.SendAsync(request, ct);
        var final = response.RequestMessage?.RequestUri ?? url;
        if ((int)response.StatusCode is >= 300 and < 400 || response.StatusCode == System.Net.HttpStatusCode.Unauthorized
            || !IsAppHost(final) || !final.AbsolutePath.StartsWith("/cockpit", StringComparison.Ordinal))
            throw new ScalableSessionExpiredException("The cockpit redirected to the sign-in.");
        if (!response.IsSuccessStatusCode)
            throw new ConnectorException($"Scalable's cockpit answered HTTP {(int)response.StatusCode}.");
        var html = await response.Content.ReadAsStringAsync(ct);
        return ExtractUserId(html) ?? throw new ScalableSessionExpiredException("The cockpit page carries no signed-in user.");
    }

    /// <summary>The signed-in user's id from the cockpit page's <c>__NEXT_DATA__</c>, or null.</summary>
    internal static string? ExtractUserId(string html)
    {
        var m = Regex.Match(html, "<script[^>]*id=\"__NEXT_DATA__\"[^>]*>(?<json>.*?)</script>",
            RegexOptions.Singleline, TimeSpan.FromSeconds(2));
        if (!m.Success) return null;
        JsonNode? root;
        try { root = JsonNode.Parse(m.Groups["json"].Value); }
        catch (System.Text.Json.JsonException) { return null; }
        if (root?["props"]?["pageProps"]?["middlewareProps"] is not JsonObject middleware) return null;
        foreach (var (_, part) in middleware)
        {
            if (part?["session"]?["user"]?["userId"] is JsonValue v && v.TryGetValue<string>(out var id) && !string.IsNullOrEmpty(id))
                return id;
        }
        return null;
    }

    private bool IsAppHost(Uri uri) => string.Equals(uri.Host, _options.BaseUri.Host, StringComparison.OrdinalIgnoreCase);

    /// <summary>Cookie names and expiries only (never values): shows how long the session can last and whether a token cookie exists.</summary>
    private void LogSessionCookies()
    {
        var cookies = _context.Http.ExportCookies()
            .Select(c => $"{c.Name}({(c.ExpiresUtc is { } e ? "expires " + e.ToString("u", CultureInfo.InvariantCulture) : "session")})");
        Log.LogInformation("Scalable session cookies after login: {Cookies}", string.Join(", ", cookies));
    }

    public Task<ConnectorSession?> LogoutAsync(CancellationToken ct)
    {
        if (_loggedInAtAll) _session.Values[LastOkAtKey] = Now.ToString("O", CultureInfo.InvariantCulture);
        return Task.FromResult<ConnectorSession?>(_session);
    }

    private string PersonId => _personId ?? throw new ConnectorException("Not logged in to Scalable.");
}
