using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using OpenBudget.Services;

namespace OpenBudget.Assist;

/// <summary>A categorized booking used as evidence. <see cref="Mandate"/> and <see cref="Creditor"/> are normalised SEPA keys.</summary>
public sealed record HistoryBooking(long Id, long CategoryId, string? Iban, string PayeeKey, IReadOnlySet<string> Tokens, long Amount)
{
    public string? Mandate { get; init; }

    public string? Creditor { get; init; }

    public DateOnly? Date { get; init; }
}

/// <summary>A booking to find a category for.</summary>
public sealed record TargetBooking(long Id, string? Iban, string PayeeKey, IReadOnlySet<string> Tokens, long Amount)
{
    public string? Mandate { get; init; }

    public string? Creditor { get; init; }

    public DateOnly? Date { get; init; }
}

/// <summary>A similar categorized booking and how similar it is (0..1).</summary>
public sealed record Neighbour(HistoryBooking Booking, double Similarity);

/// <summary>A deterministic vote: the category, how sure (0..1) and why.</summary>
public sealed record Vote(long CategoryId, double Confidence, string Source, string Rationale);

/// <summary>
/// The deterministic part of category suggestions: payee normalisation, word tokens, exact-key votes (the same
/// direct-debit mandate, the same creditor id, the same payee with the same recurring amount, the same counterparty
/// IBAN) and a k-nearest-neighbour vote over categorized bookings (similarity of words, an identical payee counts most,
/// within it a closer amount). Votes take an optional filter of the categories allowed for the booking.
/// </summary>
public sealed partial class CategoryMatching
{
    /// <summary>Share of an IBAN's bookings one category needs before the IBAN alone decides.</summary>
    public const double IbanDominance = 0.9;

    /// <summary>Relative amount difference that still counts as "the same amount".</summary>
    public const double AmountTolerance = 0.01;

    /// <summary>How far back the same payee and amount are looked for.</summary>
    public const int RecurrenceMonths = 18;

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
    private readonly Dictionary<string, List<HistoryBooking>> _byMandate = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<HistoryBooking>> _byCreditor = new(StringComparer.Ordinal);

