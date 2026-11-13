using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>Where the signed-in user stands on the Get started page.</summary>
/// <param name="BankLogins">Their bank logins.</param>
/// <param name="HasAccounts">Whether they see any account at all (own or shared).</param>
/// <param name="Import">Their import state (MoneyMoney progress, Get started completion), if any.</param>
/// <param name="HasPortfolioData">Whether they hold Portfolio Performance data.</param>
/// <param name="ProjectId">The project the last step's links open (the current one, else their first).</param>
/// <param name="HasCategories">Whether they own any category.</param>
public sealed record OnboardingState(int BankLogins, bool HasAccounts, UserDataState? Import, bool HasPortfolioData, int? ProjectId, bool HasCategories = false)
{
    /// <summary>Whether a MoneyMoney import delivered data.</summary>
    public bool MoneyMoneyImported => Import is { LastUploadAt: not null };

    /// <summary>A MoneyMoney import that delivered data and was not declared finished.</summary>
    public bool MoneyMoneyUnfinished => Import is { LastUploadAt: not null, MoneyMoneyImportFinishedAt: null };

    /// <summary>
    /// The gear menu and the settings navigation offer Get started (and signing in leads to it) until the user is
    /// through with it (<see cref="IsFinished"/>).
    /// </summary>
    public bool ShowInMenu => !IsFinished(Import);

    /// <summary>Whether the user ended Get started with Done on its last step.</summary>
    public static bool IsFinished(UserDataState? state) => state is { GetStartedCompletedAt: not null };
}

/// <summary>A starter category group: its name, its categories (none = one category of the group's name) and whether it is income.</summary>
public sealed record StarterGroup(string Name, IReadOnlyList<string> Categories, bool Income = false);

/// <summary>The state behind the Get started page and its gear-menu entry, for the signed-in user.</summary>
public sealed class OnboardingService
{
    private readonly IDbContextFactory<BudgetDbContext> _dbFactory;
    private readonly ProjectService _projects;
    private readonly CurrentProjectState _current;
    private readonly ChangeNotificationService _notifier;
    private readonly ChangeOrigin _origin;

    public OnboardingService(IDbContextFactory<BudgetDbContext> dbFactory, ProjectService projects, CurrentProjectState current,
        ChangeNotificationService notifier, ChangeOrigin origin)
    {
        _dbFactory = dbFactory;
        _projects = projects;
        _current = current;
        _notifier = notifier;
        _origin = origin;
    }

