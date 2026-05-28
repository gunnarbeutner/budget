namespace OpenBudget.Services;

/// <summary>
/// Pure helpers for the month / YTD / year "anchor + period" time model shared by the
/// dashboard, project Overview, and Browse. Centralises the range math, future-clamping,
/// and the today-marker position that used to be copy-pasted between those pages.
///
/// An anchor is a (year, month) reference point. The period reinterprets it:
///   "month" → just the anchor month, "ytd" → Jan..anchorMonth, "year" → Jan..Dec.
/// </summary>
public static class Anchoring
{
    /// <summary>The inclusive month range [From, To] the period spans for a given anchor month.</summary>
    public static (int FromMonth, int ToMonth) Range(int anchorMonth, string period) => period switch
    {
        "year" => (1, 12),
        "ytd" => (1, anchorMonth),
        _ => (anchorMonth, anchorMonth),
    };

    /// <summary>Pull an anchor back to the current month if it's in the future; floor the year at 1900.</summary>
    public static (int Year, int Month) Clamp((int Year, int Month) anchor, DateTime now)
    {
        var year = Math.Max(anchor.Year, 1900);
        var picked = new DateTime(year, anchor.Month, 1);
        var max = new DateTime(now.Year, now.Month, 1);
        if (picked > max) picked = max;
        return (picked.Year, picked.Month);
    }

    /// <summary>Step the anchor one period (a month, or a year in "year" mode), clamped to the present.</summary>
    public static (int Year, int Month) Step((int Year, int Month) anchor, string period, int direction, DateTime now)
    {
        var dt = new DateTime(anchor.Year, anchor.Month, 1);
        dt = period == "year" ? dt.AddYears(direction) : dt.AddMonths(direction);
        return Clamp((dt.Year, dt.Month), now);
    }

    /// <summary>True when stepping forward would land in the future — used to disable the "next" button.</summary>
    public static bool IsAtMax((int Year, int Month) anchor, string period, DateTime now)
        => period == "year"
            ? anchor.Year >= now.Year
            : anchor.Year > now.Year || (anchor.Year == now.Year && anchor.Month >= now.Month);

    /// <summary>True when the anchor already sits on the current month — used to disable "Today".</summary>
    public static bool IsToday((int Year, int Month) anchor, DateTime now)
        => anchor.Year == now.Year && anchor.Month == now.Month;

    /// <summary>
    /// Position of "today" within the selected period as a percentage [0..100], or null when today
    /// falls outside the period or the period is a single month (where the marker isn't informative).
    /// </summary>
    public static double? TodayMarkerPercent((int Year, int Month) anchor, string period, DateTime today)
    {
        if (period == "month") return null;
        var (fromMonth, toMonth) = Range(anchor.Month, period);
        var start = new DateTime(anchor.Year, fromMonth, 1);
        var endExclusive = new DateTime(anchor.Year, toMonth, 1).AddMonths(1);
        if (today < start || today >= endExclusive) return null;
        var totalDays = (endExclusive - start).TotalDays;
        var elapsedDays = (today - start).TotalDays;
        return elapsedDays / totalDays * 100.0;
    }
}
