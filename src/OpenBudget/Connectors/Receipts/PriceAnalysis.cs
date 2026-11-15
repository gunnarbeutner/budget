using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.Connectors.Receipts;

/// <summary>One product line of a receipt the user sees, as the price analysis reads it.</summary>
public sealed record PriceLine(long ReceiptId, string Shop, DateOnly Date, string Name, string Quantity, long? UnitCents, long TotalCents, bool IsOwn);

/// <summary>What a product's comparable price is given per.</summary>
public enum PriceUnit { Piece, Kg, Litre }

/// <summary>A size printed in a product name: the pack's content in grams or millilitres.</summary>
public sealed record PackSize(decimal Amount, PriceUnit Unit)
{
    /// <summary>The content in kilograms or litres.</summary>
    public decimal Base => Amount / 1000m;
}

/// <summary>
/// One purchase of a product: the paid price per piece, per kg or litre (weighed items, or pack items whose name carries
/// a reliable size), and the comparable <see cref="Price"/> the product's history uses.
/// </summary>
public sealed record Purchase(long ReceiptId, DateOnly Date, string Name, decimal Quantity, long TotalCents, long? PieceCents, long? BaseCents, PriceUnit BaseUnit, bool Weighed, bool IsOwn)
{
    /// <summary>Per kg for weighed items, else per piece.</summary>
    public long Price => Weighed ? BaseCents ?? 0 : PieceCents ?? 0;
}

/// <summary>A product (per shop) and its purchases, oldest first.</summary>
public sealed record ProductHistory(string Key, string Shop, string Name, IReadOnlyList<Purchase> Purchases, IReadOnlyList<string> MergedKeys)
{
    public bool Weighed => Purchases[^1].Weighed;
    public Purchase First => Purchases[0];
    public Purchase Last => Purchases[^1];
    public int TimesBought => Purchases.Select(p => (p.ReceiptId, p.Date)).Distinct().Count();

    /// <summary>The last price against the last purchase at least twelve months before <paramref name="today"/>; null without one.</summary>
    public decimal? Change12M(DateOnly today)
    {
        var then = today.AddMonths(-12);
        var reference = Purchases.LastOrDefault(p => p.Date <= then);
        return reference is { Price: > 0 } r && Last.Price > 0 && Last.Date > then ? (decimal)Last.Price / r.Price - 1m : null;
    }

    /// <summary>The median number of days between distinct purchase days; null with fewer than three.</summary>
    public double? TypicalIntervalDays(DateOnly? since = null)
    {
        var days = Purchases.Where(p => since == null || p.Date >= since).Select(p => p.Date).Distinct().Order().ToList();
        if (days.Count < 3) return null;
        var gaps = days.Zip(days.Skip(1), (a, b) => (double)(b.DayNumber - a.DayNumber)).Order().ToList();
        return gaps.Count % 2 == 1 ? gaps[gaps.Count / 2] : (gaps[gaps.Count / 2 - 1] + gaps[gaps.Count / 2]) / 2;
    }

    /// <summary>The product's identity as shown and as merges refer to it.</summary>
    public string Id => Shop + "|" + Key;

    /// <summary>Whether the product's pack purchases also carry a price per kg or litre (a size in the name).</summary>
    public bool HasBase => !Weighed && Purchases.Any(p => p.BaseCents != null);

    /// <summary>What <see cref="Purchase.BaseCents"/> is given per.</summary>
    public PriceUnit BaseUnit => Weighed ? PriceUnit.Kg : Purchases.LastOrDefault(p => p.BaseCents != null)?.BaseUnit ?? PriceUnit.Kg;
}

/// <summary>A product bought at least monthly, with what it costs per month.</summary>
public sealed record RegularProduct(string ProductId, string Shop, string Name, double IntervalDays, long MonthlyCents, int Times);

/// <summary>A receipt's payment, for the grocery spend.</summary>
public sealed record ReceiptSpend(string Shop, DateOnly Date, long Cents);

/// <summary>The span the prices page looks at, up to today.</summary>
public enum PricePeriod { SixMonths, TwelveMonths, ThreeYears, All }

