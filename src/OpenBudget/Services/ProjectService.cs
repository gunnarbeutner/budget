using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>
/// Project / source CRUD + per-project tracking-item and account-balance reads. Carved out
/// of BudgetService as part of the god-class split — no hierarchy or spending math here,
/// just the records that describe what a project is and which accounts / categories it owns.
/// </summary>
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
        var maxSortOrder = await db.Projects.AnyAsync()
            ? await db.Projects.MaxAsync(p => p.SortOrder)
            : -1;

        var project = new Project
        {
            Name = name,
            Description = description,
            CreatedAt = DateTime.UtcNow,
            SortOrder = maxSortOrder + 1,
            SourceId = sourceId
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
        var ordered = await db.Projects
            .OrderBy(p => p.SortOrder)
            .ThenByDescending(p => p.CreatedAt)
            .ToListAsync();

        var index = ordered.FindIndex(p => p.Id == projectId);
        if (index < 0) return;

        var swapIndex = index + direction;
        if (swapIndex < 0 || swapIndex >= ordered.Count) return;

        // Renumber so the swap is unambiguous even if any rows shared a SortOrder before.
        for (int i = 0; i < ordered.Count; i++)
            ordered[i].SortOrder = i;
        (ordered[index].SortOrder, ordered[swapIndex].SortOrder) =
            (ordered[swapIndex].SortOrder, ordered[index].SortOrder);

        await db.SaveChangesAsync();
        Notify(new DataChange(DataArea.Projects));
    }

    public async Task DeleteProjectAsync(int projectId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var project = await db.Projects.FindAsync(projectId);
        if (project != null)
        {
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
    /// parent belongs to a different MoneyMoney source, parent is one of this project's
    /// own descendants (cycle), or parent already has its own parent (we only support
    /// single-level hierarchies for now). Returns the resolved Project on success.
    /// </summary>
    public async Task<Project> SetProjectParentAsync(int projectId, int? parentId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var project = await db.Projects.FindAsync(projectId)
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

        var parent = await db.Projects.FindAsync(parentId.Value)
            ?? throw new ArgumentException("Parent project not found", nameof(parentId));

        if (parent.SourceId != project.SourceId)
            throw new InvalidOperationException(
                "Parent and child must share the same MoneyMoney source.");
        if (parent.ParentProjectId != null)
            throw new InvalidOperationException(
                "Only single-level hierarchies are supported; the chosen parent is itself a child.");
        var hasChildren = await db.Projects.AnyAsync(p => p.ParentProjectId == projectId);
        if (hasChildren)
            throw new InvalidOperationException(
                "This project already has child projects; it can't itself become a child.");

        project.ParentProjectId = parentId.Value;
        await db.SaveChangesAsync();
        Notify(new DataChange(DataArea.Projects, ProjectId: projectId));
        return project;
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

    /// <summary>All registered MoneyMoney sources, ordered by name.</summary>
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
        // BrowseService), so an informational securities depot contributes its real value to the
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
                // Informational accounts count toward the total only, not the cashflow balance.
                if (assignment.Scope != AccountScope.Informational)
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
