namespace OpenBudget.Services;

/// <summary>
/// Per-circuit memory of the month-model time selection — the (year, month) anchor and the
/// month / YTD / year display mode — shared by a project's Overview and Browse tabs.
///
/// Budgets and Cashflow are year-only and don't carry this selection, so detouring through
/// them used to reset the display mode on the way back. Overview/Browse seed their state from
/// here when the URL doesn't specify one, and write back on every change, so the mode survives
/// the round trip. The URL still wins when it carries an explicit selection (shareable links,
/// dashboard links that target a specific month), keeping links authoritative.
///
/// Registered scoped — in Blazor Server that's the lifetime of the user's circuit, so it
/// persists across in-app navigations but never leaks between users.
/// </summary>
public class ProjectTimeState
{
    public int? Year { get; set; }
    public int? Month { get; set; }
    public string? Period { get; set; }
}
