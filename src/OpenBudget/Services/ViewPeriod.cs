using System.Globalization;
using Microsoft.Extensions.Primitives;

namespace OpenBudget.Services;

/// <summary>
/// The period the project views show: a (year, month) anchor read as a month, the year to date, the whole year, or a
/// custom range of days. In URLs it is <c>year</c>, <c>month</c>, <c>period</c> (month / ytd / year / range) and,
/// for a range, <c>from</c> and <c>to</c> (both inclusive, yyyy-MM-dd). The older <c>per</c> key of the transaction
/// list's filter is still understood: a year opens that year, a month that month, anything else (a quarter, the
/// last N days or months, a custom from/to) a custom range covering the same days.
/// </summary>
public sealed record ViewPeriod(int Year, int Month, string Mode, DateOnly? From = null, DateOnly? To = null)
{
    public const string Range = "range";

    public bool IsRange => Mode == Range && From != null && To != null;

    /// <summary>The months of <see cref="Year"/> the period covers. A range covers its months in its last year.</summary>
    public (int FromMonth, int ToMonth) Months => IsRange
        ? (From!.Value.Year == To!.Value.Year ? From.Value.Month : 1, To.Value.Month)
        : Anchoring.Range(Month, Mode);

    /// <summary>The first and last day the period covers.</summary>
    public (DateOnly From, DateOnly To) Days
    {
        get
        {
            if (IsRange) return (From!.Value, To!.Value);
            var (fm, tm) = Months;
            return (new DateOnly(Year, fm, 1), new DateOnly(Year, tm, 1).AddMonths(1).AddDays(-1));
        }
    }

    /// <summary>The query parameters that carry this period (and drop the legacy filter key).</summary>
    public Dictionary<string, object?> ToQuery() => new(StringComparer.Ordinal)
    {
        ["year"] = Year,
        ["month"] = Month,
        ["period"] = Mode == "month" ? null : Mode,
        ["from"] = IsRange ? Format(From!.Value) : null,
        ["to"] = IsRange ? Format(To!.Value) : null,
        ["per"] = null,
    };

    /// <summary>The same parameters as a query string without the leading "?".</summary>
    public string ToQueryString() => string.Join("&", ToQuery()
        .Where(kv => kv.Value != null)
        .Select(kv => kv.Key + "=" + Uri.EscapeDataString(Convert.ToString(kv.Value, CultureInfo.InvariantCulture)!)));

    /// <summary>
    /// Overlays the period found in <paramref name="query"/> onto <paramref name="seed"/>. Unknown or malformed values
    /// are ignored. A range keeps its anchor on its last month.
    /// </summary>
    public static ViewPeriod Read(IReadOnlyDictionary<string, StringValues> query, ViewPeriod seed, DateOnly today)
    {
        DateOnly? qFrom = query.TryGetValue("from", out var f) && TryParseDay(f, out var fd) ? fd : null;
        DateOnly? qTo = query.TryGetValue("to", out var t) && TryParseDay(t, out var td) ? td : null;
        if (query.TryGetValue("per", out var per) && FromLegacyKey(per.ToString(), today, qFrom, qTo) is { } legacy)
            return legacy;

        var year = seed.Year;
        var month = seed.Month;
        if (query.TryGetValue("year", out var y) && int.TryParse(y, NumberStyles.None, CultureInfo.InvariantCulture, out var py) && py is >= 1900 and <= 2999)
            year = py;
        if (query.TryGetValue("month", out var m) && int.TryParse(m, NumberStyles.None, CultureInfo.InvariantCulture, out var pm) && pm is >= 1 and <= 12)
            month = pm;
        if (query.TryGetValue("period", out var p) && p.ToString() is "month" or "ytd" or "year" or Range)
        {
            if (p.ToString() != Range) return new ViewPeriod(year, month, p.ToString());
            if (qFrom != null && qTo != null) return WithRange(qFrom.Value, qTo.Value);
            return new ViewPeriod(year, month, "month");
        }
        if (seed.IsRange && !query.ContainsKey("year")) return seed;
        return new ViewPeriod(year, month, seed.IsRange ? "month" : seed.Mode);
    }

    /// <summary>A custom range (swapped when given backwards), anchored on its last month.</summary>
    public static ViewPeriod WithRange(DateOnly from, DateOnly to)
    {
        if (to < from) (from, to) = (to, from);
        return new ViewPeriod(to.Year, to.Month, Range, from, to);
    }

    /// <summary>The period a key of the former period filter stands for; null for "all" and unknown keys.</summary>
    public static ViewPeriod? FromLegacyKey(string? key, DateOnly today, DateOnly? customFrom = null, DateOnly? customTo = null)
    {
        if (key is not { Length: > 0 }) return null;
        if (key.StartsWith("y:", StringComparison.Ordinal) && int.TryParse(key[2..], NumberStyles.None, CultureInfo.InvariantCulture, out var year) && year is >= 1900 and <= 2999)
            return new ViewPeriod(year, 12, "year");
        if (key.StartsWith("m:", StringComparison.Ordinal) && TryParseYearPart(key[2..], 12, out var my, out var mm))
            return new ViewPeriod(my, mm, "month");
        if (key.StartsWith("q:", StringComparison.Ordinal) && TryParseYearPart(key[2..], 4, out var qy, out var q))
            return WithRange(new DateOnly(qy, (q - 1) * 3 + 1, 1), new DateOnly(qy, (q - 1) * 3 + 1, 1).AddMonths(3).AddDays(-1));
        return key switch
        {
            "30d" => WithRange(today.AddDays(-29), today),
            "90d" => WithRange(today.AddDays(-89), today),
            "12m" => WithRange(today.AddMonths(-12).AddDays(1), today),
            "custom" when customFrom != null || customTo != null => WithRange(customFrom ?? customTo!.Value, customTo ?? today),
            _ => null,
        };
    }

    public static string Format(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static bool TryParseDay(string? text, out DateOnly day) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day);

    private static bool TryParseYearPart(string text, int max, out int year, out int part)
    {
        year = part = 0;
        var bits = text.Split('-');
        return bits.Length == 2
            && int.TryParse(bits[0], NumberStyles.None, CultureInfo.InvariantCulture, out year) && year is >= 1900 and <= 2999
            && int.TryParse(bits[1], NumberStyles.None, CultureInfo.InvariantCulture, out part) && part >= 1 && part <= max;
    }
}
