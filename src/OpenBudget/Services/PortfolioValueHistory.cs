namespace OpenBudget.Services;

/// <summary>A period of the Portfolio view's value-history chart.</summary>
public enum PortfolioValuePeriod { OneYear, ThreeYears, FiveYears, Max }

/// <summary>
/// The total value of a project's depots over time, the series behind the Portfolio view's value chart.
/// </summary>
public sealed record PortfolioValueSeries(IReadOnlyList<(DateTime Day, decimal Value)> Points)
{
    public decimal StartValue => Points.Count > 0 ? Points[0].Value : 0m;
    public decimal EndValue => Points.Count > 0 ? Points[^1].Value : 0m;
    public decimal Change => EndValue - StartValue;
    /// <summary>The change as a fraction of the start value; null when the period starts at zero.</summary>
    public decimal? ChangeFraction => StartValue != 0m ? Change / StartValue : null;
}

/// <summary>
/// Sums the depots' stored daily value histories (<c>portfolio_chart</c>) into one daily series. Each depot's value
/// on a day is its last stored point on or before that day (0 before its first point); today is the depots' current
/// total value, so a depot without stored history contributes only from today.
/// </summary>
public static class PortfolioValueHistory
{
    /// <summary>The first day the period shows, or null for the whole history.</summary>
    public static DateTime? PeriodStart(PortfolioValuePeriod period, DateTime today) => period switch
    {
        PortfolioValuePeriod.OneYear => today.Date.AddYears(-1),
        PortfolioValuePeriod.ThreeYears => today.Date.AddYears(-3),
        PortfolioValuePeriod.FiveYears => today.Date.AddYears(-5),
        PortfolioValuePeriod.Max => null,
        _ => null,
    };

    /// <summary>
    /// One point per day from the later of <paramref name="from"/> and the earliest stored point through
    /// <paramref name="today"/>. Only a today point when no depot has history before today.
    /// </summary>
    public static PortfolioValueSeries Build(
        IReadOnlyDictionary<long, List<(DateTime Day, decimal Value)>> history,
        decimal currentTotal, DateTime today, DateTime? from)
    {
        today = today.Date;
        var depots = history.Values.Where(p => p.Count > 0).Select(p => p.OrderBy(x => x.Day).ToList()).ToList();
        var earliest = depots.Count > 0 ? depots.Min(p => p[0].Day.Date) : today;
        var start = from is { } f && f.Date > earliest ? f.Date : earliest;
        if (start > today) start = today;

        var points = new List<(DateTime, decimal)>();
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
            points.Add((day, sum));
        }
        points.Add((today, currentTotal));
        return new PortfolioValueSeries(points);
    }
}
