using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using OpenBudget.Models;

namespace OpenBudget.Connectors.Receipts;

/// <summary>A receipt line as the categorizer sees it; <see cref="Id"/> identifies it in the result.</summary>
public sealed record ReceiptLine(long Id, string Name, string? VatCode, long TotalCents, bool NoDiscount, ReceiptItemKind Kind);

/// <summary>Where one line goes: a category (null: the booking's own) and how it was chosen (<see cref="ReceiptCategorySource"/>).</summary>
public sealed record LineCategory(long? CategoryId, string Source);

/// <summary>One part of a receipt split: signed cents, its category (null: none) and its lines.</summary>
public sealed record ReceiptSplitPart(long AmountCents, long? CategoryId, IReadOnlyList<long> LineIds);

/// <summary>
/// A category a source's items may go to: its path ("Haushalt › Drogerie"), the owner's description of what belongs
/// there and extra keywords (comma-separated).
/// </summary>
public sealed record AllowedCategory(long CategoryId, string Path, string? Description = null, string? Keywords = null);

/// <summary>
/// A receipt source's item categories: the default (null: the booking's own category) and the allowed categories.
/// </summary>
public sealed record ReceiptSourceRules(long? DefaultCategoryId, IReadOnlyList<AllowedCategory> Allowed)
{
    public static readonly ReceiptSourceRules None = new(null, []);
}

/// <summary>
/// Sorts receipt lines into categories and turns them into split parts. Each receipt source has a default category
/// (none: the booking's own) and a list of allowed categories (see <see cref="ReceiptSourceRules"/>); a line only ever
/// goes to one of these.
///
/// A line goes, in this order: deposit lines and fees to the default; a line the owner moved to where they moved it; a
/// product the owner categorized before (the item category memory, by <see cref="Normalize">normalised name</see>) to
/// that category, when it is allowed for this source; a product whose name contains an allowed category's extra keyword
/// to that category; a product of a built-in kind (<see cref="Kind"/>: drugstore, household, drinks, alcohol, pet
/// supplies, other non-food) to the first allowed category that kind belongs to (<see cref="KindsOf"/>: the kind's
/// words in the category's path or description, e.g. "Drogerie" or "Reinigungsmittel"); a product the AI placed (only
/// when the owner opted in; it only chooses among the allowed categories) to that category; everything else to the
/// default. The VAT code alone decides nothing.
///
/// The redeemed bonus credit is spread over the lines that allow discounts in proportion to their totals; the parts
/// (one per category) add up exactly to the booking, a rounding remainder going to the largest part.
/// </summary>
public static partial class ReceiptCategorizer
{
    /// <summary>
    /// The built-in kinds: the words in a category's path or description that make it the place for the kind, and the
    /// fragments of product names of that kind (lower case, matched anywhere in the name; a fragment starting with "^"
    /// only at the start of a word, one ending in "$" only at the end of a word). Till receipts abbreviate ("ZAHNP.",
    /// "SPUELM.", "TOIL.PAP", "KUECHENR", "WASCHM", "DUSCHG", "TRCK.-SH"), so the abbreviations are listed too. Product
    /// names are checked against the kinds in this order.
    /// </summary>
    public static readonly IReadOnlyList<(string Kind, IReadOnlyList<string> CategoryWords, IReadOnlyList<string> Keywords)> Kinds =
    [
        ("drugstore", ["drogerie", "körperpflege", "pflege", "hygiene", "kosmetik"],
        [
            "deo", "seife", "zahnpasta", "zahncreme", "zahnbürste", "zahnseide", "shampoo", "duschgel", "duschbad", "^spülung",
            "taschentücher", "kosmetiktücher", "feuchttücher", "windel", "wattepads", "wattestäbchen", "rasier", "^binden",
            "tampon", "slipeinlage", "handcreme", "bodylotion", "sonnencreme", "lippenpflege", "pflaster", "toilettenpapier",
            "klopapier", "mundspül", "haarspray", "haargel", "make-up", "nagellack", "abschmink",
            "zahnp", "toil.pap", "toilettenp", "duschg", "shamp", "trck.-sh", "taschent", "rasierkl", "zahnb",
        ]),
        ("household", ["haushalt", "reinigung", "putz", "verbrauch"],
        [
            "spülmittel", "spülm", "spuelm", "waschmittel", "weichspüler", "weichsp", "küchenrolle", "küchentücher", "küchenr",
            "kuechenr", "müllbeutel", "müllb", "muellb", "abfallbeutel", "reiniger", "putzmittel", "schwamm", "spüllappen",
            "entkalker", "geschirrsp", "spülmasch", "maschinentabs", "spültabs", "^tabs", "glanzspüler", "allzweckr", "wc-rein",
            "waschm", "spuel", "fleckenentf", "desinfektion", "scheuermilch", "regeneriersalz", "wc-stein",
        ]),
        ("pets", ["tier", "katze", "hund"],
        [
            "katzenstreu", "katzenfutter", "hundefutter", "tierfutter", "vogelfutter", "kauknochen", "hundesnack",
            "katzensnack", "nassfutter", "trockenfutter", "kleintierstreu",
        ]),
        ("nonfood", ["non-food", "nonfood", "haushaltswaren", "sonstiges"],
        [
            "batterie", "kerze", "teelicht", "alufolie", "frischhaltefolie", "^folie", "gefrierbeutel", "backpapier", "glühbirne",
            "leuchtmittel", "grillanzünder", "streichhölzer", "feuerzeug", "blumenerde", "^socken", "^tragetasche",
            "^zeitschrift", "^zeitung", "geschenkpapier", "^grillkohle",
        ]),
        ("alcohol", ["alkohol", "spirituosen", "wein", "bier"],
        [
            "^bier", "pils$", "pilsener", "pilsner", "weizenbier", "^radler", "^wein$", "rotwein", "weißwein", "weisswein",
            "roséwein", "rosewein", "^sekt", "prosecco", "champagner", "^vodka", "^wodka", "whisky", "whiskey",
            "^rum$", "^gin$", "likör", "^korn$", "^aperol", "^cider", "^lambrusco", "riesling", "merlot", "chardonnay",
            "spätburgunder", "grauburgunder", "^glühwein", "^weinbrand",
        ]),
        ("drinks", ["getränk"],
        [
            "mineralwasser", "mineralw", "tafelwasser", "^wasser", "^sprudel", "schorle", "^cola", "limonade", "^limo",
            "eistee", "energy drink", "^saft", "fruchtsaft", "orangensaft", "apfelsaft", "^nektar", "^smoothie",
        ]),
    ];

