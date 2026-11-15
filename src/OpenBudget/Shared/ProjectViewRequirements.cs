namespace OpenBudget.Shared;

/// <summary>Counts of the accounts assigned to a project, by kind and whether they are closed.</summary>
public sealed record ProjectAccountSummary(int OpenDepots, int ClosedDepots, int OpenAccounts, int ClosedAccounts)
{
    public static readonly ProjectAccountSummary Empty = new(0, 0, 0, 0);
}

/// <summary>
/// What a project view needs among the project's accounts to be worth listing in the project's navigation
/// (<see cref="ProjectShell"/>). A view without an entry is always listed. A hidden view stays reachable by URL,
/// and the active view is always listed so the user sees where they are.
/// </summary>
public static class ProjectViewRequirements
{
    private static readonly Dictionary<ProjectTab, Func<ProjectAccountSummary, bool>> Requirements = new()
    {
        // Positions and value history come from open depots; a closed one has nothing current to show.
        [ProjectTab.Portfolio] = s => s.OpenDepots > 0,
    };

    /// <summary>Whether the project's accounts meet <paramref name="tab"/>'s requirement.</summary>
    public static bool IsAvailable(ProjectTab tab, ProjectAccountSummary summary) =>
        !Requirements.TryGetValue(tab, out var requirement) || requirement(summary);

    /// <summary>
    /// Whether the navigation lists <paramref name="tab"/>: the active view always, otherwise when its requirement is met.
    /// While the summary is still loading (null), views with a requirement stay hidden.
    /// </summary>
    public static bool Shows(ProjectTab tab, ProjectAccountSummary? summary, ProjectTab? active) =>
        tab == active || (summary is null ? !Requirements.ContainsKey(tab) : IsAvailable(tab, summary));
}
