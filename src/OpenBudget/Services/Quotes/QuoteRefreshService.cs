using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;
using OpenBudget.PortfolioPerformance.Proto;

namespace OpenBudget.Services.Quotes;

/// <summary>What a refresh did: how many symbols it asked for, how many answered, and the ones that did not.</summary>
public sealed record QuoteRefreshResult(int Symbols, int Updated, IReadOnlyList<string> Failed);

/// <summary>
/// Keeps the prices of a user's PortfolioPerformance securities current between saves of the file: for every security
/// still held whose history comes from Yahoo (<see cref="QuoteSymbols"/>), it fetches the closes after the file's newest
/// price, stores them (<see cref="MarketQuote"/>) and recomputes the user's portfolio accounts, which merge them in.
/// A close that moves more than <see cref="MaxDailyMove"/> against the day before is dropped as a feed glitch.
/// </summary>
public sealed class QuoteRefreshService : IDisposable
{
    /// <summary>The largest factor between two consecutive closes that is taken as real.</summary>
    public const double MaxDailyMove = 1.5;

    /// <summary>Closes re-fetched before the newest known one, so a provisional price of the running day is replaced.</summary>
    private const int Overlap = 5;

    /// <summary>How far back a security without any price in the file is filled.</summary>
    private const int FirstFetchDays = 30;

    private readonly SystemDbContextFactory _dbFactory;
    private readonly IPortfolioFileSource _files;
    private readonly PortfolioAccountSync _accounts;
    private readonly IQuoteFeed _feed;
    private readonly TimeProvider _clock;
    private readonly ILogger<QuoteRefreshService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public QuoteRefreshService(
        SystemDbContextFactory dbFactory, IPortfolioFileSource files, PortfolioAccountSync accounts, IQuoteFeed feed,
        TimeProvider clock, ILogger<QuoteRefreshService> logger)
    {
        _dbFactory = dbFactory;
        _files = files;
        _accounts = accounts;
        _feed = feed;
        _clock = clock;
        _logger = logger;
    }

    public void Dispose() => _gate.Dispose();

    /// <summary>The users with a PortfolioPerformance file shown as accounts.</summary>
    public async Task<List<string>> OwnersAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.PpPortfolioSettings.AsNoTracking().Select(s => s.OwnerUserId).Distinct().ToListAsync(ct);
    }

    /// <summary>Fetches the quotes of the user's held securities and recomputes their portfolio accounts.</summary>
    public async Task<QuoteRefreshResult> RefreshAsync(string ownerId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await RefreshCoreAsync(ownerId, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<QuoteRefreshResult> RefreshCoreAsync(string ownerId, CancellationToken ct)
    {
        PClient client;
        await using (var file = await _files.OpenLatestAsync(ownerId, ct))
        {
            if (file == null) return new QuoteRefreshResult(0, 0, []);
            client = PortfolioPerformanceFile.ReadClient(file);
        }

        var today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);
        var todayDay = EpochDay(today);
        var held = PortfolioValuation.Compute(client, today).Portfolios
            .SelectMany(p => p.Positions).Where(p => Math.Abs(p.Shares) > 1e-9)
            .Select(p => p.Security.Uuid).ToHashSet(StringComparer.Ordinal);

        // One fetch per symbol, from the oldest "newest price" among the securities quoting it.
        var wanted = new Dictionary<string, (string Currency, long NewestDay, double? NewestClose)>(StringComparer.Ordinal);
        foreach (var s in client.Securities)
        {
            if (!held.Contains(s.Uuid) || QuoteSymbols.YahooSymbol(s) is not { } symbol) continue;
            var (day, close) = NewestPrice(s);
            if (!wanted.TryGetValue(symbol, out var w) || day < w.NewestDay) wanted[symbol] = (s.CurrencyCode, day ?? todayDay - FirstFetchDays, close);
        }
        if (wanted.Count == 0) return new QuoteRefreshResult(0, 0, []);

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var symbols = wanted.Keys.ToList();
        var stored = await db.MarketQuotes.Where(q => symbols.Contains(q.Symbol)).ToListAsync(ct);
        var now = _clock.GetUtcNow().ToUnixTimeSeconds();
        var failed = new List<string>();
        var updated = 0;
        foreach (var (symbol, w) in wanted.OrderBy(w => w.Key, StringComparer.Ordinal))
        {
            var mine = stored.Where(q => q.Symbol == symbol && q.Currency == w.Currency).OrderBy(q => q.Day).ToList();
            var known = Math.Max(w.NewestDay, mine.Count > 0 ? mine[^1].Day : long.MinValue);
            var from = DateOnly.FromDayNumber((int)Math.Max(w.NewestDay, known - Overlap) + UnixEpoch.DayNumber);
            QuoteSeries? series;
            try
            {
                series = await _feed.FetchAsync(symbol, from, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException && !ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Fetching quotes of {Symbol} failed.", symbol);
                failed.Add(symbol);
                continue;
            }
            if (series == null || !string.Equals(series.Currency, w.Currency, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("No usable quotes for {Symbol} (currency {Currency}, expected {Expected}).", symbol, series?.Currency, w.Currency);
                failed.Add(symbol);
                continue;
            }

            var previous = mine.Where(q => q.Day < (series.Closes.Count > 0 ? series.Closes[0].Day : long.MaxValue)).Select(q => (double?)q.Close).LastOrDefault();
            previous ??= w.NewestClose;
            var byDay = mine.ToDictionary(q => q.Day);
            foreach (var c in series.Closes.Where(c => c.Day > w.NewestDay && c.Day <= todayDay))
            {
                if (previous is { } p && (c.Close / p > MaxDailyMove || p / c.Close > MaxDailyMove))
                {
                    _logger.LogWarning("Dropped the close {Close} of {Symbol} on day {Day}: too far from {Previous}.", c.Close, symbol, c.Day, p);
                    continue;
                }
                previous = c.Close;
                if (byDay.TryGetValue(c.Day, out var row))
                {
                    row.Close = c.Close;
                    row.FetchedAt = now;
                }
                else
                {
                    row = new MarketQuote { Symbol = symbol, Day = c.Day, Close = c.Close, Currency = w.Currency, FetchedAt = now };
                    db.MarketQuotes.Add(row);
                    byDay[c.Day] = row;
                }
            }
            updated++;
        }
        await db.SaveChangesAsync(ct);
        _logger.LogInformation("Refreshed quotes of user {OwnerUserId}: {Updated} of {Symbols} symbols.", ownerId, updated, wanted.Count);

        await _accounts.SyncAsync(ownerId, ct);
        return new QuoteRefreshResult(wanted.Count, updated, failed);
    }

    /// <summary>The newest price the file has for a security: its latest quote or the last close of its history.</summary>
    private static (long? Day, double? Close) NewestPrice(PSecurity s)
    {
        (long? Day, double? Close) newest = (null, null);
        if (s.Prices.Count > 0)
        {
            var last = s.Prices.MaxBy(p => p.Date)!;
            newest = (last.Date, last.Close / 1e8);
        }
        if (s.Latest is { Close: not 0 } l && (newest.Day == null || l.Date > newest.Day)) newest = (l.Date, l.Close / 1e8);
        return newest;
    }

    private static readonly DateOnly UnixEpoch = DateOnly.FromDateTime(DateTime.UnixEpoch);

    private static long EpochDay(DateOnly day) => day.DayNumber - UnixEpoch.DayNumber;
}
