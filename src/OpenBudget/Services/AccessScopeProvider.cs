using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;
using System.Security.Claims;

namespace OpenBudget.Services;

/// <summary>Resolves the id of the user the current request/circuit acts for.</summary>
public interface ICurrentUser
{
    /// <summary>The AppUser.Id of the authenticated user, or null when logged out.</summary>
    ValueTask<string?> GetUserIdAsync();
}

/// <summary>
/// <see cref="ICurrentUser"/> backed by the Blazor <see cref="AuthenticationStateProvider"/>: the
/// cookie principal while prerendering, and the circuit's (periodically revalidated) auth state
/// afterwards.
/// </summary>
public sealed class AuthStateCurrentUser : ICurrentUser
{
    private readonly AuthenticationStateProvider _auth;

    public AuthStateCurrentUser(AuthenticationStateProvider auth) => _auth = auth;

    public async ValueTask<string?> GetUserIdAsync()
    {
        try
        {
            var state = await _auth.GetAuthenticationStateAsync();
            return state.User.Identity?.IsAuthenticated == true
                ? state.User.FindFirstValue(ClaimTypes.NameIdentifier)
                : null;
        }
        catch (InvalidOperationException)
        {
            // Auth state not initialised (no circuit / no HttpContext): treat as logged out.
            return null;
        }
    }
}

/// <summary>
/// Computes (and caches, per circuit/request) the <see cref="AccessScope"/> of the current user.
/// The cache is dropped whenever a Projects/Sources change is published, so shares, assignments
/// and ownership changes take effect immediately. The user's existence is re-verified on every
/// recompute, so a removed user loses all access at once even if their cookie is still valid.
/// </summary>
public sealed class AccessScopeProvider : IDisposable
{
    private readonly SystemDbContextFactory _system;
    private readonly ICurrentUser _currentUser;
    private readonly ChangeNotificationService _notifier;
    // A cached scope is also dropped after this long, which bounds staleness for changes made by
    // other processes (CLI claims, user removal) that publish no in-process notification.
    private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(30);

    private Task<AccessScope>? _cached;
    private long _cachedAtMs;
    private volatile AccessScope? _last;

    public AccessScopeProvider(SystemDbContextFactory system, ICurrentUser currentUser, ChangeNotificationService notifier)
    {
        _system = system;
        _currentUser = currentUser;
        _notifier = notifier;
        // Subscribed before any component of the circuit can subscribe, so by the time a component
        // handles a change this provider has already dropped its stale snapshot.
        _notifier.Changed += OnChanged;
    }

    /// <summary>The most recently computed scope (anonymous until first computed).</summary>
    public AccessScope Last => _last ?? AccessScope.Anonymous;

    public async Task<AccessScope> GetAsync()
    {
        var task = _cached;
        if (task == null || Environment.TickCount64 - Interlocked.Read(ref _cachedAtMs) > MaxAge.TotalMilliseconds)
        {
            task = ComputeCoreAsync();
            _cached = task;
            Interlocked.Exchange(ref _cachedAtMs, Environment.TickCount64);
        }

        try
        {
            return await task;
        }
        catch
        {
            if (ReferenceEquals(_cached, task)) _cached = null;
            throw;
        }
    }

    public void Invalidate() => _cached = null;

    private void OnChanged(DataChange change, Guid? origin)
    {
        if (change.Area is DataArea.Projects or DataArea.Sources) _cached = null;
    }

    private async Task<AccessScope> ComputeCoreAsync()
    {
        var userId = await _currentUser.GetUserIdAsync();
        var scope = await ComputeAsync(_system, userId);
        _last = scope;
        return scope;
    }

    public void Dispose() => _notifier.Changed -= OnChanged;

    /// <summary>Computes the scope for <paramref name="userId"/> (anonymous when null/unknown).</summary>
    public static async Task<AccessScope> ComputeAsync(IDbContextFactory<BudgetDbContext> system, string? userId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(userId)) return AccessScope.Anonymous;

        await using var db = await system.CreateDbContextAsync(ct);
        var user = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.IsAdmin })
            .FirstOrDefaultAsync(ct);
        if (user == null) return AccessScope.Anonymous;

        var ownedSources = await db.ImportSources.AsNoTracking()
            .Where(s => s.OwnerUserId == userId)
            .Select(s => s.Id)
            .ToArrayAsync(ct);
        var projects = await db.Projects.AsNoTracking()
            .Select(p => new { p.Id, p.OwnerUserId, p.ParentProjectId, p.SourceId })
            .ToListAsync(ct);
        var shares = await db.ProjectShares.AsNoTracking()
            .Where(s => s.UserId == userId)
            .ToDictionaryAsync(s => s.ProjectId, s => s.Role, ct);

        // Own role first (owner, else an explicit share), then inheritance from the parent: a
        // sub-project is visible with at least its parent's role. Hierarchies are single-level.
        var direct = new Dictionary<int, ProjectRole>();
        foreach (var p in projects)
        {
            if (p.OwnerUserId == userId) direct[p.Id] = ProjectRole.Owner;
            else if (shares.TryGetValue(p.Id, out var r)) direct[p.Id] = r;
        }

        var roles = new Dictionary<int, ProjectRole>(direct);
        foreach (var p in projects)
        {
            if (p.ParentProjectId is not { } parentId || !direct.TryGetValue(parentId, out var inherited)) continue;
            if (!roles.TryGetValue(p.Id, out var own) || inherited > own) roles[p.Id] = inherited;
        }

        var sourceById = projects.ToDictionary(p => p.Id, p => p.SourceId);
        var visibleSources = ownedSources
            .Concat(roles.Keys.Select(id => sourceById[id]))
            .Distinct()
            .ToArray();

        var sharedProjectIds = roles.Where(kv => kv.Value is ProjectRole.Viewer or ProjectRole.Editor)
            .Select(kv => kv.Key).ToArray();
        var sharedAccounts = sharedProjectIds.Length == 0
            ? []
            : await db.ProjectAccountAssignments.AsNoTracking()
                .Where(a => sharedProjectIds.Contains(a.ProjectId))
                .Select(a => a.AccountId)
                .Distinct()
                .ToArrayAsync(ct);

        return new AccessScope(userId, user.IsAdmin, roles, ownedSources, visibleSources, sharedAccounts);
    }
}
