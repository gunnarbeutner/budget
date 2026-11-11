using System.Globalization;
using OpenBudget.PortfolioPerformance.Proto;
using PType = OpenBudget.PortfolioPerformance.Proto.PTransaction.Types.Type;

namespace OpenBudget.Services;

/// <summary>A security as a portfolio account shows it: identifiers plus its latest price (in <see cref="Currency"/>).</summary>
public sealed record PpValuedSecurity(
    string Uuid, string Name, string? Currency, string? Isin, string? Ticker, string? Wkn, double? Price, long? PriceDay);

/// <summary>One held position: shares × latest price, and the moving-average purchase price per share when known.</summary>
public sealed record PpPosition(PpValuedSecurity Security, double Shares, double? PurchasePrice, string? PurchaseCurrency)
{
    public double Price => Security.Price ?? 0;
    public double Amount => Shares * Price;
}

/// <summary>
/// One booking of a portfolio's ledger. <see cref="Key"/> is stable across uploads: the PortfolioPerformance transaction
/// uuid, suffixed ":fee" / ":tax" for the itemised fee and tax lines.
/// </summary>
public sealed record PpLedgerEntry(string Key, long Timestamp, string Type, double Amount, string? SecurityName, double Shares, string? Note)
{
    /// <summary>"Type: Security (shares) — note", the readable purpose of the booking.</summary>
    public string Purpose
    {
        get
        {
            var purpose = Type;
            if (SecurityName != null)
            {
                purpose += ": " + SecurityName;
                if (Math.Abs(Shares) > 1e-9) purpose += " (" + PortfolioValuation.TrimShares(Shares) + ")";
            }
            if (!string.IsNullOrEmpty(Note)) purpose += " — " + Note;
            return purpose;
        }
    }
}

/// <summary>The value of one security in a portfolio per day (epoch day → value), while it was held.</summary>
public sealed record PpValueSeries(string SecurityUuid, string SecurityName, SortedDictionary<long, double> Values);

public sealed record PpPortfolioValuation(
    string Uuid, string Name, bool Retired, IReadOnlyList<PpPosition> Positions, IReadOnlyList<PpLedgerEntry> Ledger,
    IReadOnlyList<PpValueSeries> History);

public sealed record PpCashAccount(string Uuid, string Name, string Currency, bool Retired, double Balance);

public sealed record PpValuation(string BaseCurrency, IReadOnlyList<PpPortfolioValuation> Portfolios, IReadOnlyList<PpCashAccount> CashAccounts);

/// <summary>
/// Turns a PortfolioPerformance file into what its portfolios look like as accounts: positions (holdings × latest
/// price, with a moving-average purchase price), a ledger of the portfolio's buys, sells, deliveries, dividends,
/// fees and taxes (fees and taxes itemised so the lines net back to PortfolioPerformance's cash amount), and a daily
/// value history (shares held on the day × the historical close). Pure; scaling is shares ÷1e8, money ÷100,
/// prices ÷1e8, and all arithmetic is in doubles, matching the pp-bridge helper this replaces.
/// </summary>
public static class PortfolioValuation
{
    private const double Share = 1e8;
    private const double Money = 100.0;
    private const double Quote = 1e8;

    /// <summary>Label and cash-effect sign of a transaction type on a portfolio ledger, or null for pure cash movements.</summary>
    public static (string Label, double Sign)? LedgerMeta(PType type) => type switch
    {
        PType.Purchase => ("Kauf", -1),
        PType.Sale => ("Verkauf", 1),
        PType.InboundDelivery => ("Einlieferung", -1),
        PType.OutboundDelivery => ("Auslieferung", 1),
        PType.SecurityTransfer => ("Übertrag", 0),
        PType.Dividend => ("Dividende", 1),
        PType.Interest => ("Zinsen", 1),
        PType.InterestCharge => ("Zinsbelastung", -1),
        PType.Tax => ("Steuer", -1),
        PType.TaxRefund => ("Steuererstattung", 1),
        PType.Fee => ("Gebühr", -1),
        PType.FeeRefund => ("Gebührenerstattung", 1),
        PType.CashTransfer or PType.Deposit or PType.Removal => null,
        _ => null,
    };

    public const string FeeLabel = "Gebühr";
    public const string TaxLabel = "Steuer";

