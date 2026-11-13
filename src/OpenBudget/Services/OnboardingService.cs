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
    /// each group at the top level with its categories, assigned to the user's default project like imported groups,
    /// income groups typed as income there. Returns whether it created them.
    /// </summary>
    public async Task<bool> CreateStarterCategoriesAsync(IReadOnlyList<StarterGroup> groups, CancellationToken ct = default)
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
        var incomeCategories = new List<long>();
        for (var gi = 0; gi < groups.Count; gi++)
        {
            var g = groups[gi];
            var groupId = next++;
            db.CategoryGroups.Add(new CategoryGroup { Id = groupId, OwnerUserId = userId, Name = g.Name, ParentGroupId = parent, Sort = gi });
            if (g.Income) incomeGroups.Add(groupId);
            var names = g.Categories.Count > 0 ? g.Categories : [g.Name];
            for (var ci = 0; ci < names.Count; ci++)
            {
                var id = next++;
                db.Categories.Add(new Category { Id = id, OwnerUserId = userId, Name = names[ci], GroupId = groupId, Type = 1, Sort = ci });
                if (g.Income) incomeCategories.Add(id);
            }
        }
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
            await db.SaveChangesAsync(ct);
        }
        _notifier.Publish(new DataChange(DataArea.ImportedData, OwnerUserId: userId), _origin.Id);
        _notifier.Publish(new DataChange(DataArea.Projects), _origin.Id);
        return true;
    }
}
