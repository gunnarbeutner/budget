namespace OpenBudget.Models;

/// <summary>
/// Output of <c>BudgetService.GetProjectActualDailyDeltaAsync</c>: the starting balance
/// (real-world account balance at year start) and a per-day delta array of scope-filtered
/// transaction sums. The caller layers projection deltas on top and cumulates to produce a
/// balance line.
/// </summary>
public class ProjectDailyDeltaResult
{
    public decimal StartingBalance { get; set; }
    /// <summary>Length = days in year. Index 0 = Jan 1.</summary>
    public decimal[] DailyDelta { get; set; } = Array.Empty<decimal>();
    /// <summary>1-indexed day of year up to which DailyDelta is populated from real transactions
    /// (subsequent days are zero pending projection). Zero when the year is entirely in the future.</summary>
    public int LastActualDayOfYear { get; set; }
    public bool HasAccounts { get; set; }
}
