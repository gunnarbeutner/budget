using System.Globalization;
using System.Text;

namespace OpenBudget.Services;

/// <summary>A booking as the detector reads it: cents negative for money leaving the account.</summary>
public sealed record RecurringBooking(long Id, long AccountId, DateOnly Date, long Cents, string? Name, string? Purpose, long? CategoryId, string? CounterpartyIban);

/// <summary>A bank standing order the detector leaves alone: the bank already reports its executions.</summary>
public sealed record StandingOrderKey(long AccountId, long Cents, string? CounterpartyName, string? CounterpartyIban);

/// <summary>The rhythm of a series: every <see cref="Count"/> weeks or months.</summary>
public readonly record struct RecurringRhythm(bool Weekly, int Count)
{
    public static readonly RecurringRhythm Week = new(true, 1);
    public static readonly RecurringRhythm Month = new(false, 1);
    public static readonly RecurringRhythm Quarter = new(false, 3);
    public static readonly RecurringRhythm HalfYear = new(false, 6);
    public static readonly RecurringRhythm Year = new(false, 12);

    /// <summary>The rhythms a series can have, shortest first.</summary>
    public static readonly RecurringRhythm[] All = [Week, Month, Quarter, HalfYear, Year];

    /// <summary>The <paramref name="n"/>-th occurrence after <paramref name="from"/>; months count from that day (the 31st becomes the month's last day).</summary>
    public DateOnly After(DateOnly from, int n = 1) => Weekly ? from.AddDays(7 * Count * n) : from.AddMonths(Count * n);

    /// <summary>How far an occurrence may be off its expected day: weekends and months of different lengths.</summary>
    public int ToleranceDays => Weekly ? 2 : Count >= 12 ? 7 : 5;

    /// <summary>Occurrences per year.</summary>
    public decimal PerYear => Weekly ? 52m / Count : 12m / Count;
}

public enum RecurringStatus
{
    /// <summary>The last occurrence is at most 1.5 rhythms ago.</summary>
    Active,

    /// <summary>One expected occurrence is missing.</summary>
    Overdue,

    /// <summary>Two or more expected occurrences are missing.</summary>
    Ended,
}

/// <summary>
/// A detected series of recurring charges. <see cref="Key"/> identifies it across reads (account, payee, first booking);
/// amounts are cents, negative. <see cref="PreviousCents"/>/<see cref="PriceChangedOn"/> are set when the amount stepped
/// from one stable value to another.
/// </summary>
public sealed record RecurringSeries(
    string Key,
    long AccountId,
    string Payee,
    RecurringRhythm Rhythm,
    long Cents,
    DateOnly LastDate,
    DateOnly NextDate,
    RecurringStatus Status,
    long? CategoryId,
    long? PreviousCents,
    DateOnly? PriceChangedOn,
    IReadOnlyList<RecurringBooking> Bookings)
{
    /// <summary>The cost per month, cents (negative).</summary>
    public long MonthlyCents => (long)Math.Round(Cents * Rhythm.PerYear / 12m, MidpointRounding.AwayFromZero);

    /// <summary>The occurrences expected in <paramref name="from"/>..<paramref name="to"/>; older missed ones are not owed.</summary>
    public IEnumerable<DateOnly> Occurrences(DateOnly from, DateOnly to)
    {
        // A charge that is a little late (the one after it is not yet due) is expected on "from".
        if (Rhythm.After(LastDate) < from && Rhythm.After(LastDate, 2) > from && from <= to) yield return from;
        for (var n = 1; n < 1000; n++)
        {
            var date = Rhythm.After(LastDate, n);
            if (date > to) yield break;
            if (date >= from) yield return date;
        }
    }
}

/// <summary>
/// Finds recurring charges in a booking history. Rules:
/// <list type="bullet">
/// <item>Only money leaving an account counts; bookings to or from one of the household's own accounts (counterparty IBAN in
/// <c>ownIbans</c>) are transfers and left out, as are payments to a person of the household (every word of one of
/// <c>ownNames</c> in the payee, e.g. "Gunnar Nils Beutner" for "Gunnar Beutner"), which often carry no IBAN.</item>
/// <item>A series is dropped when the payee has as many other bookings as the series in its time span (a shop visited
/// irregularly).</item>
/// <item>Bookings are grouped per account by payee (<see cref="NormalizePayee"/>); payees of an account that share a
/// distinctive word ("Anthropic Claude SUB", "Claude.ai Subscription") form one group.</item>
/// <item>Within a group, bookings within 5 % (at least 10 cents) of each other form an amount cluster, so several
/// subscriptions with one payee (an app store) are separate series. A cluster that starts about one rhythm after another
/// one ended continues it at a new price.</item>
/// <item>A series is the longest run of a cluster, ending at its last booking, whose bookings follow one rhythm (weekly,
/// monthly, quarterly, half-yearly, yearly) within <see cref="RecurringRhythm.ToleranceDays"/>; it needs 3 bookings (4
/// when weekly).</item>
/// <item>A series on an account whose standing order matches (same counterparty IBAN, or the same amount and a payee
/// word in common) is left out: the bank reports those.</item>
/// </list>
/// </summary>
public static class RecurringPaymentDetector
{
    public const int MinOccurrences = 3;
    public const int MinWeeklyOccurrences = 4;

