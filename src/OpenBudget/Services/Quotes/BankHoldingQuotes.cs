using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;

namespace OpenBudget.Services.Quotes;

/// <summary>
/// Brings the prices of bank-depot holdings (connector, FinTS and MoneyMoney depots — every holding not written by the
/// PortfolioPerformance sync) up to the stored <see cref="MarketQuote"/> closes. Such a holding is priced as of its
/// depot's last successful refresh (<see cref="Account.RefreshTimestamp"/>) unless it carries its own price day.
/// <para>
/// Symbol resolution: a holding's ISIN names the owner's PortfolioPerformance security with that ISIN
/// (<see cref="PpSecurity"/>), whose ticker is the Yahoo symbol the quotes are stored under. A close is used when it is
/// newer than the holding's price, in the holding's price currency, and within <see cref="QuoteRefreshService.MaxDailyMove"/>
/// of the bank's price (which keeps out a ticker quoting other units). Quantity stays the bank's; value scales with the price.
/// </para>
/// </summary>
public static class BankHoldingQuotes
{
    /// <summary>Whether <paramref name="h"/> comes from a bank depot rather than the PortfolioPerformance sync.</summary>
    public static bool IsBankHolding(Holding h) => string.IsNullOrEmpty(h.PpSecurityUuid);

    /// <summary>The Yahoo symbol per ISIN for the owner's PortfolioPerformance securities that have a ticker.</summary>
    public static async Task<Dictionary<string, string>> SymbolsByIsinAsync(BudgetDbContext db, string ownerId, IReadOnlyCollection<string> isins, CancellationToken ct)
    {
        if (isins.Count == 0) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var rows = await db.PpSecurities.AsNoTracking()
            .Where(s => s.OwnerUserId == ownerId && isins.Contains(s.Isin) && s.TickerSymbol != null && s.TickerSymbol != "")
            .Select(s => new { s.Isin, s.TickerSymbol })
            .ToListAsync(ct);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rows) result.TryAdd(r.Isin, r.TickerSymbol!.Trim());
        return result;
    }

    /// <summary>
    /// Fills the price day of the bank holdings among <paramref name="holdings"/> and applies newer stored closes to them,
    /// in place. Returns the holdings a close can be applied to at all (they have a resolved, stored symbol).
    /// </summary>
    public static async Task<HashSet<Holding>> ApplyAsync(BudgetDbContext db, string ownerId, IReadOnlyList<Holding> holdings, CancellationToken ct)
    {
        var bank = holdings.Where(IsBankHolding).ToList();
        if (bank.Count == 0) return [];

        var accountIds = bank.Select(h => h.AccountId).Distinct().ToList();
        var refreshed = await db.Accounts.AsNoTracking()
            .Where(a => accountIds.Contains(a.Id) && a.RefreshTimestamp != null)
            .ToDictionaryAsync(a => a.Id, a => a.RefreshTimestamp!.Value, ct);

        var isins = bank.Where(h => !string.IsNullOrEmpty(h.Isin)).Select(h => h.Isin!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var symbols = await SymbolsByIsinAsync(db, ownerId, isins, ct);
        var wanted = symbols.Values.Distinct(StringComparer.Ordinal).ToList();
        var latest = wanted.Count == 0
            ? []
            : (await db.MarketQuotes.AsNoTracking().Where(q => wanted.Contains(q.Symbol)).ToListAsync(ct))
                .GroupBy(q => (q.Symbol, q.Currency))
                .Select(g => g.MaxBy(q => q.Day)!)
                .ToList();

        return Apply(bank, refreshed, symbols, latest);
    }

    /// <summary>
    /// <see cref="ApplyAsync"/> over loaded data: <paramref name="refreshedAt"/> is each account's last refresh (Unix
    /// seconds), <paramref name="symbolByIsin"/> the resolved symbols and <paramref name="latestCloses"/> the newest close
    /// per symbol and currency.
    /// </summary>
    public static HashSet<Holding> Apply(
        IEnumerable<Holding> holdings, IReadOnlyDictionary<long, long> refreshedAt,
        IReadOnlyDictionary<string, string> symbolByIsin, IReadOnlyCollection<MarketQuote> latestCloses)
    {
        var quotable = new HashSet<Holding>(ReferenceEqualityComparer.Instance);
        foreach (var h in holdings.Where(IsBankHolding))
        {
            if (h.PriceTime == null && refreshedAt.TryGetValue(h.AccountId, out var at)) h.PriceTime = at - Mod(at, 86400);

            if (string.IsNullOrEmpty(h.Isin) || !symbolByIsin.TryGetValue(h.Isin, out var symbol)) continue;
            var currency = h.PriceCurrency ?? h.AmountCurrency;
            var close = latestCloses.FirstOrDefault(q => q.Symbol == symbol && string.Equals(q.Currency, currency, StringComparison.OrdinalIgnoreCase));
            if (close == null) continue;
            quotable.Add(h);

            var closeTime = close.Day * 86400;
            if (h.PriceTime is { } priceTime && closeTime <= priceTime) continue;
            if (h.Price is { } price && price > 0)
            {
                var ratio = close.Close / price;
                if (ratio > QuoteRefreshService.MaxDailyMove || 1 / ratio > QuoteRefreshService.MaxDailyMove) continue;
                if (h.Amount is { } amount) h.Amount = amount * ratio;
                else if (SameCurrency(h.AmountCurrency, close.Currency)) h.Amount = h.Quantity * close.Close;
            }
            else if (SameCurrency(h.AmountCurrency, close.Currency)) h.Amount = h.Quantity * close.Close;
            else continue;
            h.Price = close.Close;
            h.PriceCurrency ??= close.Currency;
            h.PriceTime = closeTime;
        }
        return quotable;
    }

    private static bool SameCurrency(string? a, string? b) => a == null || string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static long Mod(long a, long m) => ((a % m) + m) % m;
}