    /// <summary>Values every portfolio of <paramref name="client"/>; the history runs up to <paramref name="today"/> (UTC).</summary>
    public static PpValuation Compute(PClient client, DateOnly today)
    {
        var baseCurrency = Str(client.BaseCurrency) ?? "EUR";
        var todayDay = today.DayNumber - DateOnly.FromDateTime(DateTime.UnixEpoch).DayNumber;

        var securities = new Dictionary<string, PpValuedSecurity>(StringComparer.Ordinal);
        var priceHistory = new Dictionary<string, List<(long Day, double Close)>>(StringComparer.Ordinal);
        foreach (var s in client.Securities)
        {
            if (Str(s.Uuid) is not { } uuid) continue;
            double? price = null;
            long? priceDay = null;
            if (s.Latest is { Close: not 0 } latest)
            {
                price = latest.Close / Quote;
                priceDay = latest.Date;
            }
            else if (s.Prices.Count > 0 && s.Prices[^1] is { Close: not 0 } last)
            {
                price = last.Close / Quote;
                priceDay = last.Date;
            }
            securities[uuid] = new PpValuedSecurity(uuid, Str(s.Name) ?? uuid, Str(s.CurrencyCode), Str(s.Isin),
                Str(s.TickerSymbol), Str(s.Wkn), price, priceDay);
            priceHistory[uuid] = s.Prices.Select(p => (p.Date, p.Close / Quote)).OrderBy(p => p.Date).ThenBy(p => p.Item2).ToList();
        }

        var txns = client.Transactions;

        // A security -> portfolio map, so dividends / taxes (which carry a security but no portfolio) land on the
        // ledger of the portfolio that last traded the security.
        var secToPortfolio = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var t in txns)
        {
            if (Str(t.Portfolio) is { } port && Str(t.Security) is { } sec) secToPortfolio[sec] = port;
        }

        var ledgers = new Dictionary<string, List<PpLedgerEntry>>(StringComparer.Ordinal);
        foreach (var t in txns)
        {
            if (LedgerMeta(t.Type) is not { } meta) continue;
            var port = Str(t.Portfolio) ?? (Str(t.Security) is { } s && secToPortfolio.TryGetValue(s, out var p) ? p : null);
            if (port == null) continue;
            var amount = t.Amount / Money;
            var date = t.Date?.Seconds ?? 0;
            var secName = Str(t.Security) is { } su && securities.TryGetValue(su, out var sec) ? sec.Name : null;
            var shares = t.Shares / Share;
            var note = Str(t.Note);
            double fee = 0, tax = 0;
            foreach (var u in t.Units)
            {
                var ua = u.Amount / Money;
                if (u.Type == PTransactionUnit.Types.Type.Fee) fee += ua;
                else if (u.Type == PTransactionUnit.Types.Type.Tax) tax += ua;
            }
            // Net (signed) = main − fee − tax  ⇒  main = sign·amount + fee + tax.
            var key = Str(t.Uuid) ?? FallbackKey(t);
            if (!ledgers.TryGetValue(port, out var list))
            {
                list = [];
                ledgers[port] = list;
            }
            list.Add(new PpLedgerEntry(key, date, meta.Label, meta.Sign * amount + fee + tax, secName, shares, note));
            if (Math.Abs(fee) >= double.Epsilon) list.Add(new PpLedgerEntry(key + ":fee", date, FeeLabel, -fee, secName, 0, note));
            if (Math.Abs(tax) >= double.Epsilon) list.Add(new PpLedgerEntry(key + ":tax", date, TaxLabel, -tax, secName, 0, note));
        }

