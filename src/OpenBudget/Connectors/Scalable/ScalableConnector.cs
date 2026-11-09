using System.Globalization;
using System.Text.Json.Nodes;

namespace OpenBudget.Connectors.Scalable;

/// <summary>
/// Scalable Capital (broker + Verrechnungskonto) over its web client's plain HTTP interface: an
/// OAuth authorization-code login (Auth0 Universal Login form), an app-approval 2FA and GraphQL
/// data queries. The cookie jar is the session; it is persisted so later syncs skip login and 2FA.
/// Every broker portfolio becomes a depot account (positions) plus a cash account (balance,
/// bookings), identified as "&lt;portfolioId&gt;:depot" / "&lt;portfolioId&gt;:cash".
/// </summary>
public sealed partial class ScalableConnector : IBankConnector
{
    public const string TypeName = "scalable";
    internal const string ApprovalReference = "scalable-2fa";
    private const string LoginAtKey = "loginAt";
    private const string LastOkAtKey = "lastOkAt";

    private readonly ConnectorContext _context;
    private readonly ScalableOptions _options;
    private readonly ScalableGraphQl _graphQl;
    private ConnectorSession _session = new();
    private string? _personId;
    private string? _mfaSessionId;
    private string? _priorTwoFactorError;
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
        + "Log in with your Scalable e-mail address and password; the login is confirmed in the Scalable app. "
        + "The first live logins may need adjustments, see the README.",
        ctx => new ScalableConnector(ctx, options));

    public string ConnectorType => TypeName;

    private ILogger Log => _context.Logger;

    private DateTime Now => _context.Clock.GetUtcNow().UtcDateTime;

    // ---- login ----

    public async Task<ConnectorStep<ConnectorSession?>> LoginAsync(
        ConnectorCredentials credentials, ConnectorSession? storedSession, ChallengeAnswer? answer, CancellationToken ct)
    {
        try
        {
            return answer == null
                ? await StartLoginAsync(credentials, storedSession, ct)
                : await PollTwoFactorAsync(ct);
        }
        catch (ScalableSessionExpiredException ex)
        {
            Log.LogWarning(ex, "Scalable rejected the session during login: {Reason}", ex.Message);
            throw new ConnectorException("Scalable did not accept the session during login (UNAUTHENTICATED).", ex);
        }
    }

    private async Task<ConnectorStep<ConnectorSession?>> StartLoginAsync(ConnectorCredentials credentials, ConnectorSession? stored, CancellationToken ct)
    {
        _session = stored ?? new ConnectorSession();
        _loggedInAtAll = false;
        if (stored is { Cookies.Count: > 0 } && await TryReuseSessionAsync(ct))
            return ConnectorStep.Done<ConnectorSession?>(_session);

        var rejected = await SignInAsync(credentials, ct);
        if (rejected != null) return rejected;
        await RunPostLoginChecksAsync(ct);
        return await BeginTwoFactorAsync(ct);
    }

    /// <summary>True when the stored cookies still authenticate; otherwise logs how long the session lasted.</summary>
    private async Task<bool> TryReuseSessionAsync(CancellationToken ct)
    {
        try
        {
            _personId = await FetchPersonIdAsync(ct);
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

    private async Task<string> FetchPersonIdAsync(CancellationToken ct)
    {
        var data = await _graphQl.ExecuteAsync(ScalableGraphQl.CockpitPath, "personOverview", ScalableQueries.PersonOverview, new JsonObject(), ct);
        return data.Str("personOverview", "id") ?? throw new ConnectorException("Scalable's personOverview answer had no id.");
    }

    // ---- OAuth sign-in (Auth0 Universal Login) ----

    /// <summary>Walks authorize -> login form -> callback. Null on success, otherwise the failing step.</summary>
    private async Task<ConnectorStep<ConnectorSession?>?> SignInAsync(ConnectorCredentials credentials, CancellationToken ct)
    {
        Log.LogInformation("Logging in to Scalable Capital.");
        var (status, page, html) = await GetPageAsync(new Uri(_options.BaseUri, "auth/login"), ct);
        Log.LogDebug("Scalable auth/login: HTTP {Status}, landed on {Path}.", status, page.AbsolutePath);

        for (var step = 0; step < 3 && !IsAppHost(page); step++)
        {
            var form = ScalableLoginForm.Parse(html, page)
                ?? throw new ConnectorException($"Scalable's login page ({page.AbsolutePath}) has no sign-in form this connector understands.");
            var sentPassword = form.PasswordField != null;
            (status, page, html) = await PostFormAsync(form.Action, form.Fill(credentials.Username, credentials.Secret), ct);
            Log.LogDebug("Scalable login form step {Step}: HTTP {Status}, landed on {Path}.", step + 1, status, page.AbsolutePath);

            if (IsAppHost(page)) break;
            var again = ScalableLoginForm.Parse(html, page);
            if (sentPassword && (status is 400 or 401 or 403 || again?.PasswordField != null))
                return ConnectorStep.Fail<ConnectorSession?>("Scalable rejected the e-mail address or password.", credentialsRejected: true);
        }

        if (!IsAppHost(page))
            throw new ConnectorException($"Scalable's login did not complete (stopped at {page.AbsolutePath}).");
        return null;
    }

    private bool IsAppHost(Uri uri) => string.Equals(uri.Host, _options.BaseUri.Host, StringComparison.OrdinalIgnoreCase);

    private async Task<(int Status, Uri Page, string Html)> GetPageAsync(Uri url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await _context.Http.SendAsync(request, ct);
        return await ReadPageAsync(response, url, ct);
    }

    private async Task<(int Status, Uri Page, string Html)> PostFormAsync(Uri url, List<KeyValuePair<string, string>> fields, CancellationToken ct)
    {
        using var response = await _context.Http.PostFormAsync(url, fields, ct);
        return await ReadPageAsync(response, url, ct);
    }

    private static async Task<(int Status, Uri Page, string Html)> ReadPageAsync(HttpResponseMessage response, Uri requested, CancellationToken ct) =>
        ((int)response.StatusCode, response.RequestMessage?.RequestUri ?? requested, await response.Content.ReadAsStringAsync(ct));

    /// <summary>The follow-up requests the web client makes after the callback; their answers are not needed, only their cookies.</summary>
    private async Task RunPostLoginChecksAsync(CancellationToken ct)
    {
        foreach (var path in new[] { "auth/onboarding-check", "auth/mfa-enforcement", "auth/mfa-check" })
        {
            var (status, page, _) = await GetPageAsync(new Uri(_options.BaseUri, path), ct);
            Log.LogDebug("Scalable {Path}: HTTP {Status}, landed on {Landed}.", path, status, page.AbsolutePath);
        }
    }

    // ---- 2FA (app approval) ----

    private async Task<ConnectorStep<ConnectorSession?>> BeginTwoFactorAsync(CancellationToken ct)
    {
        _priorTwoFactorError = null;
        System.Text.Json.JsonElement? info = null;
        try
        {
            var data = await _graphQl.ExecuteAsync(ScalableGraphQl.AuthPath, "is2faOnLoginEnabled", ScalableTwoFactorDocuments.Is2faOnLoginEnabled,
                new JsonObject { ["input"] = ParseInput(_options.Is2faEnabledInputJson, nameof(ScalableOptions.Is2faEnabledInputJson)) }, ct);
            info = data.Path("is2faOnLoginEnabled");
        }
        catch (ConnectorException ex)
        {
            // The input of this query is a guess: carry on as if 2FA were required and keep the message for a later failure.
            _priorTwoFactorError = ex.Message;
            Log.LogWarning(ex, "Scalable's 2FA status query failed; assuming 2FA is required: {Message}", ex.Message);
        }

        if (info is { } i)
        {
            if (i.Bool("isSms2faEnabled"))
                throw new ConnectorException("Scalable uses SMS two-factor authentication for this login, which is not supported yet.");
            if (!i.Bool("enabled") || i.Bool("hasApprovedSession"))
            {
                Log.LogInformation("Scalable needs no 2FA for this login.");
                return await FinishLoginAsync(ct);
            }
        }
        return await StartTwoFactorAsync(ct);
    }

    private async Task<ConnectorStep<ConnectorSession?>> StartTwoFactorAsync(CancellationToken ct)
    {
        try
        {
            var data = await _graphQl.ExecuteAsync(ScalableGraphQl.AuthPath, "start2faOnLogin", _options.Start2faQuery,
                new JsonObject { ["input"] = ParseInput(_options.Start2faInputJson, nameof(ScalableOptions.Start2faInputJson)) }, ct);
            _mfaSessionId = data.Str("start2faOnLogin", "mfaSessionId")
                ?? throw new ConnectorException("Scalable's start2faOnLogin answer had no mfaSessionId.");
        }
        catch (ConnectorException ex) when (_priorTwoFactorError != null)
        {
            throw new ConnectorException($"{ex.Message} (the earlier 2FA status query also failed: {_priorTwoFactorError})", ex);
        }
        Log.LogInformation("Waiting for the login to be approved in the Scalable app.");
        return ConnectorStep.Ask<ConnectorSession?>(ConnectorChallenge.Approval(
            "Bitte bestätigen Sie die Anmeldung in der Scalable-App. / Please confirm the login in the Scalable app.",
            _options.PollInterval, ApprovalReference));
    }

    private async Task<ConnectorStep<ConnectorSession?>> PollTwoFactorAsync(CancellationToken ct)
    {
        if (_mfaSessionId == null) return ConnectorStep.Fail<ConnectorSession?>("There is no 2FA request to wait for.");
        var template = _options.Validate2faInputTemplate.Replace("{mfaSessionId}", System.Text.Json.JsonEncodedText.Encode(_mfaSessionId).ToString(), StringComparison.Ordinal);
        var data = await _graphQl.ExecuteAsync(ScalableGraphQl.AuthPath, "validate2faOnLogin", _options.Validate2faQuery,
            new JsonObject { ["input"] = ParseInput(template, nameof(ScalableOptions.Validate2faInputTemplate)) }, ct);
        var status = data.Str("validate2faOnLogin", "status") ?? "(none)";
        switch (status.ToUpperInvariant())
        {
            case "SUCCESS":
                return await FinishLoginAsync(ct);
            case "PENDING":
                return ConnectorStep.Ask<ConnectorSession?>(ConnectorChallenge.Approval(
                    "Bitte bestätigen Sie die Anmeldung in der Scalable-App. / Please confirm the login in the Scalable app.",
                    _options.PollInterval, ApprovalReference));
            default:
                Log.LogWarning("Scalable reported the 2FA status {Status}.", status);
                return ConnectorStep.Fail<ConnectorSession?>($"Scalable did not approve the login (status {(status.Length <= 40 ? status : status[..40])}).");
        }
    }

    private async Task<ConnectorStep<ConnectorSession?>> FinishLoginAsync(CancellationToken ct)
    {
        await GetPageAsync(new Uri(_options.BaseUri, "auth/mfa-check"), ct);
        try
        {
            _personId = await FetchPersonIdAsync(ct);
        }
        catch (ScalableSessionExpiredException)
        {
            throw new ConnectorException("Scalable still reports the session as unauthenticated after login and 2FA.");
        }
        _loggedInAtAll = true;
        _session.Values[LoginAtKey] = Now.ToString("O", CultureInfo.InvariantCulture);
        LogSessionCookies();
        Log.LogInformation("Logged in to Scalable Capital.");
        return ConnectorStep.Done<ConnectorSession?>(_session);
    }

    /// <summary>Cookie names and expiries only (never values): shows how long the session can last and whether a token cookie exists.</summary>
    private void LogSessionCookies()
    {
        var cookies = _context.Http.ExportCookies()
            .Select(c => $"{c.Name}({(c.ExpiresUtc is { } e ? "expires " + e.ToString("u", CultureInfo.InvariantCulture) : "session")})");
        Log.LogInformation("Scalable session cookies after login: {Cookies}", string.Join(", ", cookies));
    }

    private static JsonObject ParseInput(string json, string setting)
    {
        try
        {
            return JsonNode.Parse(json) is JsonObject o ? o : throw new ConnectorException($"The Scalable setting {setting} must be a JSON object.");
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new ConnectorException($"The Scalable setting {setting} is not valid JSON.", ex);
        }
    }

    public Task<ConnectorSession?> LogoutAsync(CancellationToken ct)
    {
        if (_loggedInAtAll) _session.Values[LastOkAtKey] = Now.ToString("O", CultureInfo.InvariantCulture);
        return Task.FromResult<ConnectorSession?>(_session);
    }

    private string PersonId => _personId ?? throw new ConnectorException("Not logged in to Scalable.");
}
