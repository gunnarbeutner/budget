using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>Where the signed-in user stands on the Get started page.</summary>
/// <param name="BankLogins">Their bank logins.</param>
/// <param name="HasAccounts">Whether they see any account at all (own or shared).</param>
/// <param name="Import">Their import state (MoneyMoney progress, Get started dismissal), if any.</param>
/// <param name="HasPortfolioData">Whether they hold Portfolio Performance data.</param>
/// <param name="ProjectId">The project the last step's links open (the current one, else their first).</param>
public sealed record OnboardingState(int BankLogins, bool HasAccounts, UserDataState? Import, bool HasPortfolioData, int? ProjectId)
{
    /// <summary>Whether a MoneyMoney import delivered data.</summary>
    public bool MoneyMoneyImported => Import is { LastUploadAt: not null };

    /// <summary>A MoneyMoney import that delivered data and was not declared finished.</summary>
    public bool MoneyMoneyUnfinished => Import is { LastUploadAt: not null, MoneyMoneyImportFinishedAt: null };

    /// <summary>The gear menu offers Get started while the user has no data yet or an unfinished MoneyMoney import.</summary>
    public bool ShowInMenu => (BankLogins == 0 && !HasAccounts) || MoneyMoneyUnfinished;
}

/// <summary>The state behind the Get started page and its gear-menu entry, for the signed-in user.</summary>
public sealed class OnboardingService
{
    private readonly IDbContextFactory<BudgetDbContext> _dbFactory;
    private readonly ProjectService _projects;
    private readonly CurrentProjectState _current;

    public OnboardingService(IDbContextFactory<BudgetDbContext> dbFactory, ProjectService projects, CurrentProjectState current)
    {
        _dbFactory = dbFactory;
        _projects = projects;
        _current = current;
    }

    public async Task<OnboardingState> GetAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        if (db.Scope is not { IsAuthenticated: true, UserId: { } userId } scope) return new OnboardingState(0, false, null, false, null);
        var logins = await db.BankLogins.CountAsync(l => l.OwnerUserId == userId, ct);
        var hasAccounts = await db.Accounts.AnyAsync(ct);
        var hasPortfolio = await db.PpPortfolios.AnyAsync(p => p.OwnerUserId == userId, ct);
        var import = await db.UserDataStates.AsNoTracking().FirstOrDefaultAsync(s => s.UserId == userId, ct);
        int? projectId = _current.ProjectId;
        if (projectId == null)
        {
            var projects = await _projects.GetProjectsAsync();
            projectId = projects.FirstOrDefault(p => scope.IsOwner(p.Id))?.Id ?? projects.FirstOrDefault()?.Id;
        }
        return new OnboardingState(logins, hasAccounts, import, hasPortfolio, projectId);
    }

}
