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

    /// <summary>
    /// End-of-day balances of a set of accounts (an account group): the sum of their running balances, which is the
    /// running balance of their summed current balances over all their flows.
    /// </summary>
    public static RunningBalance ForAccounts(
        IEnumerable<(decimal CurrentBalance, IEnumerable<(DateTime Date, decimal Amount)> Flows)> accounts, Func<DateTime, DateOnly> toLocalDay)
    {
        var list = accounts.ToList();
        return new RunningBalance(list.Sum(a => a.CurrentBalance), list.SelectMany(a => a.Flows), toLocalDay);
    }

    /// <summary>
    /// Whether end-of-day balances mean anything for the list: only for a selected account or account group whose list
    /// is not narrowed by a filter or search (otherwise the bookings between two day rows are not all its bookings).
    /// </summary>
    public static bool Applies(bool accountScope, bool hasBalance, bool filtered) => accountScope && hasBalance && !filtered;

    /// <summary>The balance after all bookings of <paramref name="day"/>: the current balance minus everything booked on later days.</summary>
    public decimal AtEndOf(DateOnly day)
    {
        var i = Array.BinarySearch(_days, day);
        var firstLater = i >= 0 ? i + 1 : ~i;
        return firstLater < _days.Length ? _current - _fromIndexOn[firstLater] : _current;
    }
}
