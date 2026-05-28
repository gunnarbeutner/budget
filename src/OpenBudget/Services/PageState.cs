using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;

namespace OpenBudget.Services;

/// <summary>
/// URL-state primitives shared by the project-scoped pages for the view-state query params
/// (year, month, budgetId, …) that survive alongside the path. Centralises the reads and the
/// "don't 302-loop on identical URL" guard that ServerPrerendered would otherwise hit.
/// </summary>
public static class PageState
{
    /// <summary>Parse a single int query parameter; null if absent or unparseable.</summary>
    public static int? ReadIntQuery(NavigationManager nav, string key)
    {
        var query = QueryHelpers.ParseQuery(nav.ToAbsoluteUri(nav.Uri).Query);
        if (query.TryGetValue(key, out var value) && int.TryParse(value, out var parsed))
            return parsed;
        return null;
    }

    /// <summary>
    /// Replace the current URL with one carrying the given query parameters (replace, not push,
    /// so the back button doesn't accumulate every filter tweak). Skips the navigation when the
    /// new URL matches the current one — a render during ServerPrerendered would otherwise
    /// emit a 302 to itself and loop.
    /// </summary>
    public static void ReplaceQuery(NavigationManager nav, IReadOnlyDictionary<string, object?> parameters)
    {
        var uri = nav.GetUriWithQueryParameters(new Dictionary<string, object?>(parameters));
        if (uri == nav.Uri) return;
        nav.NavigateTo(uri, forceLoad: false, replace: true);
    }
}
