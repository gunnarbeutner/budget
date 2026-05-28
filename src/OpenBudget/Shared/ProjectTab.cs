namespace OpenBudget.Shared;

/// <summary>
/// Which primary sub-page of a project the user is currently viewing. Drives the
/// active-tab highlight in <see cref="ProjectPageHeader"/>. Editing or wizard-running
/// a budget keeps the <see cref="Budgets"/> tab active — those are sub-flows of the
/// Budgets section.
///
/// The settings/overview page (<c>/projects/{id}</c>) is intentionally NOT a primary
/// tab; it's reached via the small gear icon at the end of the strip. Settings are
/// infrequently visited compared to the data views.
/// </summary>
public enum ProjectTab
{
    Browse,
    Budgets,
    Cashflow,
}
