namespace OpenBudget.Services;

/// <summary>A period of the Portfolio view's value-history chart.</summary>
public enum PortfolioValuePeriod { OneDay, OneWeek, OneMonth, YearToDate, OneYear, ThreeYears, Max }

/// <summary>One day of the Portfolio view's value series.</summary>
/// <param name="Value">The depots' total value at the end of the day.</param>
/// <param name="Flow">Net money moved into the depots on the day (negative when money left them).</param>
/// <param name="Invested">The start value plus every flow of the period up to and including the day.</param>
public sealed record PortfolioValuePoint(DateTime Day, decimal Value, decimal Flow, decimal Invested);

/// <summary>
/// The total value of a project's depots over time, the series behind the Portfolio view's value chart, split into
/// the money moved into or out of the depots and the gain on top of it.
/// </summary>
public sealed record PortfolioValueSeries(IReadOnlyList<PortfolioValuePoint> Points)
{
    public decimal StartValue => Points.Count > 0 ? Points[0].Value : 0m;
    public decimal EndValue => Points.Count > 0 ? Points[^1].Value : 0m;
    /// <summary>How much the value changed over the period.</summary>
    public decimal Change => EndValue - StartValue;
    /// <summary>Net money moved into the depots after the first day (the first day's flows are in the start value).</summary>
    public decimal NetFlows => Points.Skip(1).Sum(p => p.Flow);
    /// <summary>The part of the change not explained by money moved in or out.</summary>
    public decimal Gain => Change - NetFlows;

    /// <summary>
    /// The time-weighted return: each day's return (its value less its flows, over the previous day's value) chained
    /// over the period, so money moved in or out does not count as performance. A day following a zero value measures
    /// its value against its flows instead; with neither it adds no return. Null when no day has anything to compare
    /// against.
    /// </summary>
    public decimal? TimeWeightedReturn
    {
        get
        {
            decimal growth = 1m;
            var any = false;
            for (int i = 1; i < Points.Count; i++)
            {
                var (previous, p) = (Points[i - 1].Value, Points[i]);
                if (previous != 0m) growth *= (p.Value - p.Flow) / previous;
                else if (p.Flow != 0m) growth *= p.Value / p.Flow;
                else continue;
                any = true;
            }
            return any ? growth - 1m : null;
        }
    }
}

/// <summary>
/// Sums the depots' stored daily value histories (<c>portfolio_chart</c>) into one daily series. Each depot's value
/// on a day is its last stored point on or before that day (0 before its first point); today is the depots' current
/// total value, so a depot without stored history contributes only from today.
/// </summary>
public static class PortfolioValueHistory
{
    /// <summary>
    /// The day the period is measured from (its first point), or null for the whole history. A day compares against
    /// <paramref name="lastStoredDay"/>, the last day before today with a stored value, or yesterday without one; the
    /// year to date against the last day of the previous year.
    /// </summary>
    public static DateTime? PeriodStart(PortfolioValuePeriod period, DateTime today, DateTime? lastStoredDay = null)
    {
        today = today.Date;
        return period switch
        {
            PortfolioValuePeriod.OneDay => lastStoredDay is { } d && d.Date < today ? d.Date : today.AddDays(-1),
            PortfolioValuePeriod.OneWeek => today.AddDays(-7),
            PortfolioValuePeriod.OneMonth => today.AddMonths(-1),
            PortfolioValuePeriod.YearToDate => new DateTime(today.Year, 1, 1).AddDays(-1),
            PortfolioValuePeriod.OneYear => today.AddYears(-1),
            PortfolioValuePeriod.ThreeYears => today.AddYears(-3),
            _ => null,
        };
    }

    /// <summary>The last day before <paramref name="today"/> on which any depot has a stored value.</summary>
    public static DateTime? LastStoredDayBefore(IReadOnlyDictionary<long, List<(DateTime Day, decimal Value)>> history, DateTime today) =>
        history.Values.SelectMany(p => p).Select(p => p.Day.Date).Where(d => d < today.Date).Select(d => (DateTime?)d).Max();

    /// <summary>
    /// One point per day from the later of <paramref name="from"/> and the earliest stored point through
    /// <paramref name="today"/>. Only a today point when no depot has history before today. Flows dated before the
    /// first day are left out; flows after today count on today.
    /// </summary>
    public static PortfolioValueSeries Build(
        IReadOnlyDictionary<long, List<(DateTime Day, decimal Value)>> history,
        decimal currentTotal, DateTime today, DateTime? from,
        IReadOnlyDictionary<DateTime, decimal>? flows = null)
    {
        today = today.Date;
        var depots = history.Values.Where(p => p.Count > 0).Select(p => p.OrderBy(x => x.Day).ToList()).ToList();
        var earliest = depots.Count > 0 ? depots.Min(p => p[0].Day.Date) : today;
        var start = from is { } f && f.Date > earliest ? f.Date : earliest;
        if (start > today) start = today;

        var flowByDay = new Dictionary<DateTime, decimal>();
        foreach (var (day, amount) in flows ?? new Dictionary<DateTime, decimal>())
        {
            var d = day.Date > today ? today : day.Date;
            if (d >= start) flowByDay[d] = flowByDay.GetValueOrDefault(d) + amount;
        }

        var values = new List<(DateTime Day, decimal Value)>();
        var index = new int[depots.Count];
        var value = new decimal[depots.Count];
        for (var day = start; day < today; day = day.AddDays(1))
        {
            decimal sum = 0m;
            for (int i = 0; i < depots.Count; i++)
            {
                var p = depots[i];
                while (index[i] < p.Count && p[index[i]].Day.Date <= day) value[i] = p[index[i]++].Value;
                sum += value[i];
            }
            values.Add((day, sum));
        }
        values.Add((today, currentTotal));

        var points = new List<PortfolioValuePoint>(values.Count);
        decimal invested = values[0].Value;
        for (int i = 0; i < values.Count; i++)
        {
            var flow = flowByDay.GetValueOrDefault(values[i].Day);
            if (i > 0) invested += flow;
            points.Add(new PortfolioValuePoint(values[i].Day, values[i].Value, flow, invested));
        }
        return new PortfolioValueSeries(points);
    }
}
