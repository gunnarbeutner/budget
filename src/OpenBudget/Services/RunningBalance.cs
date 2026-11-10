namespace OpenBudget.Services;

/// <summary>
/// The balance of an account at the end of each day, derived backwards from its current balance and its booked flows.
/// Pure: the caller supplies the flows (in any order) and how a UTC instant maps to the user's calendar day.
/// </summary>
public sealed class RunningBalance
{
    private readonly DateOnly[] _days;        // distinct days with flows, ascending
    private readonly decimal[] _fromIndexOn;  // _fromIndexOn[i] = sum of the flows on days _days[i..]
    private readonly decimal _current;

    public RunningBalance(decimal currentBalance, IEnumerable<(DateTime Date, decimal Amount)> flows, Func<DateTime, DateOnly> toLocalDay)
    {
        _current = currentBalance;
        var perDay = flows.GroupBy(f => toLocalDay(f.Date)).OrderBy(g => g.Key).Select(g => (Day: g.Key, Sum: g.Sum(f => f.Amount))).ToList();
        _days = perDay.Select(d => d.Day).ToArray();
        _fromIndexOn = new decimal[_days.Length];
        decimal running = 0;
        for (var i = _days.Length - 1; i >= 0; i--)
        {
            running += perDay[i].Sum;
            _fromIndexOn[i] = running;
        }
    }

    /// <summary>The balance after all bookings of <paramref name="day"/>: the current balance minus everything booked on later days.</summary>
    public decimal AtEndOf(DateOnly day)
    {
        var i = Array.BinarySearch(_days, day);
        var firstLater = i >= 0 ? i + 1 : ~i;
        return firstLater < _days.Length ? _current - _fromIndexOn[firstLater] : _current;
    }
}
