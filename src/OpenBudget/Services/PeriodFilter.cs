using System.Globalization;

namespace OpenBudget.Services;

/// <summary>One entry of the transaction list's period filter. <see cref="Kind"/> is "all", "days", "months", "month", "quarter", "year" or "custom".</summary>
public sealed record PeriodChoice(string Key, string Kind, int Year = 0, int Number = 0);

/// <summary>
/// The period filter of the transaction list: 30/90 days, 12 months, recent months / quarters / years, custom. Pure (the
/// caller passes "today" in the user's zone). Keys are what goes into the URL.
/// </summary>
public static class PeriodFilter
{
    public const string All = "all";
    public const string Custom = "custom";

    /// <summary>The choices in menu order: all, rolling windows, the last six months, the last four quarters, the last three years, custom.</summary>
    public static IReadOnlyList<PeriodChoice> Choices(DateOnly today)
    {
        var list = new List<PeriodChoice>
        {
            new(All, "all"),
            new("30d", "days", 0, 30),
            new("90d", "days", 0, 90),
            new("12m", "months", 0, 12),
        };
        var month = new DateOnly(today.Year, today.Month, 1);
        for (var i = 0; i < 6; i++)
        {
            var m = month.AddMonths(-i);
            list.Add(new PeriodChoice(MonthKey(m.Year, m.Month), "month", m.Year, m.Month));
        }
        var quarter = (today.Month - 1) / 3 + 1;
        var qYear = today.Year;
        for (var i = 0; i < 4; i++)
        {
            list.Add(new PeriodChoice(QuarterKey(qYear, quarter), "quarter", qYear, quarter));
            if (--quarter == 0) { quarter = 4; qYear--; }
        }
        for (var i = 0; i < 3; i++)
            list.Add(new PeriodChoice(YearKey(today.Year - i), "year", today.Year - i));
        list.Add(new PeriodChoice(Custom, "custom"));
        return list;
    }

    /// <summary>The date range (inclusive start, exclusive end) a key stands for; null bounds are open. Unknown keys mean "all".</summary>
    public static (DateOnly? From, DateOnly? ToExclusive) Resolve(string? key, DateOnly today, DateOnly? customFrom = null, DateOnly? customTo = null)
    {
        if (key == "30d") return (today.AddDays(-29), today.AddDays(1));
        if (key == "90d") return (today.AddDays(-89), today.AddDays(1));
        if (key == "12m") return (today.AddMonths(-12).AddDays(1), today.AddDays(1));
        if (key == Custom) return (customFrom, customTo?.AddDays(1));
        if (key is not { Length: > 2 } || key[1] != ':') return (null, null);

        var rest = key[2..];
        if (key[0] == 'm' && TryParseYearPart(rest, 12, out var y, out var m))
            return (new DateOnly(y, m, 1), new DateOnly(y, m, 1).AddMonths(1));
        if (key[0] == 'q' && TryParseYearPart(rest, 4, out var qy, out var q))
            return (new DateOnly(qy, (q - 1) * 3 + 1, 1), new DateOnly(qy, (q - 1) * 3 + 1, 1).AddMonths(3));
        if (key[0] == 'y' && int.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out var year) && year is >= 1900 and <= 2999)
            return (new DateOnly(year, 1, 1), new DateOnly(year + 1, 1, 1));
        return (null, null);
    }

    public static string MonthKey(int year, int month) => FormattableString.Invariant($"m:{year:0000}-{month:00}");

    public static string QuarterKey(int year, int quarter) => FormattableString.Invariant($"q:{year:0000}-{quarter}");

    public static string YearKey(int year) => FormattableString.Invariant($"y:{year:0000}");

    private static bool TryParseYearPart(string text, int max, out int year, out int part)
    {
        year = part = 0;
        var bits = text.Split('-');
        return bits.Length == 2
            && int.TryParse(bits[0], NumberStyles.None, CultureInfo.InvariantCulture, out year) && year is >= 1900 and <= 2999
            && int.TryParse(bits[1], NumberStyles.None, CultureInfo.InvariantCulture, out part) && part >= 1 && part <= max;
    }
}
