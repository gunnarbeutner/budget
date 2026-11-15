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
}

/// <summary>A price index row: a shop (or all shops, <see cref="Shop"/> null) between two periods.</summary>
public sealed record PriceIndexRow(string Label, string? Shop, decimal Change, int Products, long Spend);

/// <summary>One product's average price change between the index's periods.</summary>
public sealed record PriceMover(string ProductId, string Shop, string Name, long Before, long After, decimal Change, long Spend);

/// <summary>The personal inflation: per period pair, per shop and overall; and the trailing period's biggest movers.</summary>
public sealed record PriceIndex(IReadOnlyList<PriceIndexRow> Rows, IReadOnlyList<PriceMover> Increases, IReadOnlyList<PriceMover> Decreases);

/// <summary>A product bought at least monthly, with what it costs per month.</summary>
public sealed record RegularProduct(string ProductId, string Shop, string Name, double IntervalDays, long MonthlyCents, int Times);

/// <summary>
/// Price history from itemised receipts: product identity (<see cref="ProductKey"/>), sizes printed in names
/// (<see cref="ParseSize"/>), unit prices, the personal price index and regular purchases. Pure functions over
/// <see cref="PriceLine"/>s; <see cref="PriceHistoryService"/> loads and caches them per user.
/// </summary>
public static partial class PriceAnalysis
{
    /// <summary>Amazon titles are cut to this many characters (at a word) to form their key.</summary>
    public const int AmazonKeyLength = 50;

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
    /// dropped, punctuation turned into single spaces; Amazon titles additionally cut to their first
    /// <see cref="AmazonKeyLength"/> characters at a word boundary.
    /// </summary>
    public static string ProductKey(string shop, string name)
    {
        var folded = SearchText.Fold(name).Trim();
        var stripped = TrailingNoise().Replace(folded, "");
        if (stripped.Length >= 3) folded = stripped;
        var key = NonAlnum().Replace(folded, " ").Trim();
        if (shop == ReceiptShops.Amazon && key.Length > AmazonKeyLength)
        {
            var cut = key.LastIndexOf(' ', AmazonKeyLength, StringComparison.Ordinal);
            key = key[..(cut > 20 ? cut : AmazonKeyLength)];
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

    /// <summary>Products bought at least this often in each period take part in the index.</summary>
    public const int IndexMinPurchases = 3;

    /// <summary>
    /// The personal price index: for every product bought at least <see cref="IndexMinPurchases"/> times in both
    /// periods, its average price in the later period over the earlier one, weighted by its spend in the earlier
    /// period (a fixed basket). Rows: the trailing twelve months against the twelve before, then each calendar year
    /// against the one before; each overall and per shop. Movers come from the trailing comparison.
    /// </summary>
    public static PriceIndex Index(IReadOnlyList<ProductHistory> products, DateOnly today, int movers = 10)
    {
        var rows = new List<PriceIndexRow>();
        var end = today.AddDays(1);
        var mid = end.AddMonths(-12);
        var start = mid.AddMonths(-12);
        var trailing = Compare(products, start, mid, end);
        rows.AddRange(Rows("12M", trailing));
        if (products.Count > 0)
        {
            var first = products.Min(p => p.First.Date.Year);
            for (var y = today.Year; y > first; y--)
                rows.AddRange(Rows(y.ToString(CultureInfo.InvariantCulture), Compare(products, new DateOnly(y - 1, 1, 1), new DateOnly(y, 1, 1), new DateOnly(y + 1, 1, 1))));
        }
        var up = trailing.Where(m => m.Change > 0).OrderByDescending(m => m.Change).Take(movers).ToList();
        var down = trailing.Where(m => m.Change < 0).OrderBy(m => m.Change).Take(movers).ToList();
        return new PriceIndex(rows, up, down);
    }

    private static List<PriceMover> Compare(IReadOnlyList<ProductHistory> products, DateOnly start, DateOnly mid, DateOnly end)
    {
        var result = new List<PriceMover>();
        foreach (var p in products)
        {
            var before = p.Purchases.Where(x => x.Date >= start && x.Date < mid && x.Price > 0).ToList();
            var after = p.Purchases.Where(x => x.Date >= mid && x.Date < end && x.Price > 0).ToList();
            if (before.Count < IndexMinPurchases || after.Count < IndexMinPurchases) continue;
            var b = before.Average(x => (decimal)x.Price);
            var a = after.Average(x => (decimal)x.Price);
            result.Add(new PriceMover(p.Id, p.Shop, p.Name, (long)Math.Round(b), (long)Math.Round(a), a / b - 1m, before.Sum(x => x.TotalCents)));
        }
        return result;
    }

    private static IEnumerable<PriceIndexRow> Rows(string label, List<PriceMover> movers)
    {
        if (movers.Count == 0) yield break;
        yield return Row(label, null, movers);
        foreach (var g in movers.GroupBy(m => m.Shop, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
            yield return Row(label, g.Key, g.ToList());
    }

    private static PriceIndexRow Row(string label, string? shop, List<PriceMover> movers)
    {
        var spend = movers.Sum(m => m.Spend);
        var change = spend == 0 ? movers.Average(m => m.Change) : movers.Sum(m => m.Change * m.Spend) / spend;
        return new PriceIndexRow(label, shop, change, movers.Count, spend);
    }

    /// <summary>
    /// Regular purchases: products bought on at least three days in the twelve months up to <paramref name="today"/>,
    /// typically at most a month apart, and bought within the last two months; with their spend per month since their
    /// first purchase in that window.
    /// </summary>
    public static List<RegularProduct> Regulars(IReadOnlyList<ProductHistory> products, DateOnly today)
    {
        var since = today.AddMonths(-12);
        var result = new List<RegularProduct>();
        foreach (var p in products)
        {
            if (p.Last.Date < today.AddMonths(-2) || p.TypicalIntervalDays(since) is not { } interval || interval > 31) continue;
            var recent = p.Purchases.Where(x => x.Date >= since).ToList();
            var months = Math.Max(1.0, (today.DayNumber - recent[0].Date.DayNumber + 1) / 30.44);
            result.Add(new RegularProduct(p.Id, p.Shop, p.Name, interval, (long)Math.Round(recent.Sum(x => x.TotalCents) / months),
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
