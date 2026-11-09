using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>
/// Assigns accounts that newly appear for a user to that user's "default project for new accounts"
/// (see <see cref="Project.IsDefaultForNewAccounts"/>). Called by the MoneyMoney import and by the bank
/// connector's "create new account"; only ever adds assignments, never changes or removes one.
/// </summary>
public static class DefaultProjectAssigner
{
    /// <summary>
    /// True when anyone but the owner has access to the project: it has shares itself, or its parent
    /// has (sub-projects inherit their parent's shares).
    /// </summary>
    public static async Task<bool> IsSharedAsync(BudgetDbContext db, int projectId, CancellationToken ct = default)
    {
        var parentId = await db.Projects.AsNoTracking()
            .Where(p => p.Id == projectId)
            .Select(p => p.ParentProjectId)
            .FirstOrDefaultAsync(ct);
        return await db.ProjectShares.AsNoTracking()
            .AnyAsync(s => s.ProjectId == projectId || (parentId != null && s.ProjectId == parentId), ct);
    }

    /// <summary>
    /// Assigns the given new accounts of <paramref name="ownerUserId"/> to the owner's default project, skipping
    /// any account that is already assigned somewhere. Does nothing without a default project, and (defensively)
    /// when that project turns out to be shared. Returns the number of assignments added.
    /// </summary>
    public static async Task<int> AssignNewAccountsAsync(
        BudgetDbContext db, string? ownerUserId, IReadOnlyCollection<long> accountIds, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(ownerUserId) || accountIds.Count == 0) return 0;

        var projectId = await db.Projects.AsNoTracking()
            .Where(p => p.OwnerUserId == ownerUserId && p.IsDefaultForNewAccounts)
            .Select(p => (int?)p.Id)
            .FirstOrDefaultAsync(ct);
        if (projectId is not { } id || await IsSharedAsync(db, id, ct)) return 0;

        var already = await db.ProjectAccountAssignments.AsNoTracking()
            .Where(a => accountIds.Contains(a.AccountId))
            .Select(a => a.AccountId)
            .Distinct()
            .ToListAsync(ct);
        var fresh = accountIds.Except(already).ToList();
        if (fresh.Count == 0) return 0;

        foreach (var accountId in fresh)
            db.ProjectAccountAssignments.Add(new ProjectAccountAssignment { ProjectId = id, AccountId = accountId });
        await db.SaveChangesAsync(ct);
        return fresh.Count;
    }
}
