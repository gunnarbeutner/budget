using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenBudget.Connectors.Scalable;

/// <summary>The stored session is no longer accepted (GraphQL code UNAUTHENTICATED, HTTP 401, or a redirect to the login).</summary>
public sealed class ScalableSessionExpiredException : Exception
{
    public ScalableSessionExpiredException()
    {
    }

    public ScalableSessionExpiredException(string message) : base(message)
    {
    }

    public ScalableSessionExpiredException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>
/// POSTs GraphQL documents to Scalable's endpoints. Failures become <see cref="ConnectorException"/>s
/// carrying Scalable's own error messages (they normally name the wrong or missing field) and are
/// logged at Warning; nothing else from a response is ever logged.
/// </summary>
internal sealed class ScalableGraphQl
{
    private const int MaxMessages = 5;
    private const int MaxMessageLength = 300;

    private readonly IBankHttpClient _http;
    private readonly ILogger _logger;
    private readonly Uri _baseUri;

    public ScalableGraphQl(IBankHttpClient http, ILogger logger, Uri baseUri)
    {
        _http = http;
        _logger = logger;
        _baseUri = baseUri;
    }

    public const string CockpitPath = "cockpit/graphql";
    public const string BrokerPath = "broker/api/data";
    public const string AuthPath = "auth/graphql";

    /// <summary>Runs one operation and returns its <c>data</c> object.</summary>
    public async Task<JsonElement> ExecuteAsync(string endpointPath, string operation, string query, JsonObject variables, CancellationToken ct)
    {
        var body = new JsonObject { ["operationName"] = operation, ["query"] = query, ["variables"] = variables }.ToJsonString();
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, endpointPath))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Referrer = new Uri(_baseUri, "cockpit/");
        request.Headers.TryAddWithoutValidation("Origin", _baseUri.GetLeftPart(UriPartial.Authority));

        using var response = await _http.SendAsync(request, ct);
        var status = (int)response.StatusCode;
        _logger.LogDebug("Scalable {Operation}: HTTP {Status}.", operation, status);
        if (response.StatusCode is HttpStatusCode.Unauthorized)
            throw new ScalableSessionExpiredException($"HTTP 401 for {operation}");
        if (response.RequestMessage?.RequestUri is { } final && !string.Equals(final.Host, _baseUri.Host, StringComparison.OrdinalIgnoreCase))
            throw new ScalableSessionExpiredException($"{operation} was redirected to the login");

        var text = await response.Content.ReadAsStringAsync(ct);
        JsonDocument? document = null;
        try
        {
            document = ParseOrNull(text) ?? throw Fail(operation, $"Scalable answered {operation} with an unexpected response (HTTP {status}).");
            return Unwrap(document.RootElement, operation, status);
        }
        finally
        {
            document?.Dispose();
        }
    }

    private static JsonDocument? ParseOrNull(string text)
    {
        try { return JsonDocument.Parse(text); }
        catch (JsonException) { return null; }
    }

    private JsonElement Unwrap(JsonElement root, string operation, int status)
    {
        var errors = root.Path("errors").Items().ToList();
        if (errors.Count > 0)
        {
            if (errors.Any(e => string.Equals(e.Str("extensions", "code"), "UNAUTHENTICATED", StringComparison.Ordinal)))
                throw new ScalableSessionExpiredException($"UNAUTHENTICATED for {operation}");
            var messages = errors.Select(e => e.Str("message") ?? "(no message)").Take(MaxMessages)
                .Select(m => m.Length <= MaxMessageLength ? m : m[..MaxMessageLength]).ToList();
            var hasData = root.Path("data") is { ValueKind: JsonValueKind.Object };
            if (!hasData)
                throw Fail(operation, $"Scalable rejected {operation}: {string.Join(" | ", messages)}");
            _logger.LogWarning("Scalable {Operation} returned partial data with GraphQL errors: {Errors}", operation, string.Join(" | ", messages));
        }

        if (root.Path("data") is { ValueKind: JsonValueKind.Object } data) return data.Clone();
        throw Fail(operation, $"Scalable answered {operation} without data (HTTP {status}).");
    }

    private ConnectorException Fail(string operation, string message)
    {
        _logger.LogWarning("Scalable {Operation} failed: {Message}", operation, message);
        return new ConnectorException(message);
    }
}
