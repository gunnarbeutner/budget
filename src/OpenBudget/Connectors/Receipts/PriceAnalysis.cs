using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.Connectors.Receipts;

/// <summary>
/// One product line of a receipt the user sees, as the price analysis reads it, with what its name stands for
/// (<see cref="Identity"/>; null: the name rules decide).
/// </summary>
public sealed record PriceLine(long ReceiptId, string Shop, DateOnly Date, string Name, string Quantity, long? UnitCents, long TotalCents, bool IsOwn,
    ProductIdentity? Identity = null);

/// <summary>What a price is given per: a piece as sold (a pack), a kg, a litre, or one item of a pack of several.</summary>
public enum PriceUnit { Piece, Kg, Litre, Each }

/// <summary>A size printed in a product name: the pack's content in grams or millilitres.</summary>
public sealed record PackSize(decimal Amount, PriceUnit Unit)
{
    /// <summary>The content in kilograms or litres.</summary>
    public decimal Base => Amount / 1000m;
}

/// <summary>
/// What a receipt line's name stands for (see <see cref="ReceiptProductIdentity"/>): the canonical product name, the
/// pack's count and size, and whether it is a product.
/// </summary>
public sealed record ProductIdentity(string Name, string? Brand, int? PackCount, decimal? UnitSize, ProductSizeUnit? Unit, ProductIdentityKind Kind,
    ProductIdentitySource Source)
{
    /// <summary>The pack's size; null without a size and unit (a count alone counts as pieces), or when implausible.</summary>
    public ItemSize? Size
    {
        get
        {
            var count = PackCount is > 0 and var c ? c : 1;
            var size = (UnitSize, Unit) switch
            {
                ( > 0m and var v, { } u) => new ItemSize(count, v, u),
                (null, ProductSizeUnit.Piece or null) when PackCount is > 1 => new ItemSize(1, count, ProductSizeUnit.Piece),
                _ => null,
            };
            return size is { Plausible: true } ? size : null;
        }
    }

    /// <summary>From a stored identity.</summary>
    public static ProductIdentity From(ReceiptProductIdentity i) => new(i.ProductName, i.Brand, i.PackCount, i.UnitSize, i.Unit, i.Kind, i.Source);
}

/// <summary>A pack's size: <see cref="Count"/> units of <see cref="Each"/> <see cref="Unit"/> (12 × 0.5 l).</summary>
public sealed record ItemSize(int Count, decimal Each, ProductSizeUnit Unit)
{
    /// <summary>What the pack's content is measured in: kg, litres, or single items.</summary>
    public PriceUnit BaseUnit => Unit switch
    {
        ProductSizeUnit.Gram or ProductSizeUnit.Kilogram => PriceUnit.Kg,
        ProductSizeUnit.Millilitre or ProductSizeUnit.Litre => PriceUnit.Litre,
        _ => PriceUnit.Each,
    };

    /// <summary>The pack's content in <see cref="BaseUnit"/>.</summary>
    public decimal Base => Count * (Unit is ProductSizeUnit.Gram or ProductSizeUnit.Millilitre ? Each / 1000m : Each);

    /// <summary>From 5 g/ml to 25 kg/l, or 1 to 1000 items.</summary>
    public bool Plausible => Count > 0 && (BaseUnit == PriceUnit.Each ? Base is >= 1m and <= 1000m : Base is >= 0.005m and <= 25m);
}

/// <summary>
/// One purchase of a product: the paid price per piece, per kg, litre or item (weighed items, or pack items of a known
/// size), and the comparable <see cref="Price"/> the product's history uses.
/// </summary>
public sealed record Purchase(long ReceiptId, DateOnly Date, string Name, decimal Quantity, long TotalCents, long? PieceCents, long? BaseCents, PriceUnit BaseUnit, bool Weighed, bool IsOwn,
    ItemSize? Size = null, bool PerBase = false)
{
    /// <summary>Per kg for weighed items, per kg, litre or item when the product compares by size, else per piece.</summary>
    public long Price => Weighed || PerBase ? BaseCents ?? 0 : PieceCents ?? 0;
}

/// <summary>
/// A product (per shop) and its purchases, oldest first. <see cref="Size"/> is the pack size shown with the name when
/// the product is one of several of the same name that cannot be compared by size.
/// </summary>
public sealed record ProductHistory(string Key, string Shop, string Name, IReadOnlyList<Purchase> Purchases, IReadOnlyList<string> MergedKeys)
{
    public ItemSize? Size { get; init; }

