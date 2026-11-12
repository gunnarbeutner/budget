namespace OpenBudget.Connectors;

/// <summary>
/// The execution dates of a standing order's rhythm: every <c>interval</c> months on a day of the month, or every
/// <c>interval</c> weeks on a weekday, counted from the first execution and ending at the last one. Dates are the
/// nominal ones; a bank moves an execution on a weekend or holiday to the next business day, which this ignores.
/// </summary>
public static class PaymentSchedule
{
    /// <summary>Day-of-month codes for "ultimo minus two", "ultimo minus one" and "ultimo".</summary>
    public const int UltimoMinus2 = 97, UltimoMinus1 = 98, Ultimo = 99;

    /// <summary>
    /// The executions in <paramref name="from"/>..<paramref name="to"/> (both inclusive). Without a usable rhythm
    /// (no unit or an interval below one) only <paramref name="first"/> counts, as for a single transfer.
    /// </summary>
    public static IEnumerable<DateOnly> Occurrences(
        DateOnly first, ScheduleUnit? unit, int? interval, int? day, DateOnly? last, DateOnly from, DateOnly to)
    {
        var end = last is { } l && l < to ? l : to;
        if (unit == null || interval is not > 0)
        {
            if (first >= from && first <= end) yield return first;
            yield break;
        }

        if (unit == ScheduleUnit.Week)
        {
            // The first execution's week fixes the cycle; the weekday (1 = Monday … 7 = Sunday) picks the day in it.
            var weekday = day is >= 1 and <= 7 ? day.Value : IsoWeekday(first);
            var weekStart = first.AddDays(1 - IsoWeekday(first));
            var step = 7 * interval.Value;
            if (from > weekStart)
            {
                var skip = (from.DayNumber - weekStart.DayNumber) / step;
                weekStart = weekStart.AddDays(skip * step);
            }
            for (; weekStart <= end; weekStart = weekStart.AddDays(step))
            {
                var date = weekStart.AddDays(weekday - 1);
                if (date >= first && date >= from && date <= end) yield return date;
            }
            yield break;
        }

        var dayOfMonth = day is >= 1 and <= 31 or >= UltimoMinus2 and <= Ultimo ? day.Value : first.Day;
        var month = new DateOnly(first.Year, first.Month, 1);
        if (from > month)
        {
            var months = (from.Year - month.Year) * 12 + from.Month - month.Month;
            month = month.AddMonths(Math.Max(0, months / interval.Value * interval.Value - interval.Value));
        }
        for (; month <= end; month = month.AddMonths(interval.Value))
        {
            var date = InMonth(month, dayOfMonth);
            if (date >= first && date >= from && date <= end) yield return date;
        }
    }

    /// <summary>The first execution on or after <paramref name="from"/>; null when the order has ended before it.</summary>
    public static DateOnly? Next(DateOnly first, ScheduleUnit? unit, int? interval, int? day, DateOnly? last, DateOnly from) =>
        Occurrences(first, unit, interval, day, last, from, from.AddYears(3)).Select(d => (DateOnly?)d).FirstOrDefault();

    private static DateOnly InMonth(DateOnly month, int day)
    {
        var length = DateTime.DaysInMonth(month.Year, month.Month);
        var d = day switch
        {
            Ultimo => length,
            UltimoMinus1 => length - 1,
            UltimoMinus2 => length - 2,
            _ => Math.Min(day, length),
        };
        return new DateOnly(month.Year, month.Month, d);
    }

    private static int IsoWeekday(DateOnly date) => date.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)date.DayOfWeek;
}
