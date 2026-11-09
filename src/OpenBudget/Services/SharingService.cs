using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>A user a project is shared with, as shown on the project's settings page.</summary>
public sealed record ProjectShareInfo(int ShareId, string UserId, string UserName, ProjectRole Role);

/// <summary>
/// Project sharing: the owner grants a user viewer or editor access, changes it, or revokes it.
/// Authorization is enforced by the database context (only the project's owner may touch shares);
/// this service adds validation and change notifications. Sub-projects inherit their parent's shares
/// automatically (see <see cref="AccessScopeProvider"/>), so shares are only ever stored per project.
/// </summary>
public class SharingService
{
    private readonly IDbContextFactory<BudgetDbContext> _dbFactory;
    private readonly SystemDbContextFactory _system;
    private readonly ChangeNotificationService _notifier;
    private readonly ChangeOrigin _origin;

    public SharingService(IDbContextFactory<BudgetDbContext> dbFactory, SystemDbContextFactory system, ChangeNotificationService notifier, ChangeOrigin origin)
    {
        _dbFactory = dbFactory;
        _system = system;
        _notifier = notifier;
        _origin = origin;
    }

    /// <summary>Shares of a project the caller owns; empty for anyone else.</summary>
    public async Task<List<ProjectShareInfo>> GetSharesAsync(int projectId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        if (db.Scope is { } scope && !scope.IsOwner(projectId)) return new List<ProjectShareInfo>();

        var shares = await db.ProjectShares.AsNoTracking()
            .Where(s => s.ProjectId == projectId)
            .ToListAsync(ct);
        var names = await UserNamesAsync(shares.Select(s => s.UserId), ct);
        return shares
            .Select(s => new ProjectShareInfo(s.Id, s.UserId, names.GetValueOrDefault(s.UserId, "?"), s.Role))
            .OrderBy(s => s.UserName, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Create or change a share. Role must be Viewer or Editor.</summary>
    public async Task SetShareAsync(int projectId, string userId, ProjectRole role, CancellationToken ct = default)
    {
        if (role is not (ProjectRole.Viewer or ProjectRole.Editor))
            throw new ArgumentException("A share is either Viewer or Editor.", nameof(role));

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId, ct)
            ?? throw new AccessDeniedException("Project not found.");
        db.Scope?.RequireRole(projectId, ProjectRole.Owner);
        if (project.OwnerUserId == userId)
            throw new InvalidOperationException("The owner already has full access.");
        if (!await UserExistsAsync(userId, ct))
            throw new InvalidOperationException("Unknown user.");

        var existing = await db.ProjectShares.FirstOrDefaultAsync(s => s.ProjectId == projectId && s.UserId == userId, ct);
        if (existing == null)
            db.ProjectShares.Add(new ProjectShare { ProjectId = projectId, UserId = userId, Role = role });
        else
            existing.Role = role;

        await db.SaveChangesAsync(ct);
        _notifier.Publish(new DataChange(DataArea.Projects, ProjectId: projectId), _origin.Id);
    }

    public async Task RevokeShareAsync(int projectId, string userId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var existing = await db.ProjectShares.FirstOrDefaultAsync(s => s.ProjectId == projectId && s.UserId == userId, ct);
        if (existing == null) return;
        db.ProjectShares.Remove(existing);
        await db.SaveChangesAsync(ct);
        _notifier.Publish(new DataChange(DataArea.Projects, ProjectId: projectId), _origin.Id);
    }

    private async Task<bool> UserExistsAsync(string userId, CancellationToken ct)
    {
        await using var sys = await _system.CreateDbContextAsync(ct);
        return await sys.Users.AnyAsync(u => u.Id == userId, ct);
    }

    private async Task<Dictionary<string, string>> UserNamesAsync(IEnumerable<string> ids, CancellationToken ct)
    {
        var list = ids.Distinct(StringComparer.Ordinal).ToList();
        await using var sys = await _system.CreateDbContextAsync(ct);
        return await sys.Users.AsNoTracking().Where(u => list.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.UserName ?? "", StringComparer.Ordinal, ct);
    }
}
