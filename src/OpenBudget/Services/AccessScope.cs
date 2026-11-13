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
/// <item>Projects: owned, shared with the user, and children of those (shares are inherited).</item>
/// <item>Categories and their groups: the user's own and those of the owner of any visible project.</item>
/// <item>Accounts/transactions/holdings: everything the user owns, plus ONLY the accounts assigned
/// to projects shared with the user.</item>
/// </list>
/// An unowned project is visible to nobody.
/// </summary>
public sealed class AccessScope
{
    /// <summary>The scope of a logged-out (or unknown/removed) user: sees nothing.</summary>
    public static AccessScope Anonymous { get; } = new(
        userId: null, isAdmin: false,
        new Dictionary<int, ProjectRole>(), visibleOwnerIds: [], sharedAccountIds: []);

    private readonly IReadOnlyDictionary<int, ProjectRole> _roles;

    public AccessScope(
        string? userId,
        bool isAdmin,
        IReadOnlyDictionary<int, ProjectRole> projectRoles,
        string[] visibleOwnerIds,
        long[] sharedAccountIds,
        long[]? editableSharedAccountIds = null)
    {
        UserId = userId;
        IsAdmin = isAdmin;
        _roles = projectRoles;
        VisibleOwnerIds = visibleOwnerIds;
        SharedAccountIds = sharedAccountIds;
        EditableSharedAccountIds = editableSharedAccountIds ?? [];
        VisibleProjectIds = projectRoles.Keys.ToArray();
        OwnedProjectIds = projectRoles.Where(kv => kv.Value == ProjectRole.Owner).Select(kv => kv.Key).ToArray();
    }

    public string? UserId { get; }
    public bool IsAdmin { get; }
    public bool IsAuthenticated => UserId != null;

    /// <summary>The user plus the owners of every visible project (whose categories etc. the user reads).</summary>
    public string[] VisibleOwnerIds { get; }

    /// <summary>Accounts assigned to projects that are shared with (not owned by) the user.</summary>
    public long[] SharedAccountIds { get; }

    /// <summary>The part of <see cref="SharedAccountIds"/> that belongs to projects shared with the user as editor.</summary>
    public long[] EditableSharedAccountIds { get; }

    public int[] VisibleProjectIds { get; }
    public int[] OwnedProjectIds { get; }

    public ProjectRole RoleFor(int projectId) =>
        _roles.TryGetValue(projectId, out var role) ? role : ProjectRole.None;

    public bool CanView(int projectId) => RoleFor(projectId) >= ProjectRole.Viewer;
    public bool CanEdit(int projectId) => RoleFor(projectId) >= ProjectRole.Editor;
    public bool IsOwner(int projectId) => RoleFor(projectId) == ProjectRole.Owner;

    /// <summary>Whether <paramref name="ownerId"/> is the signed-in user.</summary>
    public bool Owns(string? ownerId) => IsAuthenticated && string.Equals(ownerId, UserId, StringComparison.Ordinal);
    public bool CanSeeOwner(string ownerId) => Array.IndexOf(VisibleOwnerIds, ownerId) >= 0;

    /// <summary>
    /// Whether a change notification concerns data this scope can see. Notifications only carry
    /// ids (never data), but components still ignore events outside their user's scope.
    /// </summary>
    public bool IsChangeVisible(DataChange change)
    {
        if (change.UserId != null && !string.Equals(change.UserId, UserId, StringComparison.Ordinal)) return false;
        if (change.ProjectId is { } projectId && !_roles.ContainsKey(projectId)) return false;
        if (change.OwnerUserId is { } ownerId && !CanSeeOwner(ownerId)) return false;
        return true;
    }

    /// <summary>
    /// Whether the user may categorize / annotate / check the transactions of an account: its owner, or an editor of a project the account is assigned to. Viewers never. (Whether the account's
    /// provider allows editing at all is a separate rule, see <see cref="TransactionEditRules"/>.)
    /// </summary>
    public bool CanEditTransactionsOf(string ownerId, long? accountId) =>
        IsAuthenticated && (Owns(ownerId) || (accountId is { } a && Array.IndexOf(EditableSharedAccountIds, a) >= 0));

    public void RequireRole(int projectId, ProjectRole required)
    {
        if (RoleFor(projectId) < required)
            throw new AccessDeniedException($"Requires {required} access to project {projectId}.");
    }

    public void RequireOwner(string? ownerId)
    {
        if (!Owns(ownerId))
            throw new AccessDeniedException("Only the owner may change this data.");
    }
}