/// <summary>One purchase in a product's price chart: its price (per piece or per kg/l) and whether it was an offer.</summary>
public sealed record PricePoint(Purchase Purchase, long Price, bool Offer)
{
    public DateOnly Date => Purchase.Date;
}

/// <summary>A rise of the usual price between two purchases.</summary>
public sealed record PriceRise(DateOnly From, DateOnly To, long Before, long After);

/// <summary>
/// A product within a period: its purchases there (<see cref="Points"/>), the usual price at the period's start
/// (<see cref="Base"/>: the last usual price before it, or the first in it) and now, the change between them, the price
/// rises, the cheapest purchase, the average price and the spend per month.
/// </summary>
public sealed record ProductDetail(IReadOnlyList<PricePoint> Points, IReadOnlyList<PriceRise> Rises, long Base, long Now, decimal? Change,
    PricePoint Cheapest, long Average, long MonthlyCents);

/// <summary>A product's row on the prices page for a period.</summary>
public sealed record ProductRow(ProductHistory Product, ProductDetail Detail, double? IntervalDays, int Times, long Spend);

/// <summary>One point of the basket index: 100 is the basket's price at the period's start.</summary>
public sealed record IndexPoint(DateOnly Date, decimal Value);

/// <summary>The basket index of all shops (<see cref="Shop"/> null) or one shop, and how many products it follows.</summary>
public sealed record IndexLine(string? Shop, IReadOnlyList<IndexPoint> Points, int Products)
{
    public decimal Latest => Points[^1].Value;
}

/// <summary>Everything the prices page shows for a period.</summary>
public sealed record PriceSummary(DateOnly Start, DateOnly Today, IReadOnlyList<ProductRow> Rows, IReadOnlyList<IndexLine> Index,
    IReadOnlyList<RegularProduct> Regulars, long? GroceryMonthlyCents, ProductRow? BiggestJump)
{
    /// <summary>The basket index of all shops, or null when too few products were bought often enough.</summary>
    public IndexLine? Overall => Index.FirstOrDefault(l => l.Shop == null);
}

/// <summary>
/// Price history from itemised receipts: product identity (<see cref="ProductKey"/>), sizes printed in names
/// (<see cref="ParseSize"/>), unit prices, offers, the basket index, regular purchases and the grocery spend of a
/// period. Pure functions over <see cref="PriceLine"/>s; <see cref="PriceHistoryService"/> loads and caches them per
/// project.
/// </summary>
public static partial class PriceAnalysis
{

