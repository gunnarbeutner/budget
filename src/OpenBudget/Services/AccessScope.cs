using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>
/// Thrown when a write is refused because the current user lacks the required role. Reads outside a
/// user's scope never throw — they simply return nothing ("not found"), so existence isn't revealed.
/// </summary>
public sealed class AccessDeniedException : Exception
{
    public AccessDeniedException()
    {
    }

    public AccessDeniedException(string message) : base(message)
    {
    }

    public AccessDeniedException(string message, Exception innerException) : base(message, innerException)
    {
    }
}

/// <summary>
/// Immutable snapshot of what one user may see and do, computed by <see cref="AccessScopeProvider"/>.
/// It is attached to every <see cref="BudgetDbContext"/> the app hands to services (see
/// <see cref="ScopedDbContextFactory"/>), where it drives global query filters (reads) and the
/// SaveChanges guard (writes) — so enforcement is central rather than per page or per service.
///
/// Visibility rules:
/// <list type="bullet">
/// <item>Sources: owned ones, plus the sources of any visible project (needed for category names).</item>
/// <item>Projects: owned, shared with the user, and children of those (shares are inherited).</item>
/// <item>Accounts/transactions/holdings: everything in owned sources, plus ONLY the accounts assigned
/// to projects shared with the user.</item>
/// </list>
/// An unowned source/project (not yet claimed through the CLI) is visible to nobody.
/// </summary>
public sealed class AccessScope
{
    /// <summary>The scope of a logged-out (or unknown/removed) user: sees nothing.</summary>
    public static AccessScope Anonymous { get; } = new(
        userId: null, isAdmin: false,
        new Dictionary<int, ProjectRole>(), ownedSourceIds: [], visibleSourceIds: [], sharedAccountIds: []);

    private readonly IReadOnlyDictionary<int, ProjectRole> _roles;

    public AccessScope(
        string? userId,
        bool isAdmin,
        IReadOnlyDictionary<int, ProjectRole> projectRoles,
        int[] ownedSourceIds,
        int[] visibleSourceIds,
        long[] sharedAccountIds)
    {
        UserId = userId;
        IsAdmin = isAdmin;
        _roles = projectRoles;
        OwnedSourceIds = ownedSourceIds;
        VisibleSourceIds = visibleSourceIds;
        SharedAccountIds = sharedAccountIds;
        VisibleProjectIds = projectRoles.Keys.ToArray();
        OwnedProjectIds = projectRoles.Where(kv => kv.Value == ProjectRole.Owner).Select(kv => kv.Key).ToArray();
    }

    public string? UserId { get; }
    public bool IsAdmin { get; }
    public bool IsAuthenticated => UserId != null;

    /// <summary>Sources the user owns (their accounts/transactions are fully visible to them).</summary>
    public int[] OwnedSourceIds { get; }

    /// <summary>Owned sources plus the sources behind every visible project (categories etc.).</summary>
    public int[] VisibleSourceIds { get; }

    /// <summary>Accounts assigned to projects that are shared with (not owned by) the user.</summary>
    public long[] SharedAccountIds { get; }

    public int[] VisibleProjectIds { get; }
    public int[] OwnedProjectIds { get; }

    public ProjectRole RoleFor(int projectId) =>
        _roles.TryGetValue(projectId, out var role) ? role : ProjectRole.None;

    public bool CanView(int projectId) => RoleFor(projectId) >= ProjectRole.Viewer;
    public bool CanEdit(int projectId) => RoleFor(projectId) >= ProjectRole.Editor;
    public bool IsOwner(int projectId) => RoleFor(projectId) == ProjectRole.Owner;

    public bool OwnsSource(int sourceId) => Array.IndexOf(OwnedSourceIds, sourceId) >= 0;
    public bool CanSeeSource(int sourceId) => Array.IndexOf(VisibleSourceIds, sourceId) >= 0;

    /// <summary>
    /// Whether a change notification concerns data this scope can see. Notifications only carry
    /// ids (never data), but components still ignore events outside their user's scope.
    /// </summary>
    public bool IsChangeVisible(DataChange change)
    {
        if (change.UserId != null && !string.Equals(change.UserId, UserId, StringComparison.Ordinal)) return false;
        if (change.ProjectId is { } projectId && !_roles.ContainsKey(projectId)) return false;
        if (change.SourceId is { } sourceId && !CanSeeSource(sourceId)) return false;
        return true;
    }

    public void RequireRole(int projectId, ProjectRole required)
    {
        if (RoleFor(projectId) < required)
            throw new AccessDeniedException($"Requires {required} access to project {projectId}.");
    }

    public void RequireSourceOwner(int sourceId)
    {
        if (!OwnsSource(sourceId))
            throw new AccessDeniedException($"Only the owner may change source {sourceId}.");
    }
}
