namespace OpenBudget.Assist;

/// <summary>A booking of a category for the baseline: <paramref name="Amount"/> is positive for spending (expense
/// categories) or income (income categories).</summary>
public sealed record BaselineBooking(long Id, DateOnly Date, decimal Amount, string PayeeKey);

/// <summary>A known schedule (standing order): payee, amount per occurrence (same sign as the bookings) and months between occurrences.</summary>
public sealed record BaselineSchedule(string PayeeKey, decimal Amount, int IntervalMonths, DateOnly NextDate);

/// <summary>A payment repeating every <see cref="IntervalMonths"/> months (1, 3, 6 or 12).</summary>
public sealed record RecurringSeries(string PayeeKey, int IntervalMonths, decimal Amount, DateOnly LastDate, IReadOnlyList<long> BookingIds);

/// <summary>The deterministic budget proposal for one category.</summary>
public sealed record BudgetBaseline(
    decimal[] Monthly, string Basis, IReadOnlyList<BaselineBooking> Outliers, IReadOnlyList<RecurringSeries> Recurring,
    int MonthsOfData, decimal Median, decimal Mad, decimal Trend);

/// <summary>
/// Statistics behind "Suggest budget": per category, last year's total (the 12 months before the cut-off) without clear
/// one-offs (a single booking of its payee, robust z-score over 3.5 and ten times the usual booking). Payments
/// repeating every 3, 6 or 12 months from the same payee (amounts within ±10 %) stay in their calendar month, standing
/// orders the bank holds land where they are due; the rest is spread by the category's month pattern over up to 36
/// months when that is clearly seasonal, otherwise evenly.
/// </summary>
public static class BudgetBaselineMath
{
    public const double OutlierZ = 3.5;
    private static readonly int[] Intervals = [1, 3, 6, 12];

    public static decimal Median(IReadOnlyList<decimal> values)
    {
        if (values.Count == 0) return 0;
        var s = values.Order().ToList();
        return s.Count % 2 == 1 ? s[s.Count / 2] : (s[(s.Count / 2) - 1] + s[s.Count / 2]) / 2;
    }

    /// <summary>Median absolute deviation.</summary>
    public static decimal Mad(IReadOnlyList<decimal> values)
    {
        var m = Median(values);
        return Median(values.Select(v => Math.Abs(v - m)).ToList());
    }

    /// <summary>0.6745 (x − median) / MAD. With a MAD of 0 (most values equal) any other value is infinitely far off.</summary>
    public static double RobustZ(decimal x, decimal median, decimal mad) =>
        mad == 0 ? (x == median ? 0 : Math.Sign(x - median) * double.PositiveInfinity) : 0.6745 * (double)((x - median) / mad);

    private static int MonthIndex(DateOnly d) => (d.Year * 12) + d.Month - 1;

    /// <summary>Series of bookings of one payee that repeat every 1, 3, 6 or 12 months with amounts within ±10 %.</summary>
    public static List<RecurringSeries> FindRecurring(IReadOnlyList<BaselineBooking> bookings)
    {
        var result = new List<RecurringSeries>();
        foreach (var g in bookings.Where(b => b.PayeeKey.Length > 0).GroupBy(b => b.PayeeKey, StringComparer.Ordinal))
        {
            var list = g.OrderBy(b => b.Date).ToList();
            if (list.Count < 2) continue;
            var last = list[^1];
            // The longest run back from the newest booking with a steady gap and amount.
            foreach (var interval in Intervals)
            {
                var run = new List<BaselineBooking> { last };
                for (var i = list.Count - 2; i >= 0; i--)
                {
                    var gap = MonthIndex(run[^1].Date) - MonthIndex(list[i].Date);
                    if (gap == 0 && interval == 1) continue;
                    if (gap != interval) continue;
                    if (Math.Abs(list[i].Amount - last.Amount) > Math.Abs(last.Amount) * 0.1m) continue;
                    run.Add(list[i]);
                }
                var needed = interval == 1 ? 3 : 2;
                if (run.Count < needed) continue;
                result.Add(new RecurringSeries(g.Key, interval, Median(run.Select(r => r.Amount).ToList()), last.Date, run.Select(r => r.Id).ToList()));
                break;
            }
        }
        return result;
    }

