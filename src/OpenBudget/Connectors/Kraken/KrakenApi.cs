using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace OpenBudget.Connectors.Kraken;

/// <summary>Endpoints and retry timing of the Kraken connector (overridable for tests).</summary>
public sealed class KrakenOptions
{
    public Uri BaseUri { get; init; } = new("https://api.kraken.com/");

    /// <summary>The wait after the first "rate limit exceeded"; it doubles with every further one.</summary>
    public TimeSpan RateLimitDelay { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>How many times a call is retried after "rate limit exceeded".</summary>
    public int RateLimitRetries { get; init; } = 3;
}

/// <summary>The API key or its permissions were refused; a retry with the same key cannot succeed.</summary>
public sealed class KrakenCredentialsException : Exception
{
    public KrakenCredentialsException()
    {
    }

    public KrakenCredentialsException(string message) : base(message)
    {
    }

    public KrakenCredentialsException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>
/// Kraken's REST interface. Private calls are form POSTs to <c>/0/private/&lt;method&gt;</c> carrying a nonce, signed with
/// the private key: <c>API-Sign = base64(HMAC-SHA512(base64decode(secret), uri_path + SHA256(nonce + postdata)))</c>.
/// Nonces are milliseconds since the epoch, kept strictly increasing (also across syncs through <see cref="LastNonce"/>).
/// Answers carry <c>{"error":[...],"result":...}</c>; errors are mapped to credential failures, retries or a
/// <see cref="ConnectorException"/>. Neither keys nor signatures are logged.
/// </summary>
internal sealed class KrakenApi
{
    private readonly IBankHttpClient _http;
    private readonly TimeProvider _clock;
    private readonly ILogger _log;
    private readonly KrakenOptions _options;
    private string _apiKey = "";
    private byte[] _secret = [];

    public KrakenApi(IBankHttpClient http, TimeProvider clock, ILogger log, KrakenOptions options)
    {
        _http = http;
        _clock = clock;
        _log = log;
        _options = options;
    }

    /// <summary>The highest nonce sent so far (0: none).</summary>
    public long LastNonce { get; set; }

    /// <summary>Takes the API key and the base64 private key; false when the private key is not base64.</summary>
    public bool UseKey(string apiKey, string base64Secret)
    {
        _apiKey = apiKey.Trim();
        try
        {
            _secret = Convert.FromBase64String(base64Secret.Trim());
        }
        catch (FormatException)
        {
            return false;
        }
        return _apiKey.Length > 0 && _secret.Length > 0;
    }

    /// <summary>The API-Sign header for a private call.</summary>
    public static string Sign(byte[] secret, string uriPath, string nonce, string postData)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(nonce + postData));
        var message = Encoding.UTF8.GetBytes(uriPath).Concat(hash).ToArray();
        return Convert.ToBase64String(HMACSHA512.HashData(secret, message));
    }

    private long NextNonce()
    {
        LastNonce = Math.Max(_clock.GetUtcNow().ToUnixTimeMilliseconds(), LastNonce + 1);
        return LastNonce;
    }

    /// <summary>Calls a private method and returns its <c>result</c>.</summary>
    public async Task<JsonElement> PrivateAsync(string method, CancellationToken ct)
    {
        var path = "/0/private/" + method;
        var nonceRetried = false;
        var attempt = 0;
        while (true)
        {
            var nonce = NextNonce().ToString(CultureInfo.InvariantCulture);
            var postData = "nonce=" + nonce;
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_options.BaseUri, path.TrimStart('/')))
            {
                Content = new StringContent(postData, Encoding.UTF8, "application/x-www-form-urlencoded"),
            };
            request.Headers.TryAddWithoutValidation("API-Key", _apiKey);
            request.Headers.TryAddWithoutValidation("API-Sign", Sign(_secret, path, nonce, postData));
            var (errors, result) = await SendAsync(request, method, ct);
            if (errors.Count == 0) return result;

            if (errors.Any(e => e.Contains("Invalid key", StringComparison.OrdinalIgnoreCase)))
                throw new KrakenCredentialsException("Kraken rejected the API key. Check that the API key and private key were copied completely.");
            if (errors.Any(e => e.Contains("Invalid signature", StringComparison.OrdinalIgnoreCase)))
                throw new KrakenCredentialsException("Kraken rejected the signature: the private key does not belong to this API key.");
            if (errors.Any(e => e.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)))
                throw new KrakenCredentialsException("The API key lacks a permission. Give it “Query Funds” and “Query Ledger Entries”.");
            if (errors.Any(e => e.Contains("Invalid nonce", StringComparison.OrdinalIgnoreCase)) && !nonceRetried)
            {
                nonceRetried = true;
                _log.LogInformation("Kraken refused the nonce of {Method}; retrying once with a fresh one.", method);
                continue;
            }
            if (errors.Any(IsRateLimit) && attempt < _options.RateLimitRetries)
            {
                await WaitAsync(attempt++, method, ct);
                continue;
            }
            throw Failure(method, errors);
        }
    }

    /// <summary>Calls a public method (<paramref name="query"/> without the leading '?') and returns its <c>result</c>.</summary>
    public async Task<JsonElement> PublicAsync(string method, string? query, CancellationToken ct)
    {
        var attempt = 0;
        while (true)
        {
            var url = new Uri(_options.BaseUri, "0/public/" + method + (string.IsNullOrEmpty(query) ? "" : "?" + query));
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            var (errors, result) = await SendAsync(request, method, ct);
            if (errors.Count == 0) return result;
            if (errors.Any(IsRateLimit) && attempt < _options.RateLimitRetries)
            {
                await WaitAsync(attempt++, method, ct);
                continue;
            }
            throw Failure(method, errors);
        }
    }

    private static bool IsRateLimit(string error) => error.Contains("Rate limit", StringComparison.OrdinalIgnoreCase)
        || error.Contains("Too many requests", StringComparison.OrdinalIgnoreCase);

    private async Task WaitAsync(int attempt, string method, CancellationToken ct)
    {
        var delay = _options.RateLimitDelay * Math.Pow(2, attempt);
        _log.LogInformation("Kraken's rate limit was hit by {Method}; waiting {Seconds:0.#} s.", method, delay.TotalSeconds);
        if (delay > TimeSpan.Zero) await Task.Delay(delay, _clock, ct);
    }

    private static ConnectorException Failure(string method, IReadOnlyList<string> errors) =>
        new($"Kraken refused {method}: {string.Join(", ", errors.Select(e => e.Length <= 80 ? e : e[..80]))}");

    private async Task<(List<string> Errors, JsonElement Result)> SendAsync(HttpRequestMessage request, string method, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new ConnectorException($"Kraken is not reachable ({method}): {ex.Message}", ex);
        }
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(body);
            }
            catch (JsonException)
            {
                if (response.StatusCode == HttpStatusCode.TooManyRequests) return (["EAPI:Rate limit exceeded"], default);
                throw new ConnectorException($"Kraken answered {method} with HTTP {(int)response.StatusCode} and no JSON.");
            }
            using (doc)
            {
                var root = doc.RootElement;
                var errors = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Array
                    ? e.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList()
                    : [];
                if (errors.Count == 0 && !response.IsSuccessStatusCode)
                    throw new ConnectorException($"Kraken answered {method} with HTTP {(int)response.StatusCode}.");
                var result = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("result", out var r) ? r.Clone() : default;
                return (errors, result);
            }
        }
    }
}
