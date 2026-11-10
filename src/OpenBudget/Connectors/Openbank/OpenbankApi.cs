using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenBudget.Connectors.Openbank;

/// <summary>Endpoints of the Openbank connector (overridable for tests).</summary>
public sealed class OpenbankOptions
{
    /// <summary>The JSON API behind Openbank's German customer site (kunden.openbankpay.com).</summary>
    public Uri ApiBase { get; init; } = new("https://api.kunden.openbankpay.com/");

    /// <summary>The web client the API calls come from (Origin/Referer).</summary>
    public Uri SiteBase { get; init; } = new("https://kunden.openbankpay.com/");

    /// <summary>Page size of the card transactions request (the web client asks for 99).</summary>
    public int PageSize { get; init; } = 99;

    /// <summary>The card programme's area on the customer site; its sign-in page is &lt;SiteBase&gt;/&lt;Portal&gt;/login.</summary>
    public string Portal { get; init; } = "amazon";

    /// <summary>Openbank's identity provider, which the sign-in page hands over to (its cookies travel with the web login).</summary>
    public string LoginHost { get; init; } = "login.openbankpay.com";
}

/// <summary>The access token is no longer accepted (HTTP 401/403).</summary>
public sealed class OpenbankUnauthorizedException : Exception
{
    public OpenbankUnauthorizedException()
    {
    }

    public OpenbankUnauthorizedException(string message) : base(message)
    {
    }

    public OpenbankUnauthorizedException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>An HTTP answer of the API: status and parsed JSON body (null when the body is not JSON).</summary>
internal sealed record OpenbankResponse(HttpStatusCode Status, JsonElement? Body)
{
    public bool Ok => (int)Status is >= 200 and < 300;
}

/// <summary>
/// Thin JSON client for Openbank's API: bearer token, JSON in and out. Only paths and status codes are
/// logged, never bodies (they carry tokens and personal data).
/// </summary>
internal sealed class OpenbankApi(IBankHttpClient http, ILogger logger, OpenbankOptions options)
{
    public string? AccessToken { get; set; }

    public async Task<OpenbankResponse> SendAsync(HttpMethod method, string path, JsonObject? body, CancellationToken ct, bool authorized = true)
    {
        using var request = new HttpRequestMessage(method, new Uri(options.ApiBase, path));
        if (body != null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("Origin", options.SiteBase.GetLeftPart(UriPartial.Authority));
        request.Headers.Referrer = options.SiteBase;
        if (authorized && AccessToken != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken);

        using var response = await http.SendAsync(request, ct);
        logger.LogDebug("Openbank {Method} {Path}: HTTP {Status}.", method, Describe(path), (int)response.StatusCode);
        var text = await response.Content.ReadAsStringAsync(ct);
        JsonElement? json = null;
        if (text.Length > 0)
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                json = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                // Not JSON (an HTML error page): the status code says enough.
            }
        }
        return new OpenbankResponse(response.StatusCode, json);
    }

    /// <summary>A GET that must succeed: 401/403 become <see cref="OpenbankUnauthorizedException"/>, other errors a <see cref="ConnectorException"/>.</summary>
    public async Task<JsonElement> GetAsync(string path, CancellationToken ct)
    {
        var response = await SendAsync(HttpMethod.Get, path, null, ct);
        if (response.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new OpenbankUnauthorizedException($"HTTP {(int)response.Status}");
        if (!response.Ok || response.Body is not { } body)
            throw new ConnectorException($"Openbank answered HTTP {(int)response.Status} for {Describe(path)}.");
        return body;
    }

    /// <summary>The API area of a path (e.g. "cb-api-cards"), without card ids or query: safe to log and show.</summary>
    internal static string Describe(string path) => path.Split('?')[0].Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "/";
}
