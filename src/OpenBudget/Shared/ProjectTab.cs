namespace OpenBudget.Shared;

/// <summary>
/// Which primary sub-page of a project the user is currently viewing. Drives the
/// active-tab highlight in <see cref="ProjectPageHeader"/>. Editing or wizard-running
/// a budget keeps the <see cref="Budgets"/> tab active — those are sub-flows of the
/// Budgets section.
///
/// <see cref="Overview"/> is the project's landing tab: the at-a-glance summary
/// (balance, budget-vs-actual, category breakdown) that used to live inside each
/// dashboard card. Clicking a project from the dashboard lands here.
///
/// The settings page (<c>/projects/{id}</c>) is intentionally NOT a primary tab;
/// it's reached via the small gear icon at the end of the strip. Settings are
/// infrequently visited compared to the data views.
/// </summary>
public enum ProjectTab
{
    Overview,
    Browse,
    Budgets,
    Cashflow,
    Rebalance,
}
