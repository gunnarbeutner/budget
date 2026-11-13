using System.Text;
using System.Text.RegularExpressions;
using OpenBudget.Services;

namespace OpenBudget.Assist;

/// <summary>A categorized booking used as evidence.</summary>
public sealed record HistoryBooking(long Id, long CategoryId, string? Iban, string PayeeKey, IReadOnlySet<string> Tokens, long Amount);

/// <summary>A booking to find a category for.</summary>
public sealed record TargetBooking(long Id, string? Iban, string PayeeKey, IReadOnlySet<string> Tokens, long Amount);

/// <summary>A similar categorized booking and how similar it is (0..1).</summary>
public sealed record Neighbour(HistoryBooking Booking, double Similarity);

/// <summary>A deterministic vote: the category, how sure (0..1) and why.</summary>
public sealed record Vote(long CategoryId, double Confidence, string Source, string Rationale);

/// <summary>
/// The deterministic part of category suggestions: payee normalisation, word tokens, the counterparty-IBAN vote and
/// a k-nearest-neighbour vote over categorized bookings (similarity of words, an identical payee counts most).
/// </summary>
public sealed partial class CategoryMatching
{
    /// <summary>Share of an IBAN's bookings one category needs before the IBAN alone decides.</summary>
    public const double IbanDominance = 0.9;

    private static readonly HashSet<string> Noise = new(StringComparer.Ordinal)
    {
        "GMBH", "MBH", "AG", "KG", "SE", "CO", "UG", "EV", "OHG", "GBR", "LTD", "INC", "SAGT", "DANKE", "UND", "DER", "DIE", "DAS",
        "FUER", "VOM", "BEI", "MIT", "IHR", "IHRE", "EUR", "SEPA", "LASTSCHRIFT", "UEBERWEISUNG", "GUTSCHRIFT", "KARTENZAHLUNG",
        "DATUM", "UHR", "ZAHLUNG", "AUFTRAG", "REF", "NR", "KTO", "BLZ", "VISA", "DEBIT", "GIROCARD", "ONLINE",
    };

    private readonly List<HistoryBooking> _history;
    private readonly Dictionary<string, List<HistoryBooking>> _byToken = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<HistoryBooking>> _byPayee = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<HistoryBooking>> _byIban = new(StringComparer.Ordinal);

    public CategoryMatching(IEnumerable<HistoryBooking> history)
    {
        _history = history.ToList();
        foreach (var h in _history)
        {
            foreach (var token in h.Tokens) Add(_byToken, token, h);
            if (h.PayeeKey.Length > 0) Add(_byPayee, h.PayeeKey, h);
            if (h.Iban is { Length: > 0 } iban) Add(_byIban, iban, h);
        }
    }

    private static void Add(Dictionary<string, List<HistoryBooking>> index, string key, HistoryBooking h)
    {
        if (!index.TryGetValue(key, out var list)) index[key] = list = [];
        list.Add(h);
    }

    /// <summary>Upper-case payee without digits, punctuation and legal-form words ("REWE MARKT").</summary>
    public static string PayeeKey(string? payee)
    {
        if (string.IsNullOrWhiteSpace(payee)) return "";
        var words = Words(payee).Where(w => !Noise.Contains(w)).ToList();
        return string.Join(' ', words);
    }

    /// <summary>The distinct words (three letters or more, no digits, no filler words) of payee and purpose.</summary>
    public static HashSet<string> Tokens(string? payee, string? purpose) =>
        Words(payee + " " + purpose).Where(w => w.Length >= 3 && !Noise.Contains(w)).ToHashSet(StringComparer.Ordinal);

    private static string[] Words(string text) =>
        NonLetters().Replace(RuleExpression.Fold(text), " ").Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Normalised IBAN (no spaces, upper case), or null.</summary>
    public static string? IbanKey(string? iban)
    {
        if (string.IsNullOrWhiteSpace(iban)) return null;
        var sb = new StringBuilder(iban.Length);
        foreach (var ch in iban) if (!char.IsWhiteSpace(ch)) sb.Append(char.ToUpperInvariant(ch));
        return sb.Length >= 12 ? sb.ToString() : null;
    }

    /// <summary>
    /// The category that at least <see cref="IbanDominance"/> of the earlier bookings of the same counterparty IBAN
    /// have (at least two of them), or null.
    /// </summary>
    public Vote? IbanVote(TargetBooking t)
    {
        if (t.Iban == null || !_byIban.TryGetValue(t.Iban, out var list) || list.Count < 2) return null;
        var top = list.GroupBy(h => h.CategoryId).Select(g => (Id: g.Key, Count: g.Count())).OrderByDescending(g => g.Count).First();
        var share = (double)top.Count / list.Count;
        if (share < IbanDominance) return null;
        // Two of two is good, twenty of twenty is better.
        var confidence = Math.Min(0.98, share * (1 - (0.3 / list.Count)));
        return new Vote(top.Id, confidence, "iban", $"{top.Count} of {list.Count} bookings with this account");
    }

    /// <summary>The <paramref name="k"/> most similar categorized bookings (Jaccard of words; same payee ≥ 0.9).</summary>
    public List<Neighbour> Neighbours(TargetBooking t, int k = 7)
    {
        var candidates = new HashSet<HistoryBooking>(ReferenceEqualityComparer.Instance);
        if (t.PayeeKey.Length > 0 && _byPayee.TryGetValue(t.PayeeKey, out var same)) candidates.UnionWith(same);
        foreach (var token in t.Tokens)
            if (_byToken.TryGetValue(token, out var list) && list.Count < 2000) candidates.UnionWith(list);
        return candidates
            .Select(h => new Neighbour(h, Similarity(t, h)))
            .Where(n => n.Similarity > 0.15)
            .OrderByDescending(n => n.Similarity).ThenByDescending(n => n.Booking.Id)
            .Take(k)
            .ToList();
    }

    public static double Similarity(TargetBooking t, HistoryBooking h)
    {
        var inter = t.Tokens.Count(h.Tokens.Contains);
        var union = t.Tokens.Count + h.Tokens.Count - inter;
        var jaccard = union == 0 ? 0 : (double)inter / union;
        if (t.PayeeKey.Length > 0 && t.PayeeKey == h.PayeeKey) jaccard = 0.9 + (0.1 * jaccard);
        // Money in and money out rarely share a category.
        if (Math.Sign(t.Amount) != Math.Sign(h.Amount)) jaccard *= 0.5;
        return jaccard;
    }

    /// <summary>Similarity-weighted vote of the neighbours; confidence = vote share × best similarity of the winner.</summary>
    public static Vote? KnnVote(IReadOnlyList<Neighbour> neighbours)
    {
        if (neighbours.Count == 0) return null;
        var total = neighbours.Sum(n => n.Similarity);
        var top = neighbours.GroupBy(n => n.Booking.CategoryId)
            .Select(g => (Id: g.Key, Weight: g.Sum(n => n.Similarity), Best: g.Max(n => n.Similarity), Count: g.Count()))
            .OrderByDescending(g => g.Weight).First();
        var confidence = top.Weight / total * top.Best;
        // A single similar booking is weak evidence.
        if (top.Count == 1) confidence *= 0.8;
        return new Vote(top.Id, Math.Round(confidence, 3), "knn", $"{top.Count} of {neighbours.Count} similar bookings");
    }

    [GeneratedRegex(@"[^A-Z]+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NonLetters();
}