    public CategoryMatching(IEnumerable<HistoryBooking> history)
    {
        _history = history.ToList();
        foreach (var h in _history)
        {
            foreach (var token in h.Tokens) Add(_byToken, token, h);
            if (h.PayeeKey.Length > 0) Add(_byPayee, h.PayeeKey, h);
            if (h.Iban is { Length: > 0 } iban) Add(_byIban, iban, h);
            if (h.Mandate is { Length: > 0 } mandate) Add(_byMandate, mandate, h);
            if (h.Creditor is { Length: > 0 } creditor) Add(_byCreditor, creditor, h);
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

    /// <summary>Normalised SEPA key (mandate reference, creditor id): no spaces, upper case; null when empty.</summary>
    public static string? SepaKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Equals("NOTPROVIDED", StringComparison.OrdinalIgnoreCase)) return null;
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value) if (!char.IsWhiteSpace(ch)) sb.Append(char.ToUpperInvariant(ch));
        return sb.Length >= 3 ? sb.ToString() : null;
    }

    /// <summary>Whether two signed amounts are the same within <see cref="AmountTolerance"/> (and of the same sign).</summary>
    public static bool SameAmount(long a, long b) =>
        a != 0 && Math.Sign(a) == Math.Sign(b) && Math.Abs(a - b) <= Math.Max(1, Math.Abs(a) * AmountTolerance);

    /// <summary>
    /// The category of the earlier bookings with the same mandate reference (and, when both have one, the same creditor
    /// id). A mandate stands for one contract, so this beats text similarity: with at least <see cref="IbanDominance"/>
    /// of them in one category it is sure; a single earlier booking still counts as strong.
    /// </summary>
    public Vote? MandateVote(TargetBooking t, Func<long, bool>? allowed = null)
    {
        if (t.Mandate == null || !_byMandate.TryGetValue(t.Mandate, out var all)) return null;
        var list = all.Where(h => h.Id != t.Id && (t.Creditor == null || h.Creditor == null || h.Creditor == t.Creditor)).ToList();
        if (Dominant(list, allowed) is not { } top) return null;
        var confidence = top.Total == 1 ? 0.8
            : top.Share >= IbanDominance ? Math.Min(0.98, top.Share * (1 - (0.15 / top.Total)))
            : top.Share * 0.7;
        return new Vote(top.Id, Math.Round(confidence, 3), "mandate", $"{top.Count} of {top.Total} bookings with this mandate");
    }

    /// <summary>
    /// The category of the earlier bookings with the same creditor id. One creditor often has several contracts
    /// (electricity and gas), so this is a weaker vote that stays below the sure-enough line.
    /// </summary>
    public Vote? CreditorVote(TargetBooking t, Func<long, bool>? allowed = null)
    {
        if (t.Creditor == null || !_byCreditor.TryGetValue(t.Creditor, out var all)) return null;
        if (Dominant(all.Where(h => h.Id != t.Id).ToList(), allowed) is not { } top || top.Total < 2 || top.Share < IbanDominance) return null;
        var confidence = Math.Min(0.7, top.Share * (1 - (0.3 / top.Total)));
        return new Vote(top.Id, Math.Round(confidence, 3), "creditor", $"{top.Count} of {top.Total} bookings with this creditor");
    }

    /// <summary>
    /// The earlier bookings of the same payee with the same amount (± <see cref="AmountTolerance"/>) in the
    /// <see cref="RecurrenceMonths"/> before the booking: a recurring charge (a subscription) when there are at least
    /// two and at least <see cref="IbanDominance"/> of them share a category.
    /// </summary>
    public Vote? AmountVote(TargetBooking t, Func<long, bool>? allowed = null)
    {
        if (Dominant(SamePayeeAndAmount(t).ToList(), allowed) is not { } top || top.Total < 2 || top.Share < IbanDominance) return null;
        var confidence = Math.Min(0.96, top.Share * (1 - (0.2 / top.Total)));
        return new Vote(top.Id, Math.Round(confidence, 3), "amount", $"{top.Count} of {top.Total} bookings with this payee and amount");
    }

    private IEnumerable<HistoryBooking> SamePayeeAndAmount(TargetBooking t)
    {
        if (t.PayeeKey.Length == 0 || t.Amount == 0 || !_byPayee.TryGetValue(t.PayeeKey, out var same)) return [];
        var from = t.Date?.AddMonths(-RecurrenceMonths);
        return same.Where(h => h.Id != t.Id && SameAmount(t.Amount, h.Amount) && (from == null || h.Date == null || (h.Date >= from && h.Date <= t.Date)));
    }

    /// <summary>
    /// Categorized bookings that share a contract key with the target, most specific first: the same mandate, the same
    /// payee and amount, the same creditor (newest first within each; at most <paramref name="max"/>).
    /// </summary>
    public List<HistoryBooking> KeyNeighbours(TargetBooking t, int max, Func<long, bool>? allowed = null)
    {
        var result = new List<HistoryBooking>();
        void Take(IEnumerable<HistoryBooking> list)
        {
            foreach (var h in list.Where(h => h.Id != t.Id && (allowed == null || allowed(h.CategoryId))).OrderByDescending(h => h.Date).ThenByDescending(h => h.Id))
            {
                if (result.Count >= max) return;
                if (!result.Contains(h)) result.Add(h);
            }
        }
        if (t.Mandate != null && _byMandate.TryGetValue(t.Mandate, out var m)) Take(m);
        Take(SamePayeeAndAmount(t));
        if (t.Creditor != null && _byCreditor.TryGetValue(t.Creditor, out var c)) Take(c);
        return result;
    }

    private static (long Id, int Count, int Total, double Share)? Dominant(List<HistoryBooking> list, Func<long, bool>? allowed)
    {
        if (allowed != null) list = list.Where(h => allowed(h.CategoryId)).ToList();
        if (list.Count == 0) return null;
        var top = list.GroupBy(h => h.CategoryId).Select(g => (Id: g.Key, Count: g.Count())).OrderByDescending(g => g.Count).First();
        return (top.Id, top.Count, list.Count, (double)top.Count / list.Count);
    }

    /// <summary>
    /// How often the payee recurs among <paramref name="others"/> (the household's other bookings of that payee, any
    /// category) in the <see cref="RecurrenceMonths"/> up to <paramref name="date"/>: "same payee and amount 14 times in
    /// 18 months, about monthly", "same payee 6 times in 18 months, amounts vary", or null.
    /// </summary>
    public static string? Recurrence(DateOnly date, long amount, IEnumerable<(DateOnly Date, long Amount)> others)
    {
        var from = date.AddMonths(-RecurrenceMonths);
        var window = others.Where(o => o.Date >= from && o.Date <= date && o.Amount != 0 && Math.Sign(o.Amount) == Math.Sign(amount)).ToList();
        var same = window.Where(o => SameAmount(amount, o.Amount)).Select(o => o.Date).ToList();
        if (same.Count >= 2)
            return string.Create(CultureInfo.InvariantCulture, $"same payee and amount {same.Count} times in {RecurrenceMonths} months{Cadence(same.Append(date))}");
        if (window.Count >= 3)
            return string.Create(CultureInfo.InvariantCulture, $"same payee {window.Count} times in {RecurrenceMonths} months, amounts vary{Cadence(window.Select(w => w.Date).Append(date))}");
        return null;
    }

    private static string Cadence(IEnumerable<DateOnly> dates)
    {
        var sorted = dates.Distinct().Order().ToList();
        if (sorted.Count < 3) return "";
        var gaps = sorted.Zip(sorted.Skip(1), (a, b) => b.DayNumber - a.DayNumber).Order().ToList();
        var word = gaps[gaps.Count / 2] switch
        {
            >= 6 and <= 8 => "weekly",
            >= 26 and <= 35 => "monthly",
            >= 56 and <= 66 => "every two months",
            >= 85 and <= 96 => "quarterly",
            >= 175 and <= 190 => "half-yearly",
            >= 350 and <= 380 => "yearly",
            _ => null,
        };
        return word == null ? "" : ", about " + word;
    }

    /// <summary>
    /// The category that at least <see cref="IbanDominance"/> of the earlier bookings of the same counterparty IBAN
    /// have (at least two of them), or null.
    /// </summary>
    public Vote? IbanVote(TargetBooking t, Func<long, bool>? allowed = null)
    {
        if (t.Iban == null || !_byIban.TryGetValue(t.Iban, out var all)) return null;
        if (Dominant(all.Where(h => h.Id != t.Id).ToList(), allowed) is not { } top || top.Total < 2 || top.Share < IbanDominance) return null;
        // Two of two is good, twenty of twenty is better.
        var confidence = Math.Min(0.98, top.Share * (1 - (0.3 / top.Total)));
        return new Vote(top.Id, confidence, "iban", $"{top.Count} of {top.Total} bookings with this account");
    }

    /// <summary>
    /// The deterministic decision after the rules: a sure mandate vote wins outright; otherwise the surest of the
    /// exact-key votes (mandate, payee and amount, IBAN, creditor), replaced by the kNN vote when that is surer and the
    /// key vote is below <paramref name="sureEnough"/>. <paramref name="neighbours"/> gets the kNN neighbours.
    /// </summary>
    public Vote? Decide(TargetBooking t, Func<long, bool>? allowed, double sureEnough, out List<Neighbour> neighbours)
    {
        neighbours = Neighbours(t, allowed: allowed);
        var mandate = MandateVote(t, allowed);
        if (mandate != null && mandate.Confidence >= sureEnough) return mandate;
        var vote = new[] { mandate, AmountVote(t, allowed), IbanVote(t, allowed), CreditorVote(t, allowed) }
            .Where(v => v != null).OrderByDescending(v => v!.Confidence).FirstOrDefault();
        if (vote == null || vote.Confidence < sureEnough)
        {
            var knn = KnnVote(neighbours);
            if (knn != null && (vote == null || knn.Confidence > vote.Confidence)) vote = knn;
        }
        return vote;
    }

    /// <summary>The <paramref name="k"/> most similar categorized bookings (Jaccard of words; same payee ≥ 0.9).</summary>
    public List<Neighbour> Neighbours(TargetBooking t, int k = 7, Func<long, bool>? allowed = null)
    {
        if (allowed != null) return Neighbours(t, int.MaxValue).Where(n => allowed(n.Booking.CategoryId)).Take(k).ToList();
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
        // Within the same payee a closer amount weighs more (a subscription next to one-off purchases).
        if (t.PayeeKey.Length > 0 && t.PayeeKey == h.PayeeKey) jaccard = 0.85 + (0.1 * jaccard) + (0.05 * AmountCloseness(t.Amount, h.Amount));
        // Money in and money out rarely share a category.
        if (Math.Sign(t.Amount) != Math.Sign(h.Amount)) jaccard *= 0.5;
        return jaccard;
    }

    /// <summary>1 for the same amount, falling to 0 at twice (or half) the amount.</summary>
    public static double AmountCloseness(long a, long b)
    {
        if (a == b) return 1;
        var max = Math.Max(Math.Abs(a), Math.Abs(b));
        return max == 0 ? 1 : Math.Max(0, 1 - ((double)Math.Abs(Math.Abs(a) - Math.Abs(b)) / max * 2));
    }

    /// <summary>Similarity-weighted vote of the neighbours; confidence = vote share × best similarity of the winner, lowered when they disagree.</summary>
    public static Vote? KnnVote(IReadOnlyList<Neighbour> neighbours)
    {
        if (neighbours.Count == 0) return null;
        var total = neighbours.Sum(n => n.Similarity);
        var top = neighbours.GroupBy(n => n.Booking.CategoryId)
            .Select(g => (Id: g.Key, Weight: g.Sum(n => n.Similarity), Best: g.Max(n => n.Similarity), Count: g.Count()))
            .OrderByDescending(g => g.Weight).First();
        var confidence = top.Weight / total * top.Best;
        // When the neighbours disagree, a high vote share still meant only about two in three right in a backtest.
        if (top.Count < neighbours.Count) confidence *= 0.75;
        // A single similar booking is weak evidence.
        if (top.Count == 1) confidence *= 0.8;
        return new Vote(top.Id, Math.Round(confidence, 3), "knn", $"{top.Count} of {neighbours.Count} similar bookings");
    }

    [GeneratedRegex(@"[^A-Z]+", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NonLetters();
}
