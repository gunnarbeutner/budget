using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;

namespace OpenBudget.Services;

/// <summary>
/// Per-circuit memory of the period the project views show (see <see cref="ViewPeriod"/>), shared by Transactions,
/// Budget and Cashflow: switching between them keeps it. A view seeds its period from here, overlays what its URL
/// carries (links stay authoritative), and writes every change back; the sidebar's view links carry it in their
/// query so the next view's URL names it too.
///
/// Registered scoped — in Blazor Server that's the lifetime of the user's circuit, so it persists across in-app
/// navigations but never leaks between users.
/// </summary>
public class ProjectTimeState
{
    public ViewPeriod? Period { get; set; }

    /// <summary>The remembered period, or the current month.</summary>
    public ViewPeriod Current(DateTime nowLocal) => Period ?? new ViewPeriod(nowLocal.Year, nowLocal.Month, "month");

    /// <summary>The remembered period overlaid with the one in the current URL; remembered as well.</summary>
    public ViewPeriod ReadFromUrl(NavigationManager nav, DateTime nowLocal)
    {
        var query = QueryHelpers.ParseQuery(nav.ToAbsoluteUri(nav.Uri).Query);
        Period = ViewPeriod.Read(query, Current(nowLocal), DateOnly.FromDateTime(nowLocal));
        return Period;
    }
}