    private static readonly HashSet<string> Generic = new(StringComparer.Ordinal)
    {
        "paypal", "europe", "sarl", "sub", "subscription", "abo", "bill", "billing", "com", "www", "http", "https", "gmbh", "ltd", "llc", "inc",
        "lastschrift", "sepa", "kartenzahlung", "karte", "card", "visa", "mastercard", "debit", "online", "payment", "zahlung", "und", "the",
        "de", "eu", "uk", "us", "net", "org", "mktp", "co", "ag", "kg", "se",
    };

    /// <summary>
    /// The payee a booking groups by: lower case without accents, the part before a "*" (a card descriptor's reference),
    /// with punctuation as spaces and words containing digits (references, ids, dates) dropped. "PayPal " stays a word.
    /// </summary>
    public static string NormalizePayee(string? name, string? purpose = null)
    {
        var text = string.IsNullOrWhiteSpace(name) ? purpose ?? string.Empty : name;
        var star = text.IndexOf('*', StringComparison.Ordinal);
        if (star > 0) text = text[..star];
        var folded = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat == UnicodeCategory.NonSpacingMark) continue;
            folded.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }
        var words = folded.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => !w.Any(char.IsDigit));
        return string.Join(' ', words);
    }

    private static IEnumerable<string> Distinctive(string payee) =>
        payee.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 4 && !Generic.Contains(w));

    /// <summary>Series costing less per month are left out.</summary>
    public const long MinMonthlyCents = 10;

    public static bool SameAmount(long a, long b) => Math.Abs(a - b) <= Math.Max(10, Math.Max(Math.Abs(a), Math.Abs(b)) * 5 / 100);

    public static List<RecurringSeries> Detect(IEnumerable<RecurringBooking> bookings, IReadOnlyCollection<StandingOrderKey> standing,
        IReadOnlySet<string> ownIbans, DateOnly today, IReadOnlyCollection<string>? ownNames = null)
    {
        var result = new List<RecurringSeries>();
        var people = (ownNames ?? []).Select(Words).Where(w => w.Count >= 2).ToList();
        var outgoing = bookings.Where(b => b.Cents < 0 && b.Date <= today
            && (b.CounterpartyIban == null || !ownIbans.Contains(NormalizeIban(b.CounterpartyIban)))
            && !IsPerson(b.Name, people)).ToList();
        foreach (var account in outgoing.GroupBy(b => b.AccountId))
        {
            foreach (var group in GroupByPayee(account))
            {
                var found = Clusters(group).Select(c => Evaluate(c, today)).OfType<RecurringSeries>().ToList();
                // A shop visited now and then (a supermarket) can show a regular run by chance: a series counts only when
                // the payee's other bookings in its time span are fewer than its own.
                var inSeries = found.SelectMany(f => f.Bookings).Select(b => b.Id).ToHashSet();
                foreach (var series in found)
                {
                    var from = series.Bookings.Min(b => b.Date);
                    var to = series.Bookings.Max(b => b.Date);
                    var others = group.Count(b => !inSeries.Contains(b.Id) && b.Date >= from && b.Date <= to);
                    if (others >= series.Bookings.Count) continue;
                    // A few cents a year (a tax deduction on interest) is no subscription.
                    if (series.MonthlyCents > -MinMonthlyCents) continue;
                    if (!CoveredByStandingOrder(series, standing)) result.Add(series);
                }
            }
        }
        return result.OrderBy(s => s.MonthlyCents).ThenBy(s => s.Payee, StringComparer.Ordinal).ToList();
    }

    public static string NormalizeIban(string iban) => iban.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();

    /// <summary>The account's bookings per payee, payees sharing a distinctive word merged.</summary>
    private static List<List<RecurringBooking>> GroupByPayee(IEnumerable<RecurringBooking> bookings)
    {
        var byPayee = bookings.GroupBy(b => NormalizePayee(b.Name, b.Purpose), StringComparer.Ordinal).Where(g => g.Key.Length > 0).ToList();
        var parent = Enumerable.Range(0, byPayee.Count).ToArray();
        int Find(int i)
        {
            while (parent[i] != i) i = parent[i];
            return i;
        }
        var owner = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < byPayee.Count; i++)
            foreach (var word in Distinctive(byPayee[i].Key))
            {
                if (owner.TryGetValue(word, out var j)) parent[Find(i)] = Find(j);
                else owner[word] = i;
            }
        return Enumerable.Range(0, byPayee.Count).GroupBy(Find)
            .Select(g => g.SelectMany(i => byPayee[i]).OrderBy(b => b.Date).ThenBy(b => b.Id).ToList()).ToList();
    }

    /// <summary>Amount clusters of one payee, oldest first; a cluster that continues another one's rhythm at a new price joins it.</summary>
    private static List<List<RecurringBooking>> Clusters(List<RecurringBooking> group)
    {
        var clusters = new List<List<RecurringBooking>>();
        foreach (var b in group)
        {
            var match = clusters.FirstOrDefault(c => SameAmount(c[^1].Cents, b.Cents));
            if (match != null) match.Add(b);
            else clusters.Add([b]);
        }
        var merged = true;
        while (merged)
        {
            merged = false;
            foreach (var a in clusters)
            {
                var b = clusters.FirstOrDefault(c => c != a && c[0].Date > a[^1].Date && Continues(a, c));
                if (b == null) continue;
                a.AddRange(b);
                clusters.Remove(b);
                merged = true;
                break;
            }
        }
        return clusters;
    }

    /// <summary>Whether <paramref name="next"/> takes over from <paramref name="prev"/>: the joined bookings keep one rhythm across the step.</summary>
    private static bool Continues(List<RecurringBooking> prev, List<RecurringBooking> next)
    {
        var joined = prev.Concat(next).ToList();
        foreach (var rhythm in RecurringRhythm.All)
        {
            var run = TrailingRun(joined, rhythm);
            if (run > next.Count && run >= MinOccurrences) return true;
        }
        return false;
    }

    /// <summary>How many bookings at the end of <paramref name="dates"/> follow <paramref name="rhythm"/>.</summary>
    private static int TrailingRun(List<RecurringBooking> dates, RecurringRhythm rhythm)
    {
        var run = 1;
        for (var i = dates.Count - 1; i > 0; i--)
        {
            var expected = rhythm.After(dates[i - 1].Date);
            if (Math.Abs(dates[i].Date.DayNumber - expected.DayNumber) > rhythm.ToleranceDays) break;
            run++;
        }
        return run;
    }

    private static RecurringSeries? Evaluate(List<RecurringBooking> cluster, DateOnly today)
    {
        RecurringRhythm? best = null;
        var bestRun = 0;
        foreach (var rhythm in RecurringRhythm.All)
        {
            var run = TrailingRun(cluster, rhythm);
            if (run < (rhythm.Weekly ? MinWeeklyOccurrences : MinOccurrences) || run <= bestRun) continue;
            best = rhythm;
            bestRun = run;
        }
        if (best is not { } r) return null;
        var bookings = cluster.Skip(cluster.Count - bestRun).ToList();
        var last = bookings[^1];
        var next = r.After(last.Date);
        var halfRhythm = (next.DayNumber - last.Date.DayNumber) / 2;
        var status = today.DayNumber <= next.DayNumber + halfRhythm ? RecurringStatus.Active
            : today.DayNumber <= r.After(last.Date, 2).DayNumber + r.ToleranceDays ? RecurringStatus.Overdue
            : RecurringStatus.Ended;

        // A price change: the latest step from one amount to another, with the old amount paid at least twice before it.
        long? previous = null;
        DateOnly? changedOn = null;
        for (var i = bookings.Count - 1; i > 0; i--)
        {
            if (bookings[i].Cents == bookings[i - 1].Cents) continue;
            var after = bookings.Skip(i).All(b => b.Cents == bookings[i].Cents);
            var before = bookings.Take(i).Count(b => b.Cents == bookings[i - 1].Cents) >= 2;
            if (after && before)
            {
                previous = bookings[i - 1].Cents;
                changedOn = bookings[i].Date;
            }
            break;
        }

        var payee = (last.Name ?? last.Purpose ?? string.Empty).Trim();
        var key = string.Create(CultureInfo.InvariantCulture, $"{last.AccountId}:{NormalizePayee(bookings[0].Name, bookings[0].Purpose)}:{bookings[0].Id}");
        var category = bookings.LastOrDefault(b => b.CategoryId != null)?.CategoryId;
        return new RecurringSeries(key, last.AccountId, payee, r, last.Cents, last.Date, next, status, category, previous, changedOn, bookings);
    }

    private static bool CoveredByStandingOrder(RecurringSeries series, IReadOnlyCollection<StandingOrderKey> standing)
    {
        var ibans = series.Bookings.Where(b => b.CounterpartyIban != null).Select(b => NormalizeIban(b.CounterpartyIban!)).ToHashSet(StringComparer.Ordinal);
        var words = series.Bookings.SelectMany(b => Distinctive(NormalizePayee(b.Name, b.Purpose))).ToHashSet(StringComparer.Ordinal);
        return standing.Any(o => o.AccountId == series.AccountId
            && ((o.CounterpartyIban != null && ibans.Contains(NormalizeIban(o.CounterpartyIban)))
                || (SameAmount(o.Cents, series.Cents) && Distinctive(NormalizePayee(o.CounterpartyName)).Any(words.Contains))));
    }

    private static HashSet<string> Words(string? text) =>
        new string(OpenBudget.Services.SearchText.Fold(text ?? "").Select(c => char.IsAsciiLetterOrDigit(c) ? c : ' ').ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);

    /// <summary>Whether a payee names one of the household's people: all words of their name appear in it.</summary>
    private static bool IsPerson(string? payee, List<HashSet<string>> people)
    {
        if (people.Count == 0 || string.IsNullOrWhiteSpace(payee)) return false;
        var words = Words(payee);
        return people.Any(p => p.IsSubsetOf(words));
    }
}
