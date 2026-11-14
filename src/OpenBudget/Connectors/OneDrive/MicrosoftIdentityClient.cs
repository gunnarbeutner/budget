using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OpenBudget.Connectors.OneDrive;

/// <summary>Tokens from the Microsoft identity platform. Never logged or shown.</summary>
public sealed record MicrosoftTokens(string AccessToken, string? RefreshToken, DateTime ExpiresAtUtc, string? AccountName)
{
    /// <summary>The scopes the token endpoint says it granted (null when it did not say).</summary>
    public string? GrantedScopes { get; init; }

    /// <summary>Whether mail reading was granted: the answer names Mail.Read, or (without a scope list) it was asked for.</summary>
    public bool GrantsMail(bool requested) =>
        GrantedScopes == null ? requested : GrantedScopes.Split(' ').Any(s => s.EndsWith("Mail.Read", StringComparison.OrdinalIgnoreCase));
}

/// <summary>A failure talking to Microsoft; <see cref="Exception.Message"/> is safe to show the user.</summary>
public class CloudException : Exception
{
    public CloudException() { }

    public CloudException(string message) : base(message) { }

    public CloudException(string message, Exception? innerException) : base(message, innerException) { }
}

/// <summary>Microsoft no longer accepts the stored grant (consent revoked, expired, password changed): the user must reconnect.</summary>
public sealed class CloudReconnectRequiredException : CloudException
{
    public CloudReconnectRequiredException() { }

    public CloudReconnectRequiredException(string message) : base(message) { }

    public CloudReconnectRequiredException(string message, Exception? innerException) : base(message, innerException) { }
}

/// <summary>
/// The OAuth 2.0 authorization code flow (with PKCE) and refresh-token grant against the Microsoft identity
/// platform v2.0 endpoints, over a plain <see cref="HttpClient"/>. Tokens never appear in log messages or exceptions.
/// </summary>
public sealed class MicrosoftIdentityClient
{
    private static readonly HashSet<string> ReconnectErrors =
        new(StringComparer.Ordinal) { "invalid_grant", "interaction_required", "consent_required", "login_required" };

    private readonly IHttpClientFactory _http;
    private readonly OneDriveOptions _options;
    private readonly TimeProvider _clock;

    public MicrosoftIdentityClient(IHttpClientFactory http, OneDriveOptions options, TimeProvider clock)
    {
        _http = http;
        _options = options;
        _clock = clock;
    }

    /// <summary>A fresh PKCE code verifier (43 characters of base64url).</summary>
    public static string NewCodeVerifier() => Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>The S256 code challenge of <paramref name="verifier"/>.</summary>
    public static string CodeChallenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    public static string NewState() => Base64Url(RandomNumberGenerator.GetBytes(24));

    public Uri BuildAuthorizeUrl(string state, string codeChallenge, Uri redirectUri, bool mail = false)
    {
        var query = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["client_id"] = _options.ClientId ?? "",
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri.AbsoluteUri,
            ["response_mode"] = "query",
            ["scope"] = OneDriveOptions.ScopesFor(mail),
            ["state"] = state,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
            // Always offer the account picker so a second Microsoft account can be chosen when reconnecting.
            ["prompt"] = "select_account",
        };
        return new Uri(_options.AuthorizeEndpoint + "?" + string.Join("&", query.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}")));
    }

    public Task<MicrosoftTokens> RedeemCodeAsync(string code, string codeVerifier, Uri redirectUri, CancellationToken ct) =>
        RedeemCodeAsync(code, codeVerifier, redirectUri, false, ct);

    /// <summary>Redeems a code; <paramref name="mail"/>: the sign-in asked for mail access too.</summary>
    public Task<MicrosoftTokens> RedeemCodeAsync(string code, string codeVerifier, Uri redirectUri, bool mail, CancellationToken ct) =>
        RequestTokenAsync(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri.AbsoluteUri,
            ["code_verifier"] = codeVerifier,
            ["scope"] = OneDriveOptions.ScopesFor(mail),
        }, ct);

    public Task<MicrosoftTokens> RefreshAsync(string refreshToken, CancellationToken ct) => RefreshAsync(refreshToken, false, ct);

    /// <summary>Refreshes; <paramref name="mail"/>: the connection includes mail access (asked for again so the token carries it).</summary>
    public Task<MicrosoftTokens> RefreshAsync(string refreshToken, bool mail, CancellationToken ct) =>
        RequestTokenAsync(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["scope"] = OneDriveOptions.ScopesFor(mail),
        }, ct);

    private async Task<MicrosoftTokens> RequestTokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        form["client_id"] = _options.ClientId ?? "";
        form["client_secret"] = _options.ClientSecret ?? "";
        using var request = new HttpRequestMessage(HttpMethod.Post, _options.TokenEndpoint) { Content = new FormUrlEncodedContent(form) };
        HttpResponseMessage response;
        try
        {
            response = await _http.CreateClient(OneDriveOptions.HttpClientName).SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new CloudException("Microsoft sign-in could not be reached.", ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            JsonElement root;
            try
            {
                using var doc = JsonDocument.Parse(body);
                root = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                throw new CloudException($"Microsoft sign-in answered with HTTP {(int)response.StatusCode}.");
            }

            if (!response.IsSuccessStatusCode || !root.TryGetProperty("access_token", out var access) || access.GetString() is not { Length: > 0 } accessToken)
            {
                var error = root.TryGetProperty("error", out var e) ? e.GetString() ?? "" : "";
                if (ReconnectErrors.Contains(error))
                    throw new CloudReconnectRequiredException("Microsoft no longer accepts OpenBudget's access. Reconnect to Microsoft.");
                if (error == "invalid_client")
                    throw new CloudException("Microsoft rejected OpenBudget's app registration (client id or secret); check the server configuration.");
                throw new CloudException($"Microsoft sign-in failed ({(error.Length > 0 ? error : $"HTTP {(int)response.StatusCode}")}).");
            }

            var expiresIn = root.TryGetProperty("expires_in", out var exp) && exp.TryGetInt32(out var seconds) ? seconds : 3600;
            var refresh = root.TryGetProperty("refresh_token", out var r) ? r.GetString() : null;
            var idToken = root.TryGetProperty("id_token", out var id) ? id.GetString() : null;
            var scope = root.TryGetProperty("scope", out var sc) && sc.ValueKind == JsonValueKind.String ? sc.GetString() : null;
            return new MicrosoftTokens(accessToken, refresh, _clock.GetUtcNow().UtcDateTime.AddSeconds(expiresIn), AccountNameFromIdToken(idToken))
            {
                GrantedScopes = scope,
            };
        }
    }

    /// <summary>
    /// The account's display name from the ID token's claims (preferred_username, else name). The token came
    /// straight from the token endpoint over TLS, so its payload is read without signature validation; it is only
    /// used as a label.
    /// </summary>
    public static string? AccountNameFromIdToken(string? idToken)
    {
        var parts = idToken?.Split('.');
        if (parts is not { Length: >= 2 }) return null;
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            foreach (var claim in new[] { "preferred_username", "email", "name" })
                if (doc.RootElement.TryGetProperty(claim, out var v) && v.GetString() is { Length: > 0 } value) return value;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
        return null;
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
