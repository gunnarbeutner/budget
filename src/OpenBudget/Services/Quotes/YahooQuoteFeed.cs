using System.Globalization;
using System.Text.Json;

namespace OpenBudget.Services.Quotes;

/// <summary>Daily closes of a symbol from <paramref name="Closes"/>' first day on, in <paramref name="Currency"/>.</summary>
public sealed record QuoteSeries(string Symbol, string Currency, IReadOnlyList<MarketClose> Closes);

public interface IQuoteFeed
{
    /// <summary>The symbol's daily closes from <paramref name="from"/> on, or null when the feed does not know it.</summary>
    Task<QuoteSeries?> FetchAsync(string symbol, DateOnly from, CancellationToken ct);
}

/// <summary>
/// Yahoo Finance's chart API, the feed PortfolioPerformance's "Yahoo Finance" provider reads. The daily bars carry the
/// closes; the bar of the running or just-ended session has no close yet, so the day of the regular market time takes
/// the regular market price, as PortfolioPerformance's latest quote does.
/// </summary>
public sealed class YahooQuoteFeed : IQuoteFeed
{
    /// <summary>The named client; its base address is the API host.</summary>
    public const string HttpClientName = "yahoo-quotes";

    private readonly IHttpClientFactory _http;

    public YahooQuoteFeed(IHttpClientFactory http) => _http = http;

    public async Task<QuoteSeries?> FetchAsync(string symbol, DateOnly from, CancellationToken ct)
    {
        var period1 = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeSeconds();
        var period2 = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds();
        var url = new Uri(string.Create(CultureInfo.InvariantCulture,
            $"v8/finance/chart/{Uri.EscapeDataString(symbol)}?period1={period1}&period2={period2}&interval=1d"), UriKind.Relative);
        using var client = _http.CreateClient(HttpClientName);
        using var response = await client.GetAsync(url, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(body, cancellationToken: ct);
        return Parse(symbol, doc.RootElement);
    }

    /// <summary>Reads a chart API answer; null when it has no result or no currency.</summary>
    public static QuoteSeries? Parse(string symbol, JsonElement root)
    {
        if (!root.TryGetProperty("chart", out var chart) || !chart.TryGetProperty("result", out var results)
            || results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0) return null;
        var result = results[0];
        if (!result.TryGetProperty("meta", out var meta) || !meta.TryGetProperty("currency", out var cur) || cur.ValueKind != JsonValueKind.String) return null;
        var currency = cur.GetString()!;
        var offset = meta.TryGetProperty("gmtoffset", out var g) && g.ValueKind == JsonValueKind.Number ? g.GetInt64() : 0;

        var closes = new SortedDictionary<long, double>();
        if (result.TryGetProperty("timestamp", out var stamps) && stamps.ValueKind == JsonValueKind.Array
            && result.TryGetProperty("indicators", out var ind) && ind.TryGetProperty("quote", out var quote)
            && quote.ValueKind == JsonValueKind.Array && quote.GetArrayLength() > 0
            && quote[0].TryGetProperty("close", out var close) && close.ValueKind == JsonValueKind.Array)
        {
            var n = Math.Min(stamps.GetArrayLength(), close.GetArrayLength());
            for (var i = 0; i < n; i++)
            {
                if (close[i].ValueKind != JsonValueKind.Number || stamps[i].ValueKind != JsonValueKind.Number) continue;
                var value = close[i].GetDouble();
                if (value > 0) closes[ExchangeDay(stamps[i].GetInt64(), offset)] = value;
            }
        }
        if (meta.TryGetProperty("regularMarketPrice", out var price) && price.ValueKind == JsonValueKind.Number && price.GetDouble() > 0
            && meta.TryGetProperty("regularMarketTime", out var time) && time.ValueKind == JsonValueKind.Number)
        {
            closes[ExchangeDay(time.GetInt64(), offset)] = price.GetDouble();
        }
        return new QuoteSeries(symbol, currency, closes.Select(c => new MarketClose(c.Key, c.Value)).ToList());
    }

    /// <summary>The exchange's calendar day of a Unix time, as days since 1970-01-01.</summary>
    private static long ExchangeDay(long unixSeconds, long gmtOffset) => (long)Math.Floor((unixSeconds + gmtOffset) / 86400.0);
}