    // A size: an optional multipack count ("6x"), an amount and a unit ("500g", "1,5 l", "0.33l").
    [GeneratedRegex(@"(?<![\w.,])(?:(?<n>\d{1,2})\s*[x×]\s*)?(?<v>\d+(?:[.,]\d+)?)\s*(?<u>kg|g|gr|ml|cl|l|ltr|liter)(?![a-z])", RegexOptions.ExplicitCapture | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex SizePattern();

    // Trailing pack noise: sizes, piece counts ("10 st", "4er"), "ca." and a lone "x".
    [GeneratedRegex(@"(?:\s+(?:(?:\d{1,2}\s*[x×]\s*)?\d+(?:[.,]\d+)?\s*(?:kg|g|gr|ml|cl|l|ltr|liter|st|stk|stueck|er)\.?|ca\.?|x))+\s*$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex TrailingNoise();

    [GeneratedRegex(@"[^a-z0-9]+", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex NonAlnum();

    /// <summary>
    /// A product's key within its shop: the name folded (case, umlauts, diacritics), trailing pack sizes and counts
    /// dropped, punctuation turned into single spaces; for shops with long titles cut to their first
    /// <see cref="ReceiptSourceProfile.ProductKeyLength"/> characters at a word boundary.
    /// </summary>
    public static string ProductKey(string shop, string name)
    {
        var folded = SearchText.Fold(name).Trim();
        var stripped = TrailingNoise().Replace(folded, "");
        if (stripped.Length >= 3) folded = stripped;
        var key = NonAlnum().Replace(folded, " ").Trim();
        if (ReceiptSourceProfiles.Default.For(shop)?.ProductKeyLength is { } length && key.Length > length)
        {
            var cut = key.LastIndexOf(' ', length, StringComparison.Ordinal);
            key = key[..(cut > 20 ? cut : length)];
        }
        return key;
    }

    /// <summary>
    /// The pack size a name carries ("500g", "1,5 l", "6x0,33l" = 1980 ml), or null when it carries none, several
    /// differing ones, or an implausible amount (below 5 g/ml or above 25 kg/l).
    /// </summary>
    public static PackSize? ParseSize(string name)
    {
        var folded = SearchText.Fold(name);
        PackSize? found = null;
        foreach (Match m in SizePattern().Matches(folded))
        {
            if (!decimal.TryParse(m.Groups["v"].Value.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var v)) return null;
            var count = m.Groups["n"].Success ? int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture) : 1;
            var (amount, unit) = m.Groups["u"].Value switch
            {
                "kg" => (v * 1000m, PriceUnit.Kg),
                "g" or "gr" => (v, PriceUnit.Kg),
                "ml" => (v, PriceUnit.Litre),
                "cl" => (v * 10m, PriceUnit.Litre),
                _ => (v * 1000m, PriceUnit.Litre),
            };
            var size = new PackSize(amount * count, unit);
            if (found != null && found != size) return null;
            found = size;
        }
        return found is { Amount: >= 5m and <= 25000m } ? found : null;
    }

    /// <summary>
    /// The purchase a line stands for, or null for a line without a positive amount or quantity. A quantity in grams
    /// ("958g") or a fractional count ("0.521", kilograms) is weighed: its unit price is per kg. Otherwise the price per
    /// piece is the unit price, or the total over the quantity; with a reliable size in the name also per kg or litre.
    /// </summary>
    public static Purchase? ToPurchase(PriceLine line)
    {
        if (line.TotalCents <= 0) return null;
        var q = line.Quantity.Trim().Replace(',', '.');
        if (q.EndsWith('g', StringComparison.Ordinal) && decimal.TryParse(q[..^1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var grams))
            return grams <= 0 ? null : Weighed(line, grams / 1000m);
        if (!decimal.TryParse(q, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var count) || count <= 0) return null;
        if (count != decimal.Truncate(count)) return Weighed(line, count);
        var piece = line.UnitCents is > 0 and var u ? u : (long)Math.Round(line.TotalCents / count);
        var size = ParseSize(line.Name);
        long? perBase = size == null ? null : (long)Math.Round(piece / size.Base);
        return new Purchase(line.ReceiptId, line.Date, line.Name, count, line.TotalCents, piece, perBase, size?.Unit ?? PriceUnit.Kg, false, line.IsOwn);
    }

    private static Purchase Weighed(PriceLine line, decimal kg)
    {
        var perKg = line.UnitCents is > 0 and var u ? u : (long)Math.Round(line.TotalCents / kg);
        return new Purchase(line.ReceiptId, line.Date, line.Name, kg, line.TotalCents, null, perKg, PriceUnit.Kg, true, line.IsOwn);
    }

    /// <summary>
    /// Groups lines into products per shop. <paramref name="merges"/> maps a product id ("shop|key") to the product it
    /// was merged into (followed transitively). A product shows its latest name; when its purchases mix weighed and
    /// pack lines, only those of the latest kind form its history.
    /// </summary>
    public static List<ProductHistory> Products(IEnumerable<PriceLine> lines, IReadOnlyDictionary<string, string>? merges = null)
    {
        var groups = new Dictionary<string, (string Shop, string Key, List<Purchase> Purchases, HashSet<string> Keys)>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            if (ToPurchase(line) is not { } p) continue;
            var key = ProductKey(line.Shop, line.Name);
            if (key.Length == 0) continue;
            var own = line.Shop + "|" + key;
            var target = Resolve(own, merges);
            if (!groups.TryGetValue(target, out var g))
                groups[target] = g = (line.Shop, target[(line.Shop.Length + 1)..], [], new HashSet<string>(StringComparer.Ordinal));
            g.Purchases.Add(p);
            g.Keys.Add(own);
        }
        return groups.Values.Select(g =>
        {
            var ordered = g.Purchases.OrderBy(p => p.Date).ThenBy(p => p.ReceiptId).ToList();
            var weighed = ordered[^1].Weighed;
            var kept = ordered.Where(p => p.Weighed == weighed).ToList();
            return new ProductHistory(g.Key, g.Shop, kept[^1].Name, kept, g.Keys.Order(StringComparer.Ordinal).ToList());
        }).OrderByDescending(p => p.Last.Date).ThenBy(p => p.Name, StringComparer.CurrentCulture).ToList();
    }

    /// <summary>Follows merges from a product id to the product it ends up in (cycles stop where they close).</summary>
    public static string Resolve(string id, IReadOnlyDictionary<string, string>? merges)
    {
        if (merges == null) return id;
        var seen = new HashSet<string>(StringComparer.Ordinal) { id };
        while (merges.TryGetValue(id, out var next) && seen.Add(next)) id = next;
        return id;
    }

    /// <summary>The first day a period covers: for <see cref="PricePeriod.All"/> the first purchase or receipt.</summary>
    public static DateOnly PeriodStart(PricePeriod period, DateOnly today, DateOnly? first) => period switch
    {
        PricePeriod.SixMonths => today.AddMonths(-6),
        PricePeriod.TwelveMonths => today.AddMonths(-12),
        PricePeriod.ThreeYears => today.AddMonths(-36),
        _ => first is { } f && f < today ? f : today,
    };

    /// <summary>The months between two days (at least one), for spend per month.</summary>
    public static double Months(DateOnly from, DateOnly to) => Math.Max(1.0, (to.DayNumber - from.DayNumber + 1) / 30.44);

    /// <summary>A purchase costing at most this share of the usual price around it is an offer.</summary>
    public const decimal OfferFactor = 0.9m;

    /// <summary>How many purchases on each side of a purchase make up its usual price.</summary>
    private const int Neighbours = 3;

    /// <summary>
    /// Which of the prices (oldest first) are offers: at most <see cref="OfferFactor"/> of the usual price on both sides,
    /// the median of up to three purchases before and after. At either end the one side decides, with at least two
    /// purchases. A price rise therefore does not turn the earlier, cheaper purchases into offers.
    /// </summary>
    public static bool[] Offers(IReadOnlyList<long> prices)
    {
        var result = new bool[prices.Count];
        for (var i = 0; i < prices.Count; i++)
        {
            var before = prices.Skip(Math.Max(0, i - Neighbours)).Take(Math.Min(i, Neighbours)).ToList();
            var after = prices.Skip(i + 1).Take(Neighbours).ToList();
            long? usual = (before.Count, after.Count) switch
            {
                ( > 0, > 0) => Math.Min(Median(before), Median(after)),
                ( >= 2, 0) => Median(before),
                (0, >= 2) => Median(after),
                _ => null,
            };
            result[i] = usual is { } u && prices[i] <= u * OfferFactor;
        }
        return result;
    }

    private static long Median(List<long> values)
    {
        var o = values.Order().ToList();
        return o.Count % 2 == 1 ? o[o.Count / 2] : (o[o.Count / 2 - 1] + o[o.Count / 2]) / 2;
    }

    /// <summary>A change of the usual price of at least this share counts as a rise.</summary>
    private const decimal RiseThreshold = 0.03m;

    /// <summary>
    /// A product between <paramref name="start"/> and <paramref name="today"/>, priced per piece (per kg for weighed
    /// items) or, with <paramref name="perBase"/>, per kg or litre (purchases without a size are left out). Offers are
    /// told from the whole history; the usual price, its rises and the change leave them out. Null when the product
    /// has no priced purchase in the period.
    /// </summary>
    public static ProductDetail? Detail(ProductHistory p, DateOnly start, DateOnly today, bool perBase = false)
    {
        var priced = p.Purchases.Select(x => (Purchase: x, Price: perBase ? x.BaseCents ?? 0 : x.Price)).Where(x => x.Price > 0).ToList();
        var offers = Offers(priced.Select(x => x.Price).ToList());
        var all = priced.Select((x, i) => new PricePoint(x.Purchase, x.Price, offers[i])).ToList();
        var points = all.Where(x => x.Date >= start && x.Date <= today).ToList();
        if (points.Count == 0) return null;
        var usual = all.Where(x => !x.Offer && x.Date <= today).ToList();
        var baseline = usual.LastOrDefault(x => x.Date < start) ?? usual.FirstOrDefault(x => x.Date >= start) ?? points[0];
        var now = usual.LastOrDefault() ?? points[^1];
        var rises = new List<PriceRise>();
        for (var i = 1; i < usual.Count; i++)
        {
            var (a, b) = (usual[i - 1], usual[i]);
            if (b.Date >= start && b.Price >= a.Price * (1 + RiseThreshold))
                rises.Add(new PriceRise(a.Date < start ? start : a.Date, b.Date, a.Price, b.Price));
        }
        decimal? change = now.Date > baseline.Date ? (decimal)now.Price / baseline.Price - 1m : null;
        var cheapest = points.OrderBy(x => x.Price).ThenByDescending(x => x.Date).First();
        var average = (long)Math.Round(points.Average(x => (decimal)x.Price));
        var monthly = (long)Math.Round(points.Sum(x => x.Purchase.TotalCents) / Months(points[0].Date, today));
        return new ProductDetail(points, rises, baseline.Price, now.Price, change, cheapest, average, monthly);
    }

    /// <summary>Products bought at least this often in each half of the period take part in the index.</summary>
    public const int IndexMinPurchases = 3;

    /// <summary>
    /// The basket index from <paramref name="start"/> to <paramref name="today"/>, overall and per shop: for every
    /// product bought at least <see cref="IndexMinPurchases"/> times in each half of the period, its usual price (offers
    /// left out) at each month's end over its usual price at the start, averaged weighted by the product's spend in the
    /// period. One point at the start (100), one per month end and one today. Empty without such a product.
    /// </summary>
    public static List<IndexLine> IndexSeries(IReadOnlyList<ProductHistory> products, DateOnly start, DateOnly today)
    {
        var mid = DateOnly.FromDayNumber((start.DayNumber + today.DayNumber) / 2);
        var basket = new List<BasketItem>();
        foreach (var p in products)
        {
            var priced = p.Purchases.Where(x => x.Price > 0 && x.Date <= today).ToList();
            if (priced.Count(x => x.Date >= start && x.Date < mid) < IndexMinPurchases || priced.Count(x => x.Date >= mid) < IndexMinPurchases) continue;
            var offers = Offers(priced.Select(x => x.Price).ToList());
            var usual = priced.Where((_, i) => !offers[i]).ToList();
            var baseline = usual.LastOrDefault(x => x.Date <= start) ?? usual.FirstOrDefault(x => x.Date >= start);
            if (baseline == null) continue;
            basket.Add(new BasketItem(p.Shop, usual, baseline.Price, Math.Max(1, priced.Where(x => x.Date >= start).Sum(x => x.TotalCents))));
        }
        if (basket.Count == 0) return [];
        var dates = new List<DateOnly> { start };
        for (var m = new DateOnly(start.Year, start.Month, 1).AddMonths(1).AddDays(-1); m < today; m = m.AddDays(1).AddMonths(1).AddDays(-1))
            if (m > start) dates.Add(m);
        if (today > start) dates.Add(today);

        var lines = new List<IndexLine> { Line(null, basket, dates) };
        foreach (var g in basket.GroupBy(b => b.Shop, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
            lines.Add(Line(g.Key, g.ToList(), dates));
        return lines;
    }

    private sealed record BasketItem(string Shop, List<Purchase> Usual, long Base, long Weight);

    private static IndexLine Line(string? shop, List<BasketItem> items, List<DateOnly> dates)
    {
        var weight = items.Sum(i => (decimal)i.Weight);
        var points = dates.Select((d, n) => new IndexPoint(d, n == 0 ? 100m : Math.Round(100m * items.Sum(i =>
            i.Weight * (decimal)(i.Usual.LastOrDefault(x => x.Date <= d)?.Price ?? i.Base) / i.Base) / weight, 1))).ToList();
        return new IndexLine(shop, points, items.Count);
    }

    /// <summary>The shops whose receipts count as grocery shopping (see <see cref="ReceiptSourceProfile.Groceries"/>).</summary>
    public static readonly IReadOnlySet<string> GroceryShops = ReceiptSourceProfiles.Default.GroceryShops;

    /// <summary>
    /// The average grocery spend per month: the payments of grocery shops' receipts (<see cref="GroceryShops"/>) from
    /// <paramref name="start"/> to <paramref name="today"/>, over the months since the first of them. Null without one.
    /// </summary>
    public static long? GroceryMonthly(IEnumerable<ReceiptSpend> receipts, DateOnly start, DateOnly today)
    {
        var groceries = receipts.Where(r => GroceryShops.Contains(r.Shop) && r.Date >= start && r.Date <= today && r.Cents > 0).ToList();
        if (groceries.Count == 0) return null;
        return (long)Math.Round(groceries.Sum(r => r.Cents) / Months(groceries.Min(r => r.Date), today));
    }

    /// <summary>The product whose usual price rose the most in the period, bought at least twice in it; null without a rise.</summary>
    public static ProductRow? BiggestJump(IEnumerable<ProductRow> rows) =>
        rows.Where(r => r.Times >= 2 && r.Detail.Change > 0).OrderByDescending(r => r.Detail.Change).ThenByDescending(r => r.Spend).FirstOrDefault();

    /// <summary>The prices page of a period: the products bought in it, the basket index, regulars, grocery spend and biggest jump.</summary>
    public static PriceSummary Summarise(IReadOnlyList<ProductHistory> products, IReadOnlyList<ReceiptSpend> receipts, PricePeriod period, DateOnly today)
    {
        var firsts = products.Select(p => p.First.Date).Concat(receipts.Select(r => r.Date)).ToList();
        var start = PeriodStart(period, today, firsts.Count == 0 ? null : firsts.Min());
        var rows = new List<ProductRow>();
        foreach (var p in products)
        {
            if (Detail(p, start, today) is not { } d) continue;
            var times = d.Points.Select(x => (x.Purchase.ReceiptId, x.Date)).Distinct().Count();
            rows.Add(new ProductRow(p, d, p.TypicalIntervalDays(start), times, d.Points.Sum(x => x.Purchase.TotalCents)));
        }
        return new PriceSummary(start, today, rows, IndexSeries(products, start, today), Regulars(products, today, start),
            GroceryMonthly(receipts, start, today), BiggestJump(rows));
    }

    /// <summary>
    /// Regular purchases: products bought on at least three days since <paramref name="since"/> (default: twelve months
    /// before <paramref name="today"/>), typically at most a month apart, and bought within the last two months; with
    /// their spend per month since their first purchase in that window.
    /// </summary>
    public static List<RegularProduct> Regulars(IReadOnlyList<ProductHistory> products, DateOnly today, DateOnly? since = null)
    {
        var from = since ?? today.AddMonths(-12);
        var result = new List<RegularProduct>();
        foreach (var p in products)
        {
            if (p.Last.Date < today.AddMonths(-2) || p.TypicalIntervalDays(from) is not { } interval || interval > 31) continue;
            var recent = p.Purchases.Where(x => x.Date >= from).ToList();
            result.Add(new RegularProduct(p.Id, p.Shop, p.Name, interval, (long)Math.Round(recent.Sum(x => x.TotalCents) / Months(recent[0].Date, today)),
                recent.Select(x => (x.ReceiptId, x.Date)).Distinct().Count()));
        }
        return result.OrderByDescending(r => r.MonthlyCents).ToList();
    }

    /// <summary>A text built of the product's names for searching (folded).</summary>
    public static string SearchKey(ProductHistory p)
    {
        var sb = new StringBuilder(p.Key);
        foreach (var n in p.Purchases.Select(x => x.Name).Distinct(StringComparer.Ordinal)) sb.Append(' ').Append(SearchText.Fold(n));
        return sb.ToString();
    }
}