    public bool Weighed => Purchases.All(p => p.Weighed);
    public Purchase First => Purchases[0];
    public Purchase Last => Purchases[^1];
    public int TimesBought => Purchases.Select(p => (p.ReceiptId, p.Date)).Distinct().Count();

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

    /// <summary>Whether the product compares by size (per kg, litre or item) rather than per piece.</summary>
    public bool PerBase => Purchases[^1].Weighed || Purchases[^1].PerBase;

    /// <summary>What <see cref="Purchase.BaseCents"/> is given per.</summary>
    public PriceUnit BaseUnit => PerBase ? Purchases[^1].BaseUnit : PriceUnit.Kg;

    /// <summary>What <see cref="Purchase.Price"/> is given per.</summary>
    public PriceUnit Unit => PerBase ? BaseUnit : PriceUnit.Piece;

    /// <summary>Whether a price per piece can be shown as well: every purchase is the same pack, none weighed.</summary>
    public bool HasPackPrice => PerBase && Purchases.All(p => p is { Weighed: false, PieceCents: not null }) && Purchases.Select(p => p.Size).Distinct().Count() == 1;
}

/// <summary>A product bought at least monthly, with what it costs per month.</summary>
public sealed record RegularProduct(string ProductId, string Shop, string Name, double IntervalDays, long MonthlyCents, int Times);

/// <summary>A receipt's payment, for the grocery spend.</summary>
public sealed record ReceiptSpend(string Shop, DateOnly Date, long Cents);

/// <summary>The span the prices page looks at, up to today.</summary>
public enum PricePeriod { SixMonths, TwelveMonths, ThreeYears, All }

/// <summary>
/// One purchase in a product's price chart: its price (per piece or per kg, litre or item), whether it was an offer, and
/// whether it is unusual (see <see cref="PriceAnalysis.UnusualFactor"/>).
/// </summary>
public sealed record PricePoint(Purchase Purchase, long Price, bool Offer, bool Unusual = false)
{
    public DateOnly Date => Purchase.Date;
}

/// <summary>A rise of the usual price between two purchases.</summary>
public sealed record PriceRise(DateOnly From, DateOnly To, long Before, long After);

/// <summary>
/// A product within a period: all its purchases (<see cref="All"/>) and those in the period (<see cref="Points"/>), the
/// usual price at the period's start (<see cref="Base"/>: the last usual price before it, or the first in it; the chart
/// starts there), the last usual price before the period (<see cref="Before"/> on <see cref="BeforeDate"/>, null
/// without one; the base of <see cref="Change"/>) and now, the change
/// between the last usual prices before and in the period (null without both), the price rises, the cheapest usual
/// purchase in the period, the average usual price and the spend per month.
/// </summary>
public sealed record ProductDetail(IReadOnlyList<PricePoint> All, IReadOnlyList<PricePoint> Points, IReadOnlyList<PriceRise> Rises, long Base, long? Before,
    long Now, decimal? Change, PricePoint Cheapest, long Average, long MonthlyCents, DateOnly? BeforeDate = null);

