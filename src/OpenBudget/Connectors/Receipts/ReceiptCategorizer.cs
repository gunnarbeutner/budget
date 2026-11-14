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
/// Sorts supermarket receipt lines into categories and turns them into split parts.
///
/// A line goes, in this order: deposit lines and fees to the booking's category; a product the owner categorized
/// before (the item category memory, by <see cref="Normalize">normalised name</see>) to that category; a product whose
/// name contains a non-food keyword (soap, detergent, deodorant, …) to the owner's non-food category, when one is set;
/// a product the AI placed (only when the owner opted in) to that category; everything else to the booking's category.
/// The VAT code alone decides nothing (drinks and some vegetables carry 19 % too).
///
/// The redeemed bonus credit is spread over the lines that allow discounts in proportion to their totals; the parts
/// (one per category) add up exactly to the booking, a rounding remainder going to the largest part.
/// </summary>
public static partial class ReceiptCategorizer
{
    /// <summary>Name fragments of household goods, drugstore and other non-food products (lower case, matched anywhere in the name).</summary>
    public static readonly IReadOnlyList<string> NonFoodKeywords =
    [
        "deo", "seife", "toilettenpapier", "klopapier", "spül", "waschmittel", "weichspüler", "küchenrolle", "küchentücher",
        "zahnpasta", "zahncreme", "zahnbürste", "zahnseide", "shampoo", "duschgel", "duschbad", "spülung", "müllbeutel",
        "abfallbeutel", "reiniger", "putzmittel", "taschentücher", "kosmetiktücher", "feuchttücher", "windel", "batterie",
        "kerze", "teelicht", "folie", "schwamm", "spüllappen", "wattepads", "wattestäbchen", "rasier", "binden", "tampon",
        "slipeinlage", "handcreme", "bodylotion", "sonnencreme", "lippenpflege", "pflaster", "glühbirne", "leuchtmittel",
        "gefrierbeutel", "backpapier", "alufolie", "frischhaltefolie", "entkalker", "geschirr", "tabs", "katzenstreu",
        "grillanzünder", "streichhölzer", "feuerzeug",
    ];

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

    /// <summary>Whether the name contains a non-food keyword.</summary>
    public static bool IsNonFood(string name)
    {
        var s = name.ToLower(CultureInfo.GetCultureInfo("de-DE"));
        return NonFoodKeywords.Any(k => s.Contains(k, StringComparison.Ordinal));
    }

    /// <summary>
    /// Each line's category (see the class remarks). <paramref name="memory"/>: normalised name → category;
    /// <paramref name="ai"/>: line id → category, for lines the AI placed; <paramref name="manual"/>: lines the owner
    /// moved (they keep their category).
    /// </summary>
    public static Dictionary<long, LineCategory> Assign(
        IReadOnlyList<ReceiptLine> lines, long? bookingCategoryId, long? nonFoodCategoryId,
        IReadOnlyDictionary<string, long> memory, IReadOnlyDictionary<long, long>? ai = null, IReadOnlyDictionary<long, long?>? manual = null)
    {
        var result = new Dictionary<long, LineCategory>();
        foreach (var line in lines)
        {
            if (manual != null && manual.TryGetValue(line.Id, out var own))
                result[line.Id] = new LineCategory(own ?? bookingCategoryId, ReceiptCategorySource.Manual);
            else if (line.Kind != ReceiptItemKind.Product)
                result[line.Id] = new LineCategory(bookingCategoryId, ReceiptCategorySource.Default);
            else if (memory.TryGetValue(Normalize(line.Name), out var remembered))
                result[line.Id] = new LineCategory(remembered, ReceiptCategorySource.Memory);
            else if (nonFoodCategoryId != null && IsNonFood(line.Name))
                result[line.Id] = new LineCategory(nonFoodCategoryId, ReceiptCategorySource.Keyword);
            else if (ai != null && ai.TryGetValue(line.Id, out var guessed))
                result[line.Id] = new LineCategory(guessed, ReceiptCategorySource.Ai);
            else
                result[line.Id] = new LineCategory(bookingCategoryId, ReceiptCategorySource.Default);
        }
        return result;
    }

    /// <summary>Lines that would go to the AI: products without a memory or keyword hit.</summary>
    public static List<ReceiptLine> Undecided(IReadOnlyList<ReceiptLine> lines, long? nonFoodCategoryId, IReadOnlyDictionary<string, long> memory) =>
        lines.Where(l => l.Kind == ReceiptItemKind.Product && !memory.ContainsKey(Normalize(l.Name))
            && !(nonFoodCategoryId != null && IsNonFood(l.Name))).ToList();

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
    /// categories first appear, each the sum of its lines after the bonus; parts that come to zero are left out (their
    /// lines join the booking category's part). The parts add up to the booking exactly: a difference (the booking
    /// differs from the receipt) goes to the largest part.
    /// </summary>
    public static List<ReceiptSplitPart> Parts(
        IReadOnlyList<ReceiptLine> lines, IReadOnlyDictionary<long, LineCategory> categories, long bonusCents, long bookingCents, long? bookingCategoryId)
    {
        var net = NetCents(lines, bonusCents);
        var sign = bookingCents < 0 ? -1 : 1;
        var groups = lines.GroupBy(l => categories[l.Id].CategoryId)
            .Select(g => (Category: g.Key, Cents: g.Sum(l => net[l.Id]) * sign, Lines: g.Select(l => l.Id).ToList()))
            .ToList();
        var zero = groups.Where(g => g.Cents == 0 && g.Category != bookingCategoryId).ToList();
        if (zero.Count > 0)
        {
            groups = groups.Except(zero).ToList();
            var home = groups.FindIndex(g => g.Category == bookingCategoryId);
            var moved = zero.SelectMany(z => z.Lines).ToList();
            if (home >= 0) groups[home] = (groups[home].Category, groups[home].Cents, [.. groups[home].Lines, .. moved]);
            else groups.Add((bookingCategoryId, 0, moved));
        }
        var diff = bookingCents - groups.Sum(g => g.Cents);
        if (diff != 0 && groups.Count > 0)
        {
            var largest = groups.IndexOf(groups.MaxBy(g => Math.Abs(g.Cents)));
            groups[largest] = (groups[largest].Category, groups[largest].Cents + diff, groups[largest].Lines);
        }
        return groups.Select(g => new ReceiptSplitPart(g.Cents, g.Category, g.Lines)).ToList();
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
