using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>
/// Project / source CRUD + per-project tracking-item and account-balance reads. Carved out
/// of BudgetService as part of the god-class split — no hierarchy or spending math here,
/// just the records that describe what a project is and which accounts / categories it owns.
/// </summary>
/// <summary>Owner view of a project's "default for new accounts" setting.</summary>
public sealed record DefaultProjectState(bool IsDefault, bool IsShared, bool ClearedNotice);

public class ProjectService
{
    private readonly IDbContextFactory<BudgetDbContext> _dbFactory;
    private readonly ChangeNotificationService _notifier;
    private readonly ChangeOrigin _origin;

    public ProjectService(IDbContextFactory<BudgetDbContext> dbFactory, ChangeNotificationService notifier, ChangeOrigin origin)
    {
        _dbFactory = dbFactory;
        _notifier = notifier;
        _origin = origin;
    }

    // Publish a change stamped with this circuit's origin so our own components skip the echo.
    private void Notify(DataChange change) => _notifier.Publish(change, _origin.Id);

    public async Task<List<Project>> GetProjectsAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.Projects
            .AsNoTracking()
            .Include(b => b.CategoryAssignments)
            .OrderBy(b => b.SortOrder)
            .ThenByDescending(b => b.CreatedAt)
            .ToListAsync();
    }

    /// <summary>Get a project with assignments, tracking items, and category-type assignments.</summary>
    public async Task<Project?> GetProjectAsync(int projectId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.Projects
            .AsNoTracking()
            .Include(b => b.CategoryAssignments)
            .Include(b => b.TrackingItems)
            .Include(b => b.CategoryTypeAssignments)
            .FirstOrDefaultAsync(b => b.Id == projectId);
    }

    public async Task<Project> CreateProjectAsync(string name, int sourceId, string? description = null)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        // A user can only create projects on sources they own (the context's write guard enforces it
        // too); the new project is owned by the creator.
        db.Scope?.RequireSourceOwner(sourceId);
        var maxSortOrder = await db.Projects.AnyAsync()
            ? await db.Projects.MaxAsync(p => p.SortOrder)
            : -1;

        var project = new Project
        {
            Name = name,
            Description = description,
            CreatedAt = DateTime.UtcNow,
            SortOrder = maxSortOrder + 1,
            SourceId = sourceId,
            OwnerUserId = db.Scope?.UserId
        };

        db.Projects.Add(project);
        await db.SaveChangesAsync();
        Notify(new DataChange(DataArea.Projects, ProjectId: project.Id));
        return project;
    }

    /// <summary>
    /// Move a project up (direction = -1) or down (+1) in the dashboard order by swapping
    /// SortOrder with its immediate neighbor. No-op when already at the edge.
    /// </summary>
    public async Task MoveProjectAsync(int projectId, int direction)
    {
        if (direction != -1 && direction != 1)
            throw new ArgumentException("direction must be -1 or 1", nameof(direction));

        await using var db = await _dbFactory.CreateDbContextAsync();
        var ordered = await OwnedInOrderAsync(db, projectId);
        var index = ordered.FindIndex(p => p.Id == projectId);
        if (index < 0) return;

        var swapIndex = index + direction;
        if (swapIndex < 0 || swapIndex >= ordered.Count) return;

        (ordered[index], ordered[swapIndex]) = (ordered[swapIndex], ordered[index]);
        await SaveOrderAsync(db, ordered);
    }

    /// <summary>
    /// Drag and drop in the project switcher: move a project to the position of <paramref name="targetProjectId"/>
    /// (dragging down lands after it, dragging up before it). Owner-only, among the owner's own projects.
    /// </summary>
    public async Task MoveProjectToAsync(int projectId, int targetProjectId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        db.Scope?.RequireRole(targetProjectId, ProjectRole.Owner);
        var ordered = await OwnedInOrderAsync(db, projectId);
        var from = ordered.FindIndex(p => p.Id == projectId);
        var to = ordered.FindIndex(p => p.Id == targetProjectId);
        if (from < 0 || to < 0 || from == to) return;

        var moved = ordered[from];
        ordered.RemoveAt(from);
        ordered.Insert(to, moved);
        await SaveOrderAsync(db, ordered);
    }

    // The order is the owner's: only owned projects are reordered (shared ones keep their place), so a user can
    // never rewrite another user's project rows. Moving a project you don't own is refused.
    private static async Task<List<Project>> OwnedInOrderAsync(BudgetDbContext db, int projectId)
    {
        var query = db.Projects.AsQueryable();
        if (db.Scope is { } scope)
        {
            scope.RequireRole(projectId, ProjectRole.Owner);
            var owned = scope.OwnedProjectIds;
            query = query.Where(p => owned.Contains(p.Id));
        }
        return await query
            .OrderBy(p => p.SortOrder)
            .ThenByDescending(p => p.CreatedAt)
            .ToListAsync();
    }

    // Renumber the whole list so the new order is unambiguous even if rows shared a SortOrder before.
    private async Task SaveOrderAsync(BudgetDbContext db, List<Project> ordered)
    {
        for (int i = 0; i < ordered.Count; i++)
            ordered[i].SortOrder = i;
        await db.SaveChangesAsync();
        Notify(new DataChange(DataArea.ProjectOrder));
    }

    /// <summary>Rename / re-describe a project (editors may; the context's write guard enforces it).</summary>
    public async Task UpdateProjectAsync(int projectId, string name, string? description)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("The name must not be empty.", nameof(name));
        await using var db = await _dbFactory.CreateDbContextAsync();
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId)
            ?? throw new ArgumentException("Project not found", nameof(projectId));
        project.Name = name.Trim();
        project.Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        await db.SaveChangesAsync();
        Notify(new DataChange(DataArea.Projects, ProjectId: projectId));
    }

    public async Task DeleteProjectAsync(int projectId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        // Bulk deletes bypass the context's write guard, so check ownership first (a project the caller can't
        // see is refused just the same). The app's SQLite connections don't enforce foreign keys, so don't rely
        // on ON DELETE CASCADE either.
        db.Scope?.RequireRole(projectId, ProjectRole.Owner);
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId);
        if (project != null)
        {
            await db.ProjectShares.Where(s => s.ProjectId == projectId).ExecuteDeleteAsync();
            db.Projects.Remove(project);
            await db.SaveChangesAsync();
            Notify(new DataChange(DataArea.Projects, ProjectId: projectId));
        }
    }

    /// <summary>Immediate children (one level) of a project, ordered by SortOrder.</summary>
    public async Task<List<Project>> GetChildProjectsAsync(int parentId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.Projects
            .AsNoTracking()
            .Where(p => p.ParentProjectId == parentId)
            .OrderBy(p => p.SortOrder)
            .ThenByDescending(p => p.CreatedAt)
            .ToListAsync();
    }

    /// <summary>
    /// Set or clear a project's parent. Rejected when: parent is the project itself,
    /// parent belongs to a different import source, parent is one of this project's
    /// own descendants (cycle), or parent already has its own parent (we only support
    /// single-level hierarchies for now). Returns the resolved Project on success.
    /// </summary>
    public async Task<Project> SetProjectParentAsync(int projectId, int? parentId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId)
            ?? throw new ArgumentException("Project not found", nameof(projectId));

        if (parentId == null)
        {
            project.ParentProjectId = null;
            await db.SaveChangesAsync();
            Notify(new DataChange(DataArea.Projects, ProjectId: projectId));
            return project;
        }

        if (parentId.Value == projectId)
            throw new InvalidOperationException("A project cannot be its own parent.");

        var parent = await db.Projects.FirstOrDefaultAsync(p => p.Id == parentId.Value)
            ?? throw new ArgumentException("Parent project not found", nameof(parentId));

        if (parent.SourceId != project.SourceId)
            throw new InvalidOperationException(
                "Parent and child must share the same import source.");
        if (parent.ParentProjectId != null)
            throw new InvalidOperationException(
                "Only single-level hierarchies are supported; the chosen parent is itself a child.");
        var hasChildren = await db.Projects.AnyAsync(p => p.ParentProjectId == projectId);
        if (hasChildren)
            throw new InvalidOperationException(
                "This project already has child projects; it can't itself become a child.");

        project.ParentProjectId = parentId.Value;
        // Under a shared parent the project becomes visible to the sharees, so it can no longer be the default.
        if (project.IsDefaultForNewAccounts && await db.ProjectShares.AnyAsync(s => s.ProjectId == parent.Id))
        {
            project.IsDefaultForNewAccounts = false;
            project.DefaultClearedNotice = true;
        }
        await db.SaveChangesAsync();
        Notify(new DataChange(DataArea.Projects, ProjectId: projectId));
        return project;
    }

    /// <summary>
    /// Mark (or unmark) a project as the owner's default for new accounts; at most one project per owner is the
    /// default, so marking one unmarks the previous one. Owner-only, and a shared project (or sub-project of a
    /// shared one) is refused: a newly arriving account must never become visible to a sharee automatically.
    /// </summary>
    public async Task SetDefaultForNewAccountsAsync(int projectId, bool isDefault)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId)
            ?? throw new AccessDeniedException("Project not found.");
        db.Scope?.RequireRole(projectId, ProjectRole.Owner);

        if (!isDefault)
        {
            if (!project.IsDefaultForNewAccounts) return;
            project.IsDefaultForNewAccounts = false;
            await db.SaveChangesAsync();
        }
        else
        {
            if (project.IsDefaultForNewAccounts) return;
            if (await DefaultProjectAssigner.IsSharedAsync(db, projectId))
                throw new InvalidOperationException("A shared project cannot be the default for new accounts.");
            var owner = project.OwnerUserId ?? throw new InvalidOperationException("The project has no owner.");
            // Clear the previous default first, in its own save, so the one-per-owner index never sees two.
            var others = await db.Projects.Where(p => p.OwnerUserId == owner && p.IsDefaultForNewAccounts && p.Id != projectId).ToListAsync();
            if (others.Count > 0)
            {
                foreach (var other in others) other.IsDefaultForNewAccounts = false;
                await db.SaveChangesAsync();
            }
            project.IsDefaultForNewAccounts = true;
            project.DefaultClearedNotice = false;
            await db.SaveChangesAsync();
        }
        Notify(new DataChange(DataArea.Projects));
    }

    /// <summary>Owner state of the "default for new accounts" setting; null when the caller does not own the project.</summary>
    public async Task<DefaultProjectState?> GetDefaultStateAsync(int projectId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        if (db.Scope is { } scope && !scope.IsOwner(projectId)) return null;
        var project = await db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId);
        if (project == null) return null;
        var shared = await DefaultProjectAssigner.IsSharedAsync(db, projectId);
        return new DefaultProjectState(project.IsDefaultForNewAccounts, shared, project.DefaultClearedNotice);
    }

    /// <summary>Dismiss the notice that sharing cleared this project's default status.</summary>
    public async Task DismissDefaultClearedNoticeAsync(int projectId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == projectId)
            ?? throw new AccessDeniedException("Project not found.");
        db.Scope?.RequireRole(projectId, ProjectRole.Owner);
        if (!project.DefaultClearedNotice) return;
        project.DefaultClearedNotice = false;
        await db.SaveChangesAsync();
    }

    public async Task AssignCategoryToProjectAsync(int projectId, long categoryId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var existing = await db.ProjectCategoryAssignments
            .FirstOrDefaultAsync(a => a.ProjectId == projectId && a.CategoryId == categoryId);
        if (existing != null) return;

        db.ProjectCategoryAssignments.Add(new ProjectCategoryAssignment
        {
            ProjectId = projectId,
            CategoryId = categoryId
        });
        await db.SaveChangesAsync();
        Notify(new DataChange(DataArea.Projects, ProjectId: projectId));
    }

    public async Task RemoveCategoryFromProjectAsync(int projectId, long categoryId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var assignment = await db.ProjectCategoryAssignments
            .FirstOrDefaultAsync(a => a.ProjectId == projectId && a.CategoryId == categoryId);
        if (assignment != null)
        {
            db.ProjectCategoryAssignments.Remove(assignment);
            await db.SaveChangesAsync();
            Notify(new DataChange(DataArea.Projects, ProjectId: projectId));
        }
    }

    /// <summary>
    /// Resolve the SourceId for a project. Single FK lookup; callers that already loaded the
    /// project should pass <c>project.SourceId</c> directly instead.
    /// </summary>
    public async Task<int> GetSourceIdForProjectAsync(int projectId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.Projects
            .Where(p => p.Id == projectId)
            .Select(p => p.SourceId)
            .FirstOrDefaultAsync();
    }

    /// <summary>
    /// Import sources visible to the current user (owned ones plus the sources of shared projects),
    /// ordered by name. Only owned sources may be picked for new projects; see <c>SourceService</c>.
    /// </summary>
    public async Task<List<ImportSource>> GetAllSourcesAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.ImportSources
            .AsNoTracking()
            .OrderBy(s => s.Name)
            .ToListAsync();
    }

    public async Task<List<ProjectTrackingItem>> GetProjectTrackingItemsAsync(int projectId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.ProjectTrackingItems
            .AsNoTracking()
            .Where(pti => pti.ProjectId == projectId)
            .ToListAsync();
    }

    public async Task UpdateProjectTrackingItemsAsync(int projectId, HashSet<long> categoryGroupIds, HashSet<long> categoryIds)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var existing = await db.ProjectTrackingItems
            .Where(pti => pti.ProjectId == projectId)
            .ToListAsync();
        db.ProjectTrackingItems.RemoveRange(existing);

        foreach (var groupId in categoryGroupIds)
        {
            db.ProjectTrackingItems.Add(new ProjectTrackingItem
            {
                ProjectId = projectId,
                CategoryGroupId = groupId,
                CategoryId = null
            });
        }

        foreach (var categoryId in categoryIds)
        {
            db.ProjectTrackingItems.Add(new ProjectTrackingItem
            {
                ProjectId = projectId,
                CategoryGroupId = null,
                CategoryId = categoryId
            });
        }

        await db.SaveChangesAsync();
        Notify(new DataChange(DataArea.TrackingItems, ProjectId: projectId));
    }

    /// <summary>
    /// Returns the current combined balance of each project's assigned accounts in a single
    /// pass — two DB calls regardless of project count. Projects with no assignments are
    /// omitted; the caller treats absence as "no accounts linked".
    /// </summary>
    public async Task<Dictionary<int, ProjectBalanceSummary>> GetAllProjectsBalancesAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var assignments = await db.ProjectAccountAssignments.AsNoTracking().ToListAsync();
        if (assignments.Count == 0) return new Dictionary<int, ProjectBalanceSummary>();

        // AccountIds are namespaced globally unique across sources, so a single Contains() query
        // resolves accounts for every project regardless of source.
        var accountIds = assignments.Select(a => a.AccountId).Distinct().ToList();
        var accounts = await db.Accounts
            .AsNoTracking()
            .Where(a => accountIds.Contains(a.Id))
            .ToListAsync();
        var accountById = accounts.ToDictionary(a => a.Id);

        // Depot accounts report holdings market value rather than the cash blob (mirrors
        // BrowseService), so a securities depot contributes its real value to the
        // total. One grouped query over every assigned depot in view.
        var depotIds = accounts.Where(a => a.Type == 7 || a.Portfolio == 1).Select(a => a.Id).ToList();
        var holdingsValueByAccount = depotIds.Count == 0
            ? new Dictionary<long, double>()
            : await db.Holdings
                .Where(h => depotIds.Contains(h.AccountId))
                .GroupBy(h => h.AccountId)
                .Select(g => new { AccountId = g.Key, Value = g.Sum(h => h.Amount ?? 0) })
                .ToDictionaryAsync(x => x.AccountId, x => x.Value);

        decimal BalanceOf(Account a) =>
            (a.Type == 7 || a.Portfolio == 1) && holdingsValueByAccount.TryGetValue(a.Id, out var hv)
                ? (decimal)hv
                : a.CurrentBalance ?? 0m;

        var result = new Dictionary<int, ProjectBalanceSummary>();
        foreach (var grp in assignments.GroupBy(a => a.ProjectId))
        {
            decimal cashflowBalance = 0m, totalBalance = 0m;
            int cashflowCount = 0, totalCount = 0;
            foreach (var assignment in grp)
            {
                if (!accountById.TryGetValue(assignment.AccountId, out var acc)) continue;
                decimal b = BalanceOf(acc);
                totalBalance += b;
                totalCount++;
                // Accounts outside the cashflow count toward the total only.
                if (assignment.InCashflow)
                {
                    cashflowBalance += b;
                    cashflowCount++;
                }
            }
            result[grp.Key] = new ProjectBalanceSummary
            {
                Balance = cashflowBalance,
                AccountCount = cashflowCount,
                TotalBalance = totalBalance,
                TotalAccountCount = totalCount
            };
        }
        return result;
    }
}
