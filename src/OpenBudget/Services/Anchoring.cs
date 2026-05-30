namespace OpenBudget.Services;

/// <summary>
/// Pure helpers for the month / YTD / year "anchor + period" time model shared by the
/// dashboard, project Overview, and Browse. Centralises the range math, bound-clamping,
/// and the today-marker position that used to be copy-pasted between those pages.
///
/// An anchor is a (year, month) reference point. The period reinterprets it:
///   "month" → just the anchor month, "ytd" → Jan..anchorMonth, "year" → Jan..Dec.
///
/// Navigation is bounded by an inclusive [min, max] month window derived from the data
/// (earliest..latest transaction), NOT by "now": the past bound stops you scrolling into
/// empty months before the first transaction, and the future bound lets you reach
/// future-dated rows (e.g. MoneyMoney's pending transactions) that sit beyond today.
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

    /// <summary>Month-granularity ordinal for comparing (year, month) points.</summary>
    private static int Ordinal((int Year, int Month) m) => m.Year * 12 + (m.Month - 1);

    /// <summary>Clamp an anchor into the inclusive [min, max] month window.</summary>
    public static (int Year, int Month) Clamp((int Year, int Month) anchor, (int Year, int Month) min, (int Year, int Month) max)
    {
        if (Ordinal(anchor) < Ordinal(min)) return min;
        if (Ordinal(anchor) > Ordinal(max)) return max;
        return anchor;
    }

    /// <summary>Step the anchor one period (a month, or a year in "year" mode), clamped to [min, max].</summary>
    public static (int Year, int Month) Step((int Year, int Month) anchor, string period, int direction, (int Year, int Month) min, (int Year, int Month) max)
    {
        var dt = new DateTime(anchor.Year, anchor.Month, 1);
        dt = period == "year" ? dt.AddYears(direction) : dt.AddMonths(direction);
        return Clamp((dt.Year, dt.Month), min, max);
    }

    /// <summary>True when stepping forward would pass the max bound — used to disable the "next" button.</summary>
    public static bool IsAtMax((int Year, int Month) anchor, string period, (int Year, int Month) max)
        => period == "year"
            ? anchor.Year >= max.Year
            : Ordinal(anchor) >= Ordinal(max);

    /// <summary>True when stepping back would pass the min bound — used to disable the "prev" button.</summary>
    public static bool IsAtMin((int Year, int Month) anchor, string period, (int Year, int Month) min)
        => period == "year"
            ? anchor.Year <= min.Year
            : Ordinal(anchor) <= Ordinal(min);

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