    private static readonly IReadOnlyList<(string Kind, Regex Pattern)> KindPatterns = Kinds
        .Select(k => (k.Kind, new Regex(string.Join('|', k.Keywords.Select(KeywordPattern)), RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))))
        .ToList();

    private static string KeywordPattern(string k)
    {
        var start = k.StartsWith('^', StringComparison.Ordinal);
        var end = k.EndsWith('$', StringComparison.Ordinal);
        var core = Regex.Escape(k.TrimStart('^').TrimEnd('$'));
        return (start ? @"(?<![\p{L}\p{N}])" : "") + core + (end ? @"(?![\p{L}\p{N}])" : "");
    }

    private static string Lower(string s) => s.ToLower(CultureInfo.GetCultureInfo("de-DE"));

    [GeneratedRegex(@"\b(ca\.?\s*)?\d+([.,]\d+)?\s*(x\s*\d+([.,]\d+)?\s*)?(g|kg|mg|ml|cl|l|st|stk|stück|er|m|cm|w)\b", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, 1000)]
    private static partial Regex Sizes();

    [GeneratedRegex(@"[^\p{L}\p{N}]+", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex NonWord();

    /// <summary>
    /// The name the memory keys on: lower case, "*" and sizes ("150g", "0,5l", "ca. 500g", "6x1,5l") removed, punctuation
    /// as single spaces, so the same product in another size is the same entry.
    /// </summary>
    public static string Normalize(string name)
    {
        var s = name.ToLower(CultureInfo.GetCultureInfo("de-DE")).Replace("*", " ", StringComparison.Ordinal);
        s = Sizes().Replace(s, " ");
        s = NonWord().Replace(s, " ").Trim();
        return s.Length <= 300 ? s : s[..300];
    }

    /// <summary>The built-in kind of a product name (see <see cref="Kinds"/>), or null.</summary>
    public static string? Kind(string name)
    {
        var s = Lower(name);
        foreach (var (kind, pattern) in KindPatterns)
            if (pattern.IsMatch(s)) return kind;
        return null;
    }

    /// <summary>The built-in kinds an allowed category is the place for: their words in its path or description.</summary>
    public static IReadOnlyList<string> KindsOf(AllowedCategory category)
    {
        var text = Lower(category.Path + " " + category.Description);
        return Kinds.Where(k => k.CategoryWords.Any(w => text.Contains(w, StringComparison.Ordinal))).Select(k => k.Kind).ToList();
    }

    /// <summary>The allowed category a product's name places it in by keywords (extra keywords first, then the built-in kinds), or null.</summary>
    public static long? ByKeywords(string name, ReceiptSourceRules rules)
    {
        var s = Lower(name);
        foreach (var c in rules.Allowed)
            foreach (var k in (c.Keywords ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (k.Length >= 2 && s.Contains(Lower(k), StringComparison.Ordinal)) return c.CategoryId;
        if (Kind(name) is not { } kind) return null;
        return rules.Allowed.FirstOrDefault(c => KindsOf(c).Contains(kind, StringComparer.Ordinal))?.CategoryId;
    }

    /// <summary>
    /// Each line's category (see the class remarks). <paramref name="memory"/>: normalised name → category;
    /// <paramref name="ai"/>: line id → category, for lines the AI placed; <paramref name="manual"/>: lines the owner
    /// moved (they keep their category; null: the booking's). Memory and AI answers outside the source's categories
    /// are ignored.
    /// </summary>
    public static Dictionary<long, LineCategory> Assign(
        IReadOnlyList<ReceiptLine> lines, long? bookingCategoryId, ReceiptSourceRules rules,
        IReadOnlyDictionary<string, long> memory, IReadOnlyDictionary<long, long>? ai = null, IReadOnlyDictionary<long, long?>? manual = null)
    {
        var home = rules.DefaultCategoryId ?? bookingCategoryId;
        var result = new Dictionary<long, LineCategory>();
        foreach (var line in lines)
        {
            if (manual != null && manual.TryGetValue(line.Id, out var own))
                result[line.Id] = new LineCategory(own ?? bookingCategoryId, ReceiptCategorySource.Manual);
            else if (line.Kind != ReceiptItemKind.Product)
                result[line.Id] = new LineCategory(home, ReceiptCategorySource.Default);
            else if (memory.TryGetValue(Normalize(line.Name), out var remembered) && IsAllowed(remembered, rules, bookingCategoryId))
                result[line.Id] = new LineCategory(remembered, ReceiptCategorySource.Memory);
            else if (ByKeywords(line.Name, rules) is { } keyword)
                result[line.Id] = new LineCategory(keyword, ReceiptCategorySource.Keyword);
            else if (ai != null && ai.TryGetValue(line.Id, out var guessed) && IsAllowed(guessed, rules, bookingCategoryId))
                result[line.Id] = new LineCategory(guessed, ReceiptCategorySource.Ai);
            else
                result[line.Id] = new LineCategory(home, ReceiptCategorySource.Default);
        }
        return result;
    }

    /// <summary>Whether a line of this source may go to <paramref name="categoryId"/>: an allowed category, the default or the booking's own.</summary>
    public static bool IsAllowed(long categoryId, ReceiptSourceRules rules, long? bookingCategoryId) =>
        categoryId == (rules.DefaultCategoryId ?? bookingCategoryId) || rules.Allowed.Any(c => c.CategoryId == categoryId);

    /// <summary>Lines that would go to the AI: products without an allowed memory entry or a keyword placement (none when nothing is allowed).</summary>
    public static List<ReceiptLine> Undecided(IReadOnlyList<ReceiptLine> lines, ReceiptSourceRules rules, IReadOnlyDictionary<string, long> memory, long? bookingCategoryId = null) =>
        rules.Allowed.Count == 0 ? [] : lines.Where(l => l.Kind == ReceiptItemKind.Product
            && !(memory.TryGetValue(Normalize(l.Name), out var m) && IsAllowed(m, rules, bookingCategoryId))
            && ByKeywords(l.Name, rules) == null).ToList();

    /// <summary>
    /// What each line costs after the bonus: <paramref name="bonusCents"/> (zero or negative) spread over the lines that
    /// allow discounts in proportion to their totals (to the cent, rounding remainder on the largest). Positive cents.
    /// </summary>
    public static Dictionary<long, long> NetCents(IReadOnlyList<ReceiptLine> lines, long bonusCents)
    {
        var net = lines.ToDictionary(l => l.Id, l => l.TotalCents);
        var eligible = lines.Where(l => !l.NoDiscount && l.TotalCents > 0).ToList();
        if (bonusCents == 0 || eligible.Count == 0) return net;
        var shares = Amazon.AmazonSplit.Proportion(bonusCents, eligible.Select(l => (long?)l.TotalCents).ToList());
        for (var i = 0; i < eligible.Count; i++) net[eligible[i].Id] += shares[i];
        return net;
    }

    /// <summary>
    /// The split parts for a booking of <paramref name="bookingCents"/> (signed): one per category, in the order the
    /// categories first appear, each the sum of its lines after the bonus. Deposit lines and fees never make a part of
    /// their own: they join the part of the booking's category (or, when the products left no such part, the largest
    /// part). A part that comes to zero or to the opposite sign of the booking joins that part too, so every part has
    /// the booking's sign. The parts add up to the booking exactly: a difference (the booking differs from the receipt)
    /// goes to the largest part.
    /// </summary>
    public static List<ReceiptSplitPart> Parts(
        IReadOnlyList<ReceiptLine> lines, IReadOnlyDictionary<long, LineCategory> categories, long bonusCents, long bookingCents, long? bookingCategoryId)
    {
        var net = NetCents(lines, bonusCents);
        var sign = bookingCents < 0 ? -1 : 1;
        var products = lines.Where(l => l.Kind == ReceiptItemKind.Product).ToList();
        var groups = products.GroupBy(l => categories[l.Id].CategoryId)
            .Select(g => (Category: g.Key, Cents: g.Sum(l => net[l.Id]) * sign, Lines: g.Select(l => l.Id).ToList()))
            .ToList();
        int Home() => groups.FindIndex(g => g.Category == bookingCategoryId) is var i and >= 0 ? i
            : groups.Count == 0 ? -1 : groups.IndexOf(groups.MaxBy(g => g.Cents * sign)!);
        void Join(IReadOnlyList<long> ids, long cents)
        {
            var home = Home();
            if (home < 0) groups.Add((bookingCategoryId, cents, ids.ToList()));
            else groups[home] = (groups[home].Category, groups[home].Cents + cents, [.. groups[home].Lines, .. ids]);
        }
        var extra = lines.Where(l => l.Kind != ReceiptItemKind.Product).ToList();
        if (extra.Count > 0) Join(extra.Select(l => l.Id).ToList(), extra.Sum(l => net[l.Id]) * sign);
        while (groups.Count > 1 && groups.FindIndex(g => g.Category != bookingCategoryId && g.Cents * sign <= 0) is var bad and >= 0)
        {
            var g = groups[bad];
            groups.RemoveAt(bad);
            Join(g.Lines, g.Cents);
        }
        // the booking category's own part can end up with the wrong sign (deposit returns larger than its products)
        while (groups.Count > 1 && groups.FindIndex(g => g.Category == bookingCategoryId && g.Cents * sign <= 0) is var own and >= 0)
        {
            var g = groups[own];
            groups.RemoveAt(own);
            var largest = groups.IndexOf(groups.MaxBy(x => x.Cents * sign)!);
            groups[largest] = (bookingCategoryId, groups[largest].Cents + g.Cents, [.. groups[largest].Lines, .. g.Lines]);
        }
        var diff = bookingCents - groups.Sum(g => g.Cents);
        if (diff != 0 && groups.Count > 0)
        {
            var largest = groups.IndexOf(groups.MaxBy(g => Math.Abs(g.Cents)));
            groups[largest] = (groups[largest].Category, groups[largest].Cents + diff, groups[largest].Lines);
        }
        var order = lines.Select((l, i) => (l.Id, i)).ToDictionary(x => x.Id, x => x.i);
        return groups.OrderBy(g => g.Lines.Min(id => order[id]))
            .Select(g => new ReceiptSplitPart(g.Cents, g.Category, g.Lines.OrderBy(id => order[id]).ToList())).ToList();
    }

    /// <summary>The parts as a compact text ("-1234:56;-789:"), to recognise them again.</summary>
    public static string Signature(IEnumerable<(long AmountCents, long? CategoryId)> parts)
    {
        var sb = new StringBuilder();
        foreach (var (amount, category) in parts.OrderBy(p => p.AmountCents).ThenBy(p => p.CategoryId))
            sb.Append(CultureInfo.InvariantCulture, $"{amount}:{category};");
        return sb.ToString();
    }

    /// <summary>The lines as one text: "Gouda gerieben; Hinterschinken ×2" (quantity shown for counts above one).</summary>
    public static string Describe(IEnumerable<(string Name, string Quantity)> lines)
    {
        var text = string.Join("; ", lines.Select(l =>
            int.TryParse(l.Quantity, NumberStyles.Integer, CultureInfo.InvariantCulture, out var q) && q > 1 ? $"{l.Name} ×{q}" : l.Name));
        return text.Length <= 2000 ? text : text[..2000];
    }
}
