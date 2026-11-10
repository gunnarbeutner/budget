namespace OpenBudget.Shared;

/// <summary>
/// The project views listed at the top of the project sidebar (<see cref="ProjectShell"/>), used to highlight
/// the active one. Editing or creating a budget belongs to <see cref="Budgets"/>. <see cref="Overview"/> is
/// on its way out: it has no sidebar entry and is only reachable by its URL.
/// </summary>
public enum ProjectTab
{
    Overview,
    Browse,
    Budgets,
    Cashflow,
    Rebalance,
}
