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
    private readonly BudgetDbContext _db;

    public ProjectService(BudgetDbContext db)
    {
        _db = db;
    }

    public async Task<List<Project>> GetProjectsAsync()
    {
        return await _db.Projects
            .Include(b => b.CategoryAssignments)
            .OrderBy(b => b.SortOrder)
            .ThenByDescending(b => b.CreatedAt)
            .ToListAsync();
    }

    /// <summary>Get a project with assignments, tracking items, and category-type assignments.</summary>
    public async Task<Project?> GetProjectAsync(int projectId)
    {
        return await _db.Projects
            .Include(b => b.CategoryAssignments)
            .Include(b => b.TrackingItems)
            .Include(b => b.CategoryTypeAssignments)
            .FirstOrDefaultAsync(b => b.Id == projectId);
    }

    public async Task<Project> CreateProjectAsync(string name, int sourceId, string? description = null)
    {
        var maxSortOrder = await _db.Projects.AnyAsync()
            ? await _db.Projects.MaxAsync(p => p.SortOrder)
            : -1;

        var project = new Project
        {
            Name = name,
            Description = description,
            CreatedAt = DateTime.UtcNow,
            SortOrder = maxSortOrder + 1,
            SourceId = sourceId
        };

        _db.Projects.Add(project);
        await _db.SaveChangesAsync();
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

        var ordered = await _db.Projects
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

        await _db.SaveChangesAsync();
    }

    public async Task DeleteProjectAsync(int projectId)
    {
        var project = await _db.Projects.FindAsync(projectId);
        if (project != null)
        {
            _db.Projects.Remove(project);
            await _db.SaveChangesAsync();
        }
    }

    /// <summary>Immediate children (one level) of a project, ordered by SortOrder.</summary>
    public async Task<List<Project>> GetChildProjectsAsync(int parentId)
    {
        return await _db.Projects
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
        var project = await _db.Projects.FindAsync(projectId)
            ?? throw new ArgumentException("Project not found", nameof(projectId));

        if (parentId == null)
        {
            project.ParentProjectId = null;
            await _db.SaveChangesAsync();
            return project;
        }

        if (parentId.Value == projectId)
            throw new InvalidOperationException("A project cannot be its own parent.");

        var parent = await _db.Projects.FindAsync(parentId.Value)
            ?? throw new ArgumentException("Parent project not found", nameof(parentId));

        if (parent.SourceId != project.SourceId)
            throw new InvalidOperationException(
                "Parent and child must share the same MoneyMoney source.");
        if (parent.ParentProjectId != null)
            throw new InvalidOperationException(
                "Only single-level hierarchies are supported; the chosen parent is itself a child.");
        var hasChildren = await _db.Projects.AnyAsync(p => p.ParentProjectId == projectId);
        if (hasChildren)
            throw new InvalidOperationException(
                "This project already has child projects; it can't itself become a child.");

        project.ParentProjectId = parentId.Value;
        await _db.SaveChangesAsync();
        return project;
    }

    public async Task AssignCategoryToProjectAsync(int projectId, long categoryId)
    {
        var existing = await _db.ProjectCategoryAssignments
            .FirstOrDefaultAsync(a => a.ProjectId == projectId && a.CategoryId == categoryId);
        if (existing != null) return;

        _db.ProjectCategoryAssignments.Add(new ProjectCategoryAssignment
        {
            ProjectId = projectId,
            CategoryId = categoryId
        });
        await _db.SaveChangesAsync();
    }

    public async Task RemoveCategoryFromProjectAsync(int projectId, long categoryId)
    {
        var assignment = await _db.ProjectCategoryAssignments
            .FirstOrDefaultAsync(a => a.ProjectId == projectId && a.CategoryId == categoryId);
        if (assignment != null)
        {
            _db.ProjectCategoryAssignments.Remove(assignment);
            await _db.SaveChangesAsync();
        }
    }

    /// <summary>
    /// Resolve the SourceId for a project. Single FK lookup; callers that already loaded the
    /// project should pass <c>project.SourceId</c> directly instead.
    /// </summary>
    public async Task<int> GetSourceIdForProjectAsync(int projectId)
    {
        return await _db.Projects
            .Where(p => p.Id == projectId)
            .Select(p => p.SourceId)
            .FirstOrDefaultAsync();
    }

    /// <summary>All registered MoneyMoney sources, ordered by name.</summary>
    public async Task<List<ImportSource>> GetAllSourcesAsync()
    {
        return await _db.ImportSources
            .OrderBy(s => s.Name)
            .ToListAsync();
    }

    public async Task<List<ProjectTrackingItem>> GetProjectTrackingItemsAsync(int projectId)
    {
        return await _db.ProjectTrackingItems
            .Where(pti => pti.ProjectId == projectId)
            .ToListAsync();
    }

    public async Task UpdateProjectTrackingItemsAsync(int projectId, HashSet<long> categoryGroupIds, HashSet<long> categoryIds)
    {
        var existing = await _db.ProjectTrackingItems
            .Where(pti => pti.ProjectId == projectId)
            .ToListAsync();
        _db.ProjectTrackingItems.RemoveRange(existing);

        foreach (var groupId in categoryGroupIds)
        {
            _db.ProjectTrackingItems.Add(new ProjectTrackingItem
            {
                ProjectId = projectId,
                CategoryGroupId = groupId,
                CategoryId = null
            });
        }

        foreach (var categoryId in categoryIds)
        {
            _db.ProjectTrackingItems.Add(new ProjectTrackingItem
            {
                ProjectId = projectId,
                CategoryGroupId = null,
                CategoryId = categoryId
            });
        }

        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Returns the current combined balance of each project's assigned accounts in a single
    /// pass — two DB calls regardless of project count. Projects with no assignments are
    /// omitted; the caller treats absence as "no accounts linked".
    /// </summary>
    public async Task<Dictionary<int, ProjectBalanceSummary>> GetAllProjectsBalancesAsync()
    {
        var assignments = await _db.ProjectAccountAssignments.ToListAsync();
        if (!assignments.Any()) return new Dictionary<int, ProjectBalanceSummary>();

        // AccountIds are namespaced globally unique across sources, so a single Contains() query
        // resolves accounts for every project regardless of source.
        var accountIds = assignments.Select(a => a.AccountId).Distinct().ToList();
        var accounts = await _db.Accounts
            .Where(a => accountIds.Contains(a.Id))
            .ToListAsync();
        var accountById = accounts.ToDictionary(a => a.Id);

        var result = new Dictionary<int, ProjectBalanceSummary>();
        foreach (var grp in assignments.GroupBy(a => a.ProjectId))
        {
            decimal balance = 0m;
            int linkedCount = 0;
            foreach (var assignment in grp)
            {
                if (!accountById.TryGetValue(assignment.AccountId, out var acc)) continue;
                balance += acc.CurrentBalance ?? 0m;
                linkedCount++;
            }
            result[grp.Key] = new ProjectBalanceSummary
            {
                Balance = balance,
                AccountCount = linkedCount
            };
        }
        return result;
    }
}