        // Shares per (portfolio, security) and cash account balances.
        var holdings = new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);
        void AddShares(string port, string sec, double delta)
        {
            if (!holdings.TryGetValue(port, out var bySec))
            {
                bySec = new Dictionary<string, double>(StringComparer.Ordinal);
                holdings[port] = bySec;
            }
            bySec[sec] = bySec.GetValueOrDefault(sec) + delta;
        }
        var balances = new Dictionary<string, double>(StringComparer.Ordinal);
        var accounts = client.Accounts.Where(a => Str(a.Uuid) != null).GroupBy(a => a.Uuid, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);
        foreach (var t in txns)
        {
            var amount = t.Amount / Money;
            var shares = t.Shares / Share;
            if (Str(t.Portfolio) is { } port && Str(t.Security) is { } sec)
            {
                if (t.Type is PType.Purchase or PType.InboundDelivery)
                {
                    AddShares(port, sec, shares);
                }
                else if (t.Type is PType.Sale or PType.OutboundDelivery)
                {
                    AddShares(port, sec, -shares);
                }
                else if (t.Type == PType.SecurityTransfer)
                {
                    AddShares(port, sec, -shares);
                    if (Str(t.OtherPortfolio) is { } other) AddShares(other, sec, shares);
                }
            }
            if (Str(t.Account) is { } acct && accounts.ContainsKey(acct))
            {
                double delta = t.Type switch
                {
                    PType.Purchase or PType.Removal or PType.InterestCharge or PType.Tax or PType.Fee or PType.CashTransfer => -amount,
                    PType.Sale or PType.Deposit or PType.Dividend or PType.Interest or PType.TaxRefund or PType.FeeRefund => amount,
                    PType.InboundDelivery or PType.OutboundDelivery or PType.SecurityTransfer => 0,
                    _ => 0,
                };
                balances[acct] = balances.GetValueOrDefault(acct) + delta;
                if (t.Type == PType.CashTransfer && Str(t.OtherAccount) is { } oacct && accounts.ContainsKey(oacct))
                    balances[oacct] = balances.GetValueOrDefault(oacct) + amount;
            }
        }

        var costBasis = MovingAverageCost(txns);

        var portfolios = new List<PpPortfolioValuation>();
        foreach (var p in client.Portfolios)
        {
            if (Str(p.Uuid) is not { } uuid) continue;
            var positions = new List<PpPosition>();
            foreach (var (su, sh) in holdings.GetValueOrDefault(uuid) ?? [])
            {
                if (Math.Abs(sh) <= 1e-9 || !securities.TryGetValue(su, out var sec)) continue;
                var cost = costBasis.GetValueOrDefault((uuid, su));
                double? purchase = cost > 1e-6 ? cost / sh : null;
                // The cost basis is in the base currency; it only means something next to a price in that currency.
                bool sameCurrency = (sec.Currency ?? baseCurrency) == baseCurrency;
                positions.Add(new PpPosition(sec, sh, sameCurrency ? purchase : null, sameCurrency && purchase != null ? baseCurrency : null));
            }
            positions = positions.OrderByDescending(x => x.Amount).ToList();
            var ledger = (ledgers.GetValueOrDefault(uuid) ?? []).OrderByDescending(e => e.Timestamp).ToList();
            portfolios.Add(new PpPortfolioValuation(uuid, Str(p.Name) ?? uuid, p.IsRetired, positions, ledger,
                History(txns, uuid, client.Securities, priceHistory, todayDay)));
        }

        var cash = accounts.Values
            .Select(a => new PpCashAccount(a.Uuid, Str(a.Name) ?? a.Uuid, Str(a.CurrencyCode) ?? baseCurrency, a.IsRetired, balances.GetValueOrDefault(a.Uuid)))
            .ToList();
        return new PpValuation(baseCurrency, portfolios, cash);
    }

    /// <summary>
    /// Cost basis per (portfolio, security) in the base currency by moving average, in date order: a purchase or
    /// inbound delivery adds its cash amount minus taxes (the fee stays in, as a broker counts it); a sale or outbound
    /// delivery removes the running average × shares; a transfer moves that basis to the receiving portfolio.
    /// </summary>
    private static Dictionary<(string Port, string Sec), double> MovingAverageCost(IEnumerable<PTransaction> txns)
    {
        var cost = new Dictionary<(string, string), double>();
        var shares = new Dictionary<(string, string), double>();
        double Avg((string, string) k)
        {
            var s = shares.GetValueOrDefault(k);
            return s > 1e-12 ? cost.GetValueOrDefault(k) / s : 0;
        }
        foreach (var t in txns.OrderBy(t => t.Date?.Seconds ?? 0))
        {
            if (Str(t.Security) is not { } sec || Str(t.Portfolio) is not { } port) continue;
            var qty = t.Shares / Share;
            var amt = t.Amount / Money;
            var tax = t.Units.Where(u => u.Type == PTransactionUnit.Types.Type.Tax).Sum(u => u.Amount / Money);
            var k = (port, sec);
            if (t.Type is PType.Purchase or PType.InboundDelivery)
            {
                cost[k] = cost.GetValueOrDefault(k) + (amt - tax);
                shares[k] = shares.GetValueOrDefault(k) + qty;
            }
            else if (t.Type is PType.Sale or PType.OutboundDelivery or PType.SecurityTransfer)
            {
                var a = Avg(k);
                cost[k] = cost.GetValueOrDefault(k) - a * qty;
                shares[k] = shares.GetValueOrDefault(k) - qty;
                if (t.Type == PType.SecurityTransfer && Str(t.OtherPortfolio) is { } other)
                {
                    var o = (other, sec);
                    cost[o] = cost.GetValueOrDefault(o) + a * qty;
                    shares[o] = shares.GetValueOrDefault(o) + qty;
                }
            }
        }
        return cost;
    }

    /// <summary>
    /// Daily value per security of one portfolio: from the day of its first share movement up to today, shares held at
    /// the end of the day × the latest historical close on or before it. Days without shares or without any earlier
    /// close have no value.
    /// </summary>
    private static List<PpValueSeries> History(
        IEnumerable<PTransaction> txns, string portfolio, IEnumerable<PSecurity> securities,
        Dictionary<string, List<(long Day, double Close)>> prices, long today)
    {
        var deltas = new Dictionary<string, SortedDictionary<long, double>>(StringComparer.Ordinal);
        foreach (var t in txns)
        {
            if (Str(t.Security) is not { } sec || t.Date == null) continue;
            var sh = t.Shares / Share;
            var day = FloorDay(t.Date.Seconds);
            if (!deltas.TryGetValue(sec, out var d))
            {
                d = new SortedDictionary<long, double>();
                deltas[sec] = d;
            }
            if (Str(t.Portfolio) == portfolio)
            {
                if (t.Type is PType.Purchase or PType.InboundDelivery) d[day] = d.GetValueOrDefault(day) + sh;
                else if (t.Type is PType.Sale or PType.OutboundDelivery or PType.SecurityTransfer) d[day] = d.GetValueOrDefault(day) - sh;
            }
            if (Str(t.OtherPortfolio) == portfolio && t.Type == PType.SecurityTransfer) d[day] = d.GetValueOrDefault(day) + sh;
        }

        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var s in securities) if (Str(s.Uuid) is { } u) names[u] = Str(s.Name) ?? u;

        var result = new List<PpValueSeries>();
        foreach (var (sec, d) in deltas)
        {
            if (d.Count == 0 || !names.TryGetValue(sec, out var name)) continue;
            var hist = prices.GetValueOrDefault(sec) ?? [];
            if (hist.Count == 0) continue;
            var days = hist.Select(h => h.Day).ToList();
            var values = new SortedDictionary<long, double>();
            double cum = 0;
            for (var day = d.Keys.First(); day <= today; day++)
            {
                if (d.TryGetValue(day, out var delta)) cum += delta;
                if (Math.Abs(cum) < 1e-9) continue;
                var j = UpperBound(days, day) - 1;
                if (j < 0) continue;
                values[day] = cum * hist[j].Close;
            }
            if (values.Count > 0) result.Add(new PpValueSeries(sec, name, values));
        }
        return result;
    }

    /// <summary>Index of the first element greater than <paramref name="value"/> (Python's bisect_right).</summary>
    private static int UpperBound(List<long> sorted, long value)
    {
        int lo = 0, hi = sorted.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (sorted[mid] <= value) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    private static long FloorDay(long seconds) => (long)Math.Floor(seconds / 86400.0);

    /// <summary>A share count without trailing zeros (15 → "15", 0.42 → "0.42"), like C's "%g".</summary>
    public static string TrimShares(double x) =>
        Math.Abs(x - Math.Round(x)) < double.Epsilon ? ((long)x).ToString(CultureInfo.InvariantCulture) : x.ToString("G6", CultureInfo.InvariantCulture).Replace('E', 'e');

    private static string FallbackKey(PTransaction t) =>
        $"{t.Type}|{t.Date?.Seconds}|{t.Amount}|{t.Shares}|{t.Security}|{t.Portfolio}";

    private static string? Str(string? s) => string.IsNullOrEmpty(s) ? null : s;
}