/// <summary>
/// A product's row on the prices page for a period: <see cref="Times"/> counts the purchases in the period,
/// <see cref="TotalTimes"/> all of them (as the list filter <see cref="PriceAnalysis.Listed"/> does).
/// </summary>
public sealed record ProductRow(ProductHistory Product, ProductDetail Detail, double? IntervalDays, int Times, long Spend)
{
    public int TotalTimes => Product.TimesBought;
}

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
/// Price history from itemised receipts: product identity (a line's <see cref="ProductIdentity"/>, else the name rules
/// <see cref="ProductKey"/> and <see cref="ParseSize"/>), unit prices, unusual purchases, offers, the basket index,
/// regular purchases and the grocery spend of a period. Pure functions over <see cref="PriceLine"/>s;
/// <see cref="PriceHistoryService"/> loads and caches them per project.
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

    /// <summary>The products of a shop a user's identities, merges and edits refer to: the line's name folded, whitespace collapsed, at most 300 characters.</summary>
    public static string NameKey(string name)
    {
        var key = TextFold.Fold(name, collapseWhitespace: true).Trim();
        return key.Length > 300 ? key[..300] : key;
    }

    /// <summary>
    /// A product's key within its shop by the name rules: the name folded (case, umlauts, diacritics), trailing pack
    /// sizes and counts dropped, punctuation turned into single spaces; for shops with long titles cut to their first
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

    /// <summary>The key of a product with an identity: its canonical name folded, punctuation turned into single spaces.</summary>
    public static string IdentityKey(ProductIdentity identity) => NonAlnum().Replace(SearchText.Fold(identity.Name), " ").Trim();

    /// <summary>The product key of a line: by its identity when it has one, else by the name rules.</summary>
    public static string KeyOf(PriceLine line) => line.Identity is { } id && IdentityKey(id) is { Length: > 0 } k ? k : ProductKey(line.Shop, line.Name);

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
    /// The size of a line's pack: its identity's; without one (or for an identity from the AI or the rules that has
    /// none) the size in its name. The user's identity decides alone.
    /// </summary>
    public static ItemSize? SizeOf(PriceLine line)
    {
        if (line.Identity is { } id && (id.Size != null || id.Source == ProductIdentitySource.User)) return id.Size;
        return ParseSize(line.Name) is { } s ? new ItemSize(1, s.Amount, s.Unit == PriceUnit.Litre ? ProductSizeUnit.Millilitre : ProductSizeUnit.Gram) : null;
    }

    /// <summary>
    /// The purchase a line stands for, or null for a line without a positive amount or quantity. A quantity in grams
    /// ("958g") or a fractional count ("0.521", kilograms) is weighed: its unit price is per kg. Otherwise the price per
    /// piece is the unit price, or the total over the quantity; with a known size (<see cref="SizeOf"/>) also per kg,
    /// litre or item.
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
        var size = SizeOf(line);
        long? perBase = size == null ? null : (long)Math.Round(piece / size.Base);
        return new Purchase(line.ReceiptId, line.Date, line.Name, count, line.TotalCents, piece, perBase, size?.BaseUnit ?? PriceUnit.Kg, false, line.IsOwn, size);
    }

    private static Purchase Weighed(PriceLine line, decimal kg)
    {
        var perKg = line.UnitCents is > 0 and var u ? u : (long)Math.Round(line.TotalCents / kg);
        return new Purchase(line.ReceiptId, line.Date, line.Name, kg, line.TotalCents, null, perKg, PriceUnit.Kg, true, line.IsOwn);
    }

    /// <summary>
    /// Groups lines into products per shop by <see cref="KeyOf"/>, leaving out lines that are not products.
    /// <paramref name="merges"/> maps a product id ("shop|key") to the product it was merged into (followed
    /// transitively). A product shows its latest name. Its purchases compare by size when all of them have one of the
    /// same kind (weight, volume or items), whatever the pack, and per piece when they are one pack size and lines
    /// without a size; otherwise each kind, and the purchases without a size, form a product of their own (id
    /// "shop|key#kind"). Within a kind, packs whose contents differ by more than <see cref="ComparablePackFactor"/> form
    /// products of their own as well (<see cref="PackGroups"/>, id "shop|key#kind-size"). A product split off is shown
    /// with its pack size (its latest when its purchases differ).
    /// </summary>
    public static List<ProductHistory> Products(IEnumerable<PriceLine> lines, IReadOnlyDictionary<string, string>? merges = null)
    {
        var groups = new Dictionary<string, (string Shop, string Key, List<(Purchase P, string Name)> Items, HashSet<string> Keys)>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            if (line.Identity?.Kind == ProductIdentityKind.NotAProduct || ToPurchase(line) is not { } p) continue;
            var key = KeyOf(line);
            if (key.Length == 0) continue;
            var own = line.Shop + "|" + key;
            var target = Resolve(own, merges);
            if (!groups.TryGetValue(target, out var g))
                groups[target] = g = (line.Shop, target[(line.Shop.Length + 1)..], [], new HashSet<string>(StringComparer.Ordinal));
            g.Items.Add((p, line.Identity?.Name ?? line.Name));
            g.Keys.Add(own);
        }
        return groups.Values.SelectMany(g =>
        {
            var keys = g.Keys.Order(StringComparer.Ordinal).ToList();
            var ordered = g.Items.OrderBy(x => x.P.Date).ThenBy(x => x.P.ReceiptId).ToList();
            var kinds = ordered.GroupBy(x => x.P.BaseCents != null ? x.P.BaseUnit : (PriceUnit?)null).ToList();
            // One pack size and lines without a size: the same pack, compared per piece.
            if (kinds.Count > 1 && ordered.All(x => !x.P.Weighed) && ordered.Select(x => x.P.Size).Where(s => s != null).Distinct().Count() == 1)
                kinds = ordered.GroupBy(_ => (PriceUnit?)null).ToList();
            var parts = kinds.SelectMany(k => (k.Key == null ? [(KindCode(null), k.ToList())] : PackGroups(k.ToList()))
                .Select(c => (Kind: k.Key, c.Code, c.Items))).ToList();
            return parts.Select(part =>
            {
                var purchases = part.Items.Select(x => x.P with { PerBase = part.Kind != null }).ToList();
                var sizes = purchases.Select(x => x.Size).Distinct().ToList();
                var key = parts.Count == 1 ? g.Key : g.Key + "#" + part.Code;
                var size = parts.Count == 1 ? null : sizes.Count == 1 ? sizes[0] : purchases[^1].Size;
                return new ProductHistory(key, g.Shop, part.Items[^1].Name, purchases, keys) { Size = size };
            });
        }).OrderByDescending(p => p.Last.Date).ThenBy(p => p.Name, StringComparer.CurrentCulture).ToList();
    }

    /// <summary>
    /// Purchases of one product compare by size only when their pack contents (pack count × unit size) differ by at
    /// most this factor; a 1 l bottle and a 12 × 0.5 l crate are different products, since their price per litre
    /// differs by the pack format as much as by the price.
    /// </summary>
    public const decimal ComparablePackFactor = 2m;

    /// <summary>
    /// Groups the purchases of one kind (by kg, litre or item) by pack content: sorted by content, a new group starts
    /// when a pack exceeds the group's smallest × <see cref="ComparablePackFactor"/>. Weighed purchases and those
    /// without a size stay with the only group, or form one of their own beside several. Codes: the kind, and with
    /// several groups the smallest content of each ("l-6").
    /// </summary>
    private static List<(string Code, List<(Purchase P, string Name)> Items)> PackGroups(List<(Purchase P, string Name)> items)
    {
        var kind = KindCode(items[0].P.BaseCents != null ? items[0].P.BaseUnit : null);
        var sized = items.Where(x => x.P.Size != null).OrderBy(x => x.P.Size!.Base).ToList();
        var groups = new List<(decimal Smallest, List<(Purchase P, string Name)> Items)>();
        foreach (var x in sized)
        {
            if (groups.Count == 0 || x.P.Size!.Base > groups[^1].Smallest * ComparablePackFactor) groups.Add((x.P.Size!.Base, []));
            groups[^1].Items.Add(x);
        }
        var loose = items.Where(x => x.P.Size == null).ToList();
        if (groups.Count <= 1) return [(kind, items)];
        var result = groups.Select(c => (kind + "-" + c.Smallest.ToString("0.###", CultureInfo.InvariantCulture),
            c.Items.OrderBy(x => x.P.Date).ThenBy(x => x.P.ReceiptId).ToList())).ToList();
        if (loose.Count > 0) result.Add((kind, loose));
        return result;
    }

    private static string KindCode(PriceUnit? unit) => unit switch
    {
        PriceUnit.Kg => "kg",
        PriceUnit.Litre => "l",
        PriceUnit.Each => "pcs",
        _ => "pack",
    };

    /// <summary>A product id without the kind <see cref="Products"/> appends when it splits a product by size.</summary>
    public static string WithoutKind(string id) => id.IndexOf('#', StringComparison.Ordinal) is var i and >= 0 ? id[..i] : id;

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

    /// <summary>
    /// A purchase whose price is more than this multiple of the product's median price (over its whole history), or
    /// less than its inverse, is unusual: a different pack sold under the same name, a misread line, a one-off. Unusual
    /// purchases are shown but left out of the usual price, its change, the basket index, the biggest jump and offers.
    /// </summary>
    public const decimal UnusualFactor = 2m;

    /// <summary>Unusual purchases need a median of at least this many purchases to stand out from.</summary>
    private const int UnusualMinPurchases = 3;

    /// <summary>Which of the prices are unusual (see <see cref="UnusualFactor"/>); none with fewer than three.</summary>
    public static bool[] Unusual(IReadOnlyList<long> prices)
    {
        var result = new bool[prices.Count];
        if (prices.Count < UnusualMinPurchases) return result;
        var median = (decimal)Median(prices.ToList());
        for (var i = 0; i < prices.Count; i++)
            result[i] = prices[i] > median * UnusualFactor || prices[i] * UnusualFactor < median;
        return result;
    }

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
    /// The priced purchases (oldest first) with their offer and unusual marks: unusual ones by
    /// <see cref="Unusual"/> over all of them, offers by <see cref="Offers"/> over the others.
    /// </summary>
    public static List<PricePoint> Points(IEnumerable<(Purchase Purchase, long Price)> purchases)
    {
        var priced = purchases.Where(x => x.Price > 0).ToList();
        var unusual = Unusual(priced.Select(x => x.Price).ToList());
        var normal = priced.Where((_, i) => !unusual[i]).ToList();
        var offers = Offers(normal.Select(x => x.Price).ToList());
        var result = new List<PricePoint>(priced.Count);
        var n = 0;
        for (var i = 0; i < priced.Count; i++)
            result.Add(new PricePoint(priced[i].Purchase, priced[i].Price, !unusual[i] && offers[n++], unusual[i]));
        return result;
    }

    /// <summary>
    /// A product between <paramref name="start"/> and <paramref name="today"/>, priced by <see cref="Purchase.Price"/>
    /// or, with <paramref name="perPiece"/>, per piece (purchases without one are left out). Offers and unusual
    /// purchases are told from the whole history; the usual price, its rises and the change leave them out. The change
    /// needs a usual purchase before the period and one in it. Null when the product has no priced purchase in the
    /// period.
    /// </summary>
    public static ProductDetail? Detail(ProductHistory p, DateOnly start, DateOnly today, bool perPiece = false)
    {
        var all = Points(p.Purchases.Where(x => x.Date <= today).Select(x => (x, perPiece ? x.PieceCents ?? 0 : x.Price)));
        var points = all.Where(x => x.Date >= start).ToList();
        if (points.Count == 0) return null;
        var usual = all.Where(x => !x.Offer && !x.Unusual).ToList();
        var before = usual.LastOrDefault(x => x.Date < start);
        var latest = usual.LastOrDefault(x => x.Date >= start);
        var baseline = before ?? usual.FirstOrDefault(x => x.Date >= start) ?? points[0];
        var now = usual.LastOrDefault() ?? points[^1];
        var rises = new List<PriceRise>();
        for (var i = 1; i < usual.Count; i++)
        {
            var (a, b) = (usual[i - 1], usual[i]);
            if (b.Date >= start && b.Price >= a.Price * (1 + RiseThreshold))
                rises.Add(new PriceRise(a.Date < start ? start : a.Date, b.Date, a.Price, b.Price));
        }
        decimal? change = before != null && latest != null ? (decimal)latest.Price / before.Price - 1m : null;
        var normal = points.Where(x => !x.Unusual).ToList();
        if (normal.Count == 0) normal = points;
        var cheapest = normal.OrderBy(x => x.Price).ThenByDescending(x => x.Date).First();
        var average = (long)Math.Round(normal.Average(x => (decimal)x.Price));
        var monthly = (long)Math.Round(points.Sum(x => x.Purchase.TotalCents) / Months(points[0].Date, today));
        return new ProductDetail(all, points, rises, baseline.Price, before?.Price, now.Price, change, cheapest, average, monthly, before?.Date);
    }

    /// <summary>Products bought at least this often in each half of the period take part in the index.</summary>
    public const int IndexMinPurchases = 3;

    /// <summary>
    /// The basket index from <paramref name="start"/> to <paramref name="today"/>, overall and per shop: for every
    /// product bought at least <see cref="IndexMinPurchases"/> times in each half of the period, its usual price (offers
    /// and unusual purchases left out) at each month's end over its usual price at the start, averaged weighted by the
    /// product's spend in the period. One point at the start (100), one per month end and one today. Empty without such
    /// a product.
    /// </summary>
    public static List<IndexLine> IndexSeries(IReadOnlyList<ProductHistory> products, DateOnly start, DateOnly today)
    {
        var mid = DateOnly.FromDayNumber((start.DayNumber + today.DayNumber) / 2);
        var basket = new List<BasketItem>();
        foreach (var p in products)
        {
            var priced = p.Purchases.Where(x => x.Price > 0 && x.Date <= today).ToList();
            if (priced.Count(x => x.Date >= start && x.Date < mid) < IndexMinPurchases || priced.Count(x => x.Date >= mid) < IndexMinPurchases) continue;
            var usual = Points(priced.Select(x => (x, x.Price))).Where(x => !x.Offer && !x.Unusual).Select(x => x.Purchase).ToList();
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

    /// <summary>The product lists show products bought at least this often (on distinct receipts) unless one-time purchases are asked for.</summary>
    public const int MinTimesBought = 2;

    /// <summary>The rows the product lists show: those bought at least <see cref="MinTimesBought"/> times, or all with <paramref name="oneTime"/>.</summary>
    public static IEnumerable<ProductRow> Listed(IEnumerable<ProductRow> rows, bool oneTime) =>
        oneTime ? rows : rows.Where(r => r.Product.TimesBought >= MinTimesBought);

    /// <summary>
    /// The product whose usual price rose the most in the period (see <see cref="ProductDetail.Change"/>), bought at
    /// least twice in it; null without a rise.
    /// </summary>
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
        sb.Append(' ').Append(SearchText.Fold(p.Name));
        foreach (var n in p.Purchases.Select(x => x.Name).Distinct(StringComparer.Ordinal)) sb.Append(' ').Append(SearchText.Fold(n));
        return sb.ToString();
    }
}
