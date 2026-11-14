using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OpenBudget.PortfolioPerformance.Proto;
using OpenBudget.Services;
using OpenBudget.Services.Quotes;
using Xunit;
using static OpenBudget.Tests.PortfolioFixture;

namespace OpenBudget.Tests;

/// <summary>Yahoo quotes between saves of a PortfolioPerformance file: parsing, merging into the valuation, and the refresh.</summary>
public sealed class QuoteRefreshTests : IAsyncDisposable
{
    private const string Owner = TestDb.DefaultOwner;

    private readonly TestDb _t = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"ob-quotes-{Guid.NewGuid():N}");
    private readonly UploadedPortfolioFileSource _files;
    private readonly PortfolioAccountSync _sync;
    private readonly FakeFeed _feed = new();
    private readonly QuoteRefreshService _refresh;

    public QuoteRefreshTests()
    {
        _files = new UploadedPortfolioFileSource(Path.Combine(_dir, "files"));
        _sync = new PortfolioAccountSync(_t.System, _files, _t.Notifier, new FixedClock(), NullLogger<PortfolioAccountSync>.Instance);
        _refresh = new QuoteRefreshService(_t.System, _files, _sync, _feed, new FixedClock(), NullLogger<QuoteRefreshService>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        _refresh.Dispose();
        await _t.DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* best effort */ }
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2024, 1, 10, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class FakeFeed : IQuoteFeed
    {
        public Dictionary<string, QuoteSeries> Series { get; } = new(StringComparer.Ordinal);

        public List<(string Symbol, DateOnly From)> Requests { get; } = [];

        public Task<QuoteSeries?> FetchAsync(string symbol, DateOnly from, CancellationToken ct)
        {
            Requests.Add((symbol, from));
            return Task.FromResult(Series.GetValueOrDefault(symbol));
        }
    }

    /// <summary>The fixture client with Gold quoted on Yahoo and a quoted security nobody holds.</summary>
    private static PClient QuotedClient()
    {
        var c = Client();
        var gold = c.Securities.Single(s => s.Uuid == "sec-gold");
        gold.TickerSymbol = "GOLD.EX";
        gold.Feed = "YAHOO";
        c.Securities.Add(new PSecurity { Uuid = "sec-sold", Name = "Sold", CurrencyCode = "EUR", TickerSymbol = "SOLD.EX", Feed = "YAHOO" });
        return c;
    }

    private async Task StoreFileAsync(PClient client)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, $"{Guid.NewGuid():N}.portfolio");
        await File.WriteAllBytesAsync(path, Pack(client));
        await _files.StoreAsync(Owner, path, CancellationToken.None);
        await _sync.SyncAsync(Owner);
    }

    private async Task<double?> GoldPriceAsync()
    {
        await using var db = await _t.System.CreateDbContextAsync();
        return (await db.Holdings.AsNoTracking().FirstAsync(h => h.PpSecurityUuid == "sec-gold")).Price;
    }

    [Fact]
    public void The_chart_answer_gives_daily_closes_and_the_regular_market_price_for_its_day()
    {
        // Two bars at the exchange's 09:00 (UTC+1), the second without a close yet; the market price is from 17:30 that day.
        const string json = """
            {"chart":{"result":[{"meta":{"currency":"EUR","gmtoffset":3600,"regularMarketPrice":12.5,"regularMarketTime":1704904200},
             "timestamp":[1704787200,1704873600],"indicators":{"quote":[{"close":[11.75,null]}]}}]}}
            """;
        var series = YahooQuoteFeed.Parse("X.EX", JsonElement.Parse(json))!;
        Assert.Equal("EUR", series.Currency);
        Assert.Equal([new MarketClose(Day(1, 9), 11.75), new MarketClose(Day(1, 10), 12.5)], series.Closes);
        Assert.Null(YahooQuoteFeed.Parse("X.EX", JsonElement.Parse("""{"chart":{"result":null}}""")));
    }

    [Fact]
    public void Fetched_closes_only_add_the_days_after_the_files_newest_price()
    {
        var closes = new Dictionary<string, IReadOnlyList<MarketClose>>(StringComparer.Ordinal)
        {
            ["sec-gold"] = [new MarketClose(Day(1, 9), 99), new MarketClose(Day(1, 10), 64)],
        };
        var valuation = PortfolioValuation.Compute(Client(), Today, closes);
        var gold = valuation.Portfolios.Single(p => p.Uuid == "pf-a").Positions.Single(p => p.Security.Uuid == "sec-gold");
        Assert.Equal((64.0, Day(1, 10)), (gold.Price, gold.Security.PriceDay!.Value));
        var history = valuation.Portfolios.Single(p => p.Uuid == "pf-a").History.Single(h => h.SecurityName == "Gold").Values;
        Assert.Equal(620, history[Day(1, 9)], 9);    // the file's latest quote (62) wins over the fetched 99
        Assert.Equal(640, history[Day(1, 10)], 9);
    }

    [Fact]
    public async Task A_refresh_fetches_the_held_quoted_securities_and_revalues_the_accounts()
    {
        await StoreFileAsync(QuotedClient());
        Assert.Equal(62, await GoldPriceAsync());
        _feed.Series["GOLD.EX"] = new QuoteSeries("GOLD.EX", "EUR", [new MarketClose(Day(1, 8), 61), new MarketClose(Day(1, 10), 63)]);

        var result = await _refresh.RefreshAsync(Owner);

        Assert.Equal(new QuoteRefreshResult(1, 1, []), result with { Failed = [] });
        Assert.Empty(result.Failed);
        Assert.Equal([("GOLD.EX", new DateOnly(2024, 1, 9))], _feed.Requests);   // not the sold security
        Assert.Equal(63, await GoldPriceAsync());
        await using var db = await _t.System.CreateDbContextAsync();
        Assert.Equal([Day(1, 10)], await db.MarketQuotes.Select(q => q.Day).ToListAsync());   // nothing the file already has
    }

    [Fact]
    public async Task A_jump_beyond_the_daily_limit_and_a_foreign_currency_are_not_taken()
    {
        await StoreFileAsync(QuotedClient());
        _feed.Series["GOLD.EX"] = new QuoteSeries("GOLD.EX", "EUR", [new MarketClose(Day(1, 10), 620)]);
        await _refresh.RefreshAsync(Owner);
        Assert.Equal(62, await GoldPriceAsync());

        _feed.Series["GOLD.EX"] = new QuoteSeries("GOLD.EX", "USD", [new MarketClose(Day(1, 10), 63)]);
        Assert.Equal(["GOLD.EX"], (await _refresh.RefreshAsync(Owner)).Failed);
        Assert.Equal(62, await GoldPriceAsync());
    }
}