    public async Task<OnboardingState> GetAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        if (db.Scope is not { IsAuthenticated: true, UserId: { } userId } scope) return new OnboardingState(0, false, null, false, null);
        var logins = await db.BankLogins.CountAsync(l => l.OwnerUserId == userId, ct);
        var hasAccounts = await db.Accounts.AnyAsync(ct);
        var hasPortfolio = await db.PpPortfolios.AnyAsync(p => p.OwnerUserId == userId, ct);
        var import = await db.UserDataStates.AsNoTracking().FirstOrDefaultAsync(s => s.UserId == userId, ct);
        var hasCategories = await db.Categories.AnyAsync(c => c.OwnerUserId == userId, ct);
        int? projectId = _current.ProjectId;
        if (projectId == null)
        {
            var projects = await _projects.GetProjectsAsync();
            projectId = projects.FirstOrDefault(p => scope.IsOwner(p.Id))?.Id ?? projects.FirstOrDefault()?.Id;
        }
        return new OnboardingState(logins, hasAccounts, import, hasPortfolio, projectId, hasCategories);
    }

    /// <summary>Completes Get started for the signed-in user: it leaves the menus and signing in no longer leads to it.</summary>
    public async Task CompleteAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        if (db.Scope is not { UserId: { } userId }) throw new AccessDeniedException("Not signed in.");
        var state = await RowIds.StateAsync(db, userId, ct);
        state.GetStartedCompletedAt ??= DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        _notifier.Publish(new DataChange(DataArea.ImportState, UserId: userId), _origin.Id);
    }

    /// <summary>
    /// Creates a starter category tree for the signed-in user, who owns no category yet (otherwise nothing happens):
    /// each group at the top level with its categories (categories coloured from the palette), assigned to the user's default project like imported groups,
    /// income groups typed as income and the other groups tracked in its budget, and a manual cash account named <paramref name="cashAccountName"/>.
    /// Returns whether it created them.
    /// </summary>
    public async Task<bool> CreateStarterCategoriesAsync(IReadOnlyList<StarterGroup> groups, string cashAccountName, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        if (db.Scope is not { UserId: { } userId }) throw new AccessDeniedException("Not signed in.");
        if (await db.Categories.AnyAsync(c => c.OwnerUserId == userId, ct)) return false;
        await RowIds.StateAsync(db, userId, ct);
        await db.SaveChangesAsync(ct);
        var root = await RowIds.SentinelRootAsync(db, userId, ct);
        long? parent = await db.CategoryGroups.AnyAsync(g => g.Id == root, ct) ? root : null;
        var next = await StructureService.NextOwnIdAsync(db, userId, ct);
        var incomeGroups = new List<long>();
        var expenseGroups = new List<long>();
        var incomeCategories = new List<long>();
        for (var gi = 0; gi < groups.Count; gi++)
        {
            var g = groups[gi];
            var groupId = next++;
            // The categories of each group share a colour of the palette; groups stay uncoloured.
            var color = CategoryColors.Palette[gi % CategoryColors.Palette.Count].Key;
            db.CategoryGroups.Add(new CategoryGroup { Id = groupId, OwnerUserId = userId, Name = g.Name, ParentGroupId = parent, Sort = gi });
            if (g.Income) incomeGroups.Add(groupId); else expenseGroups.Add(groupId);
            var names = g.Categories.Count > 0 ? g.Categories : [g.Name];
            for (var ci = 0; ci < names.Count; ci++)
            {
                var id = next++;
                db.Categories.Add(new Category { Id = id, OwnerUserId = userId, Name = names[ci], GroupId = groupId, Type = 1, Sort = ci, Color = color });
                if (g.Income) incomeCategories.Add(id);
            }
        }
        db.Accounts.Add(new Account
        {
            Id = await RowIds.NextConnectorAccountIdAsync(db, userId, ct),
            OwnerUserId = userId,
            Name = cashAccountName,
            Type = 6,
            Currency = "EUR",
            Provider = AccountProviders.Manual,
        });
        await db.SaveChangesAsync(ct);

        await DefaultProjectAssigner.AssignImportedAsync(db, userId, ct);
        var projectId = await db.Projects.AsNoTracking().Where(p => p.OwnerUserId == userId && p.IsDefaultForNewAccounts)
            .Select(p => (int?)p.Id).FirstOrDefaultAsync(ct);
        if (projectId is { } pid)
        {
            foreach (var id in incomeGroups)
                db.CategoryTypeAssignments.Add(new CategoryTypeAssignment { ProjectId = pid, CategoryId = id, ItemType = CategoryItemType.Group, IsIncome = true });
            foreach (var id in incomeCategories)
                db.CategoryTypeAssignments.Add(new CategoryTypeAssignment { ProjectId = pid, CategoryId = id, ItemType = CategoryItemType.Category, IsIncome = true });
            // Every expense group is tracked in the budget (without amounts).
            foreach (var id in expenseGroups)
                db.ProjectTrackingItems.Add(new ProjectTrackingItem { ProjectId = pid, CategoryGroupId = id });
            await db.SaveChangesAsync(ct);
        }
        _notifier.Publish(new DataChange(DataArea.ImportedData, OwnerUserId: userId), _origin.Id);
        _notifier.Publish(new DataChange(DataArea.Projects), _origin.Id);
        return true;
    }

    /// <summary>The owner's category tree for a project with whether each item is income and tracked in its budget.</summary>
    public async Task<List<CategorySetupRow>> GetCategorySetupAsync(int projectId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var ownerId = await db.Projects.Where(p => p.Id == projectId).Select(p => p.OwnerUserId).FirstOrDefaultAsync(ct)
            ?? throw new AccessDeniedException("Project not found.");
        var sentinels = (await RowIds.SentinelIdsAsync(db, ownerId, ct)).ToHashSet();
        var groups = await db.CategoryGroups.AsNoTracking().Where(g => g.OwnerUserId == ownerId).ToListAsync(ct);
        var categories = await db.Categories.AsNoTracking().Where(c => c.OwnerUserId == ownerId).ToListAsync(ct);
        var income = await db.CategoryTypeAssignments.AsNoTracking().Where(t => t.ProjectId == projectId && t.IsIncome)
            .Select(t => new { t.CategoryId, t.ItemType }).ToListAsync(ct);
        var tracked = await db.ProjectTrackingItems.AsNoTracking().Where(t => t.ProjectId == projectId).ToListAsync(ct);
        var rows = new List<CategorySetupRow>();
        void Add(long? parent, int depth)
        {
            var children = groups.Where(g => g.ParentGroupId == parent && !sentinels.Contains(g.Id))
                .Select(g => (Id: g.Id, Type: CategoryItemType.Group, g.Name, g.Sort))
                .Concat(categories.Where(c => c.GroupId == parent && !sentinels.Contains(c.Id)).Select(c => (Id: c.Id, Type: CategoryItemType.Category, c.Name, c.Sort)))
                .OrderBy(x => x.Sort ?? long.MaxValue).ThenBy(x => x.Name, StringComparer.CurrentCulture);
            foreach (var x in children)
            {
                rows.Add(new CategorySetupRow(x.Id, x.Type, x.Name ?? "", depth,
                    income.Any(i => i.CategoryId == x.Id && i.ItemType == x.Type),
                    tracked.Any(t => x.Type == CategoryItemType.Group ? t.CategoryGroupId == x.Id : t.CategoryId == x.Id)));
                if (x.Type == CategoryItemType.Group) Add(x.Id, depth + 1);
            }
        }
        var root = await RowIds.SentinelRootAsync(db, ownerId, ct);
        Add(groups.Any(g => g.Id == root) ? root : null, 0);
        return rows;
    }

    /// <summary>Tracks an item in the project's budget, or stops tracking it.</summary>
    public async Task SetTrackedAsync(int projectId, long id, CategoryItemType type, bool tracked, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var existing = await db.ProjectTrackingItems.Where(t => t.ProjectId == projectId
            && (type == CategoryItemType.Group ? t.CategoryGroupId == id : t.CategoryId == id)).ToListAsync(ct);
        if (tracked && existing.Count == 0)
            db.ProjectTrackingItems.Add(type == CategoryItemType.Group
                ? new ProjectTrackingItem { ProjectId = projectId, CategoryGroupId = id }
                : new ProjectTrackingItem { ProjectId = projectId, CategoryId = id });
        if (!tracked) db.ProjectTrackingItems.RemoveRange(existing);
        await db.SaveChangesAsync(ct);
        _notifier.Publish(new DataChange(DataArea.TrackingItems, ProjectId: projectId), _origin.Id);
    }
}

/// <summary>One row of the category setup: a group or category, its depth, and whether it is income and tracked.</summary>
public sealed record CategorySetupRow(long Id, CategoryItemType Type, string Name, int Depth, bool Income, bool Tracked);
