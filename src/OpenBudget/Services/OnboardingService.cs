using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>Where the signed-in user stands on the Get started page.</summary>
/// <param name="BankLogins">Their bank logins.</param>
/// <param name="HasAccounts">Whether they see any account at all (own or shared).</param>
/// <param name="MoneyMoneySource">Their source holding MoneyMoney data, if any.</param>
/// <param name="HasPortfolioData">Whether one of their sources holds Portfolio Performance data.</param>
/// <param name="ProjectId">The project the last step's links open (the current one, else their first).</param>
public sealed record OnboardingState(int BankLogins, bool HasAccounts, ImportSource? MoneyMoneySource, bool HasPortfolioData, int? ProjectId)
{
    /// <summary>A MoneyMoney import that delivered data and was not declared finished.</summary>
    public bool MoneyMoneyUnfinished => MoneyMoneySource is { LastUploadAt: not null, MoneyMoneyImportFinishedAt: null };

    /// <summary>The gear menu offers Get started while the user has no data yet or an unfinished MoneyMoney import.</summary>
    public bool ShowInMenu => (BankLogins == 0 && !HasAccounts) || MoneyMoneyUnfinished;
}

/// <summary>The state behind the Get started page and its gear-menu entry, for the signed-in user.</summary>
public sealed class OnboardingService
{
    private readonly IDbContextFactory<BudgetDbContext> _dbFactory;
    private readonly SourceService _sources;
    private readonly ProjectService _projects;
    private readonly CurrentProjectState _current;

    public OnboardingService(IDbContextFactory<BudgetDbContext> dbFactory, SourceService sources, ProjectService projects, CurrentProjectState current)
    {
        _dbFactory = dbFactory;
        _sources = sources;
        _projects = projects;
        _current = current;
    }

    public async Task<OnboardingState> GetAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        if (db.Scope is not { IsAuthenticated: true } scope) return new OnboardingState(0, false, null, false, null);
        var owned = await _sources.GetOwnedSourcesAsync(ct);
        var ownedIds = owned.Select(s => s.Id).ToList();
        var logins = await db.BankLogins.CountAsync(l => l.OwnerUserId == scope.UserId, ct);
        var hasAccounts = await db.Accounts.AnyAsync(ct);
        var hasPortfolio = await db.PpPortfolios.AnyAsync(p => ownedIds.Contains(p.SourceId), ct);
        var moneyMoney = owned.Where(s => s.LastUploadAt != null).OrderBy(s => s.MoneyMoneyImportFinishedAt != null).ThenBy(s => s.Id).FirstOrDefault();
        int? projectId = _current.ProjectId;
        if (projectId == null)
        {
            var projects = await _projects.GetProjectsAsync();
            projectId = projects.FirstOrDefault(p => scope.IsOwner(p.Id))?.Id ?? projects.FirstOrDefault()?.Id;
        }
        return new OnboardingState(logins, hasAccounts, moneyMoney, hasPortfolio, projectId);
    }
}
