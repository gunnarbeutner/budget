namespace OpenBudget.Shared;

/// <summary>
/// The project views listed at the top of the project sidebar (<see cref="ProjectShell"/>), used to highlight
/// the active one. Editing or creating a budget belongs to <see cref="Budgets"/>.
/// </summary>
public enum ProjectTab
{
    Browse,
    Budgets,
    Cashflow,
    Portfolio,
}
