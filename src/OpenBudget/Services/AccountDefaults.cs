using Microsoft.EntityFrameworkCore;
using OpenBudget.Connectors;
using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>
/// An account's role and its projects' "In cashflow" flags as they follow from what its connector knows (the connector's
/// <see cref="ConnectorRegistration.AccountRoles"/>, the account's type). The owner's own choices
/// (<see cref="Account.RoleByUser"/>, <see cref="ProjectAccountAssignment.InCashflowByUser"/>) are never changed here.
/// </summary>
public static class AccountDefaults
{
    /// <summary>The role the connector linked to the account gives it; <see cref="AccountRole.None"/> for an unlinked account.</summary>
    public static async Task<(AccountRole Role, string? Connector)> DerivedRoleAsync(
        BudgetDbContext db, ConnectorRegistry? registry, long accountId, CancellationToken ct = default)
    {
        var link = await db.BankLoginAccounts.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.LinkedAccountId == accountId)
            .Select(a => new { a.Type, a.BankLogin.ConnectorType })
            .FirstOrDefaultAsync(ct);
        if (link == null || registry?.Find(link.ConnectorType) is not { } registration) return (AccountRole.None, null);
        return (registration.RoleFor(link.Type), registration.DisplayName);
    }

    /// <summary>
    /// Gives the account the role its connector knows, unless its owner picked one, and lets the "In cashflow" flags the
    /// owner did not set follow. Does not save.
    /// </summary>
    public static async Task ApplyRoleAsync(BudgetDbContext db, Account account, AccountRole derived, CancellationToken ct = default)
    {
        if (account.RoleByUser || account.Role == derived) return;
        account.Role = derived;
        await FollowCashflowAsync(db, account, ct);
    }

    /// <summary>Sets the account's "In cashflow" flags its owner did not set to <see cref="AccountAssignmentDefaults.InCashflow"/>. Does not save.</summary>
    public static async Task FollowCashflowAsync(BudgetDbContext db, Account account, CancellationToken ct = default)
    {
        var inCashflow = AccountAssignmentDefaults.InCashflow(account);
        var assignments = await db.ProjectAccountAssignments.IgnoreQueryFilters()
            .Where(a => a.AccountId == account.Id && !a.InCashflowByUser && a.InCashflow != inCashflow)
            .ToListAsync(ct);
        foreach (var assignment in assignments) assignment.InCashflow = inCashflow;
    }

    /// <summary>
    /// Sorts out accounts and assignments stored before choices were told apart from defaults. A role other than None
    /// and the connector's counts as the owner's; the rest take the connector's. No "In cashflow" flag changes: one that
    /// differs from <see cref="AccountAssignmentDefaults.InCashflow"/> counts as the owner's, the rest follow the default
    /// from now on. Saves.
    /// </summary>
    public static async Task<(int UserRoles, int UserFlags)> BackfillAsync(BudgetDbContext db, ConnectorRegistry registry, CancellationToken ct = default)
    {
        var links = await db.BankLoginAccounts.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.LinkedAccountId != null)
            .Select(a => new { AccountId = a.LinkedAccountId!.Value, a.Type, a.BankLogin.ConnectorType })
            .ToListAsync(ct);
        var derivedById = links
            .GroupBy(l => l.AccountId)
            .ToDictionary(g => g.Key, g => registry.Find(g.First().ConnectorType)?.RoleFor(g.First().Type) ?? AccountRole.None);

        var accounts = await db.Accounts.IgnoreQueryFilters().ToDictionaryAsync(a => a.Id, ct);
        var userRoles = 0;
        foreach (var account in accounts.Values.Where(a => !a.RoleByUser))
        {
            var derived = derivedById.GetValueOrDefault(account.Id);
            if (account.Role != AccountRole.None && account.Role != derived)
            {
                account.RoleByUser = true;
                userRoles++;
            }
            else
                account.Role = derived;
        }

        var userFlags = 0;
        foreach (var assignment in await db.ProjectAccountAssignments.IgnoreQueryFilters().Where(a => !a.InCashflowByUser).ToListAsync(ct))
        {
            if (!accounts.TryGetValue(assignment.AccountId, out var account)) continue;
            if (assignment.InCashflow == AccountAssignmentDefaults.InCashflow(account)) continue;
            assignment.InCashflowByUser = true;
            userFlags++;
        }
        await db.SaveChangesAsync(ct);
        return (userRoles, userFlags);
    }
}