    /// <summary>
    /// The proposal for the 12 months of <paramref name="year"/> from bookings dated before <paramref name="asOf"/>
    /// (the window is the 36 months before it).
    /// </summary>
    public static BudgetBaseline Compute(IReadOnlyList<BaselineBooking> all, IReadOnlyList<BaselineSchedule> schedules, int year, DateOnly asOf)
    {
        var endIndex = MonthIndex(asOf);
        var startIndex = endIndex - 36;
        var bookings = all.Where(b => MonthIndex(b.Date) >= startIndex && MonthIndex(b.Date) < endIndex).ToList();
        if (bookings.Count == 0) return new BudgetBaseline(new decimal[12], "none", [], [], 0, 0, 0, 0);
        var firstIndex = Math.Max(startIndex, bookings.Min(b => MonthIndex(b.Date)));
        var months = endIndex - firstIndex;

        var recurring = FindRecurring(bookings).Where(r => r.IntervalMonths > 1 || r.BookingIds.Count >= 3).ToList();
        // Standing orders the bank knows are recurring even when the history is short.
        foreach (var s in schedules.Where(s => s.IntervalMonths > 1 && s.PayeeKey.Length > 0))
        {
            if (recurring.Any(r => r.PayeeKey == s.PayeeKey)) continue;
            var ids = bookings.Where(b => b.PayeeKey == s.PayeeKey).Select(b => b.Id).ToList();
            recurring.Add(new RecurringSeries(s.PayeeKey, s.IntervalMonths, s.Amount, s.NextDate.AddMonths(-s.IntervalMonths), ids));
        }
        var periodic = recurring.Where(r => r.IntervalMonths > 1).ToList();
        var periodicIds = periodic.SelectMany(r => r.BookingIds).ToHashSet();
        var recurringIds = recurring.SelectMany(r => r.BookingIds).ToHashSet();

        // Clear one-offs: a single booking of its payee, far above the usual size (robust z over 3.5 and ten times the
        // median booking). Anything less clear stays in, as leaving it out made a backtest worse.
        var sizes = bookings.Select(b => b.Amount).ToList();
        var medSize = Median(sizes);
        var madSize = Mad(sizes);
        var outliers = bookings
            .Where(b => !recurringIds.Contains(b.Id) && RobustZ(b.Amount, medSize, madSize) > OutlierZ && b.Amount > 10 * Math.Max(medSize, 1)
                && bookings.Count(o => o.PayeeKey == b.PayeeKey) == 1)
            .ToList();
        var outlierIds = outliers.Select(o => o.Id).ToHashSet();

        var rest = new decimal[months];
        foreach (var b in bookings.Where(b => !periodicIds.Contains(b.Id) && !outlierIds.Contains(b.Id)))
            rest[MonthIndex(b.Date) - firstIndex] += b.Amount;
        var median = Median(rest);
        var mad = Mad(rest);
        // Last year's total (without clear one-offs and periodic payments) as the level; the longer window only gives
        // the trend shown and the month pattern.
        var mean = rest.Average();
        var recent = rest.Skip(Math.Max(0, months - 12)).Average();
        var trend = mean == 0 ? 0 : Math.Clamp((recent - mean) / Math.Abs(mean), -0.25m, 0.25m);
        var level = recent;

        // Spread by the category's own month pattern only when it is clearly seasonal, otherwise evenly.
        var season = Seasonality(rest, firstIndex);
        if (months < 24 || !season.Any(f => Math.Abs(f - 1) > 0.25m)) season = Enumerable.Repeat(1m, 12).ToArray();
        var monthly = new decimal[12];
        for (var m = 0; m < 12; m++) monthly[m] = level * season[m];
        // A payment the bank holds as a standing order lands where it is due; other periodic payments of the last 12
        // months come back in their calendar month at last year's amount (one that stopped earlier does not).
        var lastYear = bookings.Where(b => MonthIndex(b.Date) >= endIndex - 12).ToList();
        foreach (var r in periodic)
        {
            if (schedules.Any(s => s.PayeeKey == r.PayeeKey && s.IntervalMonths == r.IntervalMonths))
            {
                foreach (var m in MonthsOf(r, year)) monthly[m] += r.Amount;
                continue;
            }
            var ids = r.BookingIds.ToHashSet();
            foreach (var b in lastYear.Where(b => ids.Contains(b.Id))) monthly[b.Date.Month - 1] += b.Amount;
        }
        for (var m = 0; m < 12; m++) monthly[m] = Math.Round(monthly[m], 0, MidpointRounding.AwayFromZero);

        var periodicTotal = periodic.Sum(r => r.Amount * MonthsOf(r, year).Count());
        var total = monthly.Sum();
        var basis = total == 0 ? "none"
            : periodicTotal > 0 && periodicTotal > total / 2 ? "recurring"
            : season.Any(f => Math.Abs(f - 1) > 0.25m) ? "seasonal"
            : trend != 0 && Math.Abs(trend) >= 0.1m ? "trend"
            : "median";
        return new BudgetBaseline(monthly, basis, outliers, recurring, months, median, mad, trend);
    }

    /// <summary>
    /// The months scaled to a new year total (whole currency units, the rounding remainder in the last non-zero month);
    /// spread evenly when all months are zero.
    /// </summary>
    public static decimal[] ScaleTo(IReadOnlyList<decimal> months, decimal total)
    {
        var sum = months.Sum();
        if (sum == total) return months.ToArray();
        var result = sum == 0
            ? Enumerable.Repeat(Math.Floor(total / 12), 12).ToArray()
            : months.Select(m => Math.Round(m * total / sum, 0, MidpointRounding.AwayFromZero)).ToArray();
        var last = Array.FindLastIndex(result, m => m != 0);
        result[last < 0 ? 11 : last] += total - result.Sum();
        return result;
    }

    /// <summary>The month indexes (0–11) of <paramref name="year"/> a periodic payment falls in.</summary>
    public static IEnumerable<int> MonthsOf(RecurringSeries r, int year)
    {
        var index = MonthIndex(r.LastDate);
        var first = (year * 12);
        while (index < first) index += r.IntervalMonths;
        for (; index < first + 12; index += r.IntervalMonths) yield return index - first;
    }

    /// <summary>
    /// Factor per calendar month: its average over the overall average, shrunk towards 1 by how many years back it
    /// (n / (n + 2)). All 1 without at least 12 months or with a non-positive average.
    /// </summary>
    public static decimal[] Seasonality(IReadOnlyList<decimal> monthly, int firstIndex)
    {
        var factors = Enumerable.Repeat(1m, 12).ToArray();
        if (monthly.Count < 12) return factors;
        var mean = monthly.Average();
        if (mean <= 0) return factors;
        for (var m = 0; m < 12; m++)
        {
            var values = monthly.Where((_, i) => (firstIndex + i) % 12 == m).ToList();
            if (values.Count == 0) continue;
            var n = values.Count;
            var raw = values.Average() / mean;
            factors[m] = 1 + ((raw - 1) * n / (n + 2));
        }
        // Keep the year's total: the factors average to 1.
        var avg = factors.Average();
        return avg == 0 ? factors : factors.Select(f => f / avg).ToArray();
    }
}
