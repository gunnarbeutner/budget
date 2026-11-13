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

        var accounts = (await db.Accounts.AsNoTracking().Where(a => fresh.Contains(a.Id)).ToListAsync(ct))
            .ToDictionary(a => a.Id);
        foreach (var accountId in fresh)
            db.ProjectAccountAssignments.Add(accounts.TryGetValue(accountId, out var account)
                ? AccountAssignmentDefaults.For(id, account)
                : new ProjectAccountAssignment { ProjectId = id, AccountId = accountId, Scope = AccountAssignmentDefaults.Scope, InCashflow = false });
        await db.SaveChangesAsync(ct);
        return fresh.Count;
    }

    /// <summary>
    /// Assigns every account and every top-level category group (directly under the hidden root) of
    /// <paramref name="ownerId"/> to that user's default project, skipping what is already assigned there. Used when a
    /// MoneyMoney import finishes; running it again adds nothing. Returns the number of assignments added.
    /// </summary>
    public static async Task<int> AssignImportedAsync(BudgetDbContext db, string ownerId, CancellationToken ct = default)
    {
        var owner = ownerId;
        var accountIds = await db.Accounts.Where(a => a.OwnerUserId == ownerId).Select(a => a.Id).ToListAsync(ct);
        var added = await AssignNewAccountsAsync(db, owner, accountIds, ct);

        var projectId = await db.Projects.AsNoTracking()
            .Where(p => p.OwnerUserId == owner && p.IsDefaultForNewAccounts)
            .Select(p => (int?)p.Id)
            .FirstOrDefaultAsync(ct);
        if (projectId is not { } id || await IsSharedAsync(db, id, ct)) return added;

        var root = RowIds.SentinelRoot(await RowIds.IdSpaceAsync(db, ownerId, ct));
        var topLevel = await db.CategoryGroups
            .Where(g => g.OwnerUserId == ownerId && g.Id != root && (g.ParentGroupId == null || g.ParentGroupId == root))
            .Select(g => g.Id).ToListAsync(ct);
        var assigned = await db.ProjectCategoryAssignments.Where(a => a.ProjectId == id).Select(a => a.CategoryId).ToListAsync(ct);
        foreach (var groupId in topLevel.Except(assigned))
        {
            db.ProjectCategoryAssignments.Add(new ProjectCategoryAssignment { ProjectId = id, CategoryId = groupId });
            added++;
        }
        await db.SaveChangesAsync(ct);
        return added;
    }
}
