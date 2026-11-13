using Microsoft.EntityFrameworkCore;
using OpenBudget.Connectors.Sync;
using OpenBudget.Services;

namespace OpenBudget.Importers.MoneyMoney;

/// <summary>
/// Undoes a user's first MoneyMoney import that failed part-way, so the user can simply try again: everything the import
/// creates goes (accounts, account groups, categories, category groups and the transactions, positions and value history
/// in MoneyMoney's half of the user's id space; the imported statements and their files; the project rows pointing at
/// them; the MoneyMoney category rules and budget; the import state). Rows OpenBudget or a connector created live in the
/// connector half of the id space and stay.
/// </summary>
public static class MoneyMoneyImportCleanup
{
    public static async Task RemoveAsync(BudgetDbContext db, BankStatementStore store, string userId, CancellationToken ct = default)
    {
        var state = await db.UserDataStates.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.UserId == userId, ct);
        if (state == null) return;
        var lo = RowIds.Namespace(state.IdSpace, 0);
        var hi = RowIds.Namespace(state.IdSpace, BankTransactionStore.ConnectorRowBase);

        var accounts = await db.Accounts.Where(a => a.OwnerUserId == userId && a.Id >= lo && a.Id < hi).Select(a => a.Id).ToListAsync(ct);
        var categories = await db.Categories.Where(c => c.OwnerUserId == userId && c.Id >= lo && c.Id < hi).Select(c => c.Id).ToListAsync(ct);
        var groups = await db.CategoryGroups.Where(g => g.OwnerUserId == userId && g.Id >= lo && g.Id < hi).Select(g => g.Id).ToListAsync(ct);
        var projects = await db.Projects.Where(p => p.OwnerUserId == userId).Select(p => p.Id).ToListAsync(ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var statements = await db.BankStatements.Where(s => s.OwnerUserId == userId && s.BankLoginId == null
            && s.Identifier.StartsWith(MoneyMoneyStatementImport.IdentifierPrefix)).ToListAsync(ct);
        var statementIds = statements.Select(s => s.Id).ToList();
        await db.StatementReads.Where(r => statementIds.Contains(r.StatementId)).ExecuteDeleteAsync(ct);
        await db.BankStatementAliases.Where(a => statementIds.Contains(a.StatementId)).ExecuteDeleteAsync(ct);
        await db.BankStatements.Where(s => statementIds.Contains(s.Id)).ExecuteDeleteAsync(ct);

        await db.ScheduledPayments.Where(p => accounts.Contains(p.AccountId)).ExecuteDeleteAsync(ct);
        await db.Holdings.Where(h => h.OwnerUserId == userId && (accounts.Contains(h.AccountId) || (h.Id >= lo && h.Id < hi))).ExecuteDeleteAsync(ct);
        await db.PortfolioChartPoints.Where(p => p.OwnerUserId == userId && (accounts.Contains(p.AccountId) || (p.Id >= lo && p.Id < hi))).ExecuteDeleteAsync(ct);
        await db.Transactions.Where(t => t.OwnerUserId == userId && t.SplitParentId != null && t.SplitParentId >= lo && t.SplitParentId < hi).ExecuteDeleteAsync(ct);
        await db.Transactions.Where(t => t.OwnerUserId == userId && ((t.Id >= lo && t.Id < hi) || (t.AccountId != null && accounts.Contains(t.AccountId.Value)))).ExecuteDeleteAsync(ct);
        await db.Transactions.Where(t => t.CategoryId != null && categories.Contains(t.CategoryId.Value))
            .ExecuteUpdateAsync(u => u.SetProperty(t => t.CategoryId, (long?)null), ct);
        await db.BankLoginAccounts.Where(l => l.LinkedAccountId != null && accounts.Contains(l.LinkedAccountId.Value))
            .ExecuteUpdateAsync(u => u.SetProperty(l => l.LinkedAccountId, (long?)null), ct);

        await db.ProjectAccountAssignments.Where(a => accounts.Contains(a.AccountId)).ExecuteDeleteAsync(ct);
        await db.ProjectCategoryAssignments.Where(a => projects.Contains(a.ProjectId) && (groups.Contains(a.CategoryId) || categories.Contains(a.CategoryId))).ExecuteDeleteAsync(ct);
        await db.ProjectTrackingItems.Where(i => projects.Contains(i.ProjectId)
            && ((i.CategoryId != null && categories.Contains(i.CategoryId.Value)) || (i.CategoryGroupId != null && groups.Contains(i.CategoryGroupId.Value)))).ExecuteDeleteAsync(ct);
        await db.CategoryTypeAssignments.Where(a => projects.Contains(a.ProjectId) && (categories.Contains(a.CategoryId) || groups.Contains(a.CategoryId))).ExecuteDeleteAsync(ct);
        await db.CategoryRules.Where(r => projects.Contains(r.ProjectId) && (r.ImportOrigin == MoneyMoneyRuleImport.Origin || categories.Contains(r.CategoryId))).ExecuteDeleteAsync(ct);
        var budgets = await db.Budgets.Where(b => projects.Contains(b.ProjectId) && b.Name == MoneyMoneyBudgetImport.BudgetName).Select(b => b.Id).ToListAsync(ct);
        await db.MonthlyBudgetEntries.Where(e => budgets.Contains(e.BudgetId)).ExecuteDeleteAsync(ct);
        await db.BudgetContributionSplits.Where(s => budgets.Contains(s.BudgetId)).ExecuteDeleteAsync(ct);
        await db.Budgets.Where(b => budgets.Contains(b.Id)).ExecuteDeleteAsync(ct);
        await db.MonthlyBudgetEntries.Where(e => categories.Contains(e.CategoryId) || groups.Contains(e.CategoryId)).ExecuteDeleteAsync(ct);
        await db.RemovedImportItems.IgnoreQueryFilters().Where(r => r.OwnerUserId == userId).ExecuteDeleteAsync(ct);

        // Parents first lose their children's references (the group trees refer to themselves).
        await db.Categories.Where(c => categories.Contains(c.Id)).ExecuteUpdateAsync(u => u.SetProperty(c => c.GroupId, (long?)null), ct);
        await db.CategoryGroups.Where(g => groups.Contains(g.Id)).ExecuteUpdateAsync(u => u.SetProperty(g => g.ParentGroupId, (long?)null), ct);
        await db.Categories.Where(c => categories.Contains(c.Id)).ExecuteDeleteAsync(ct);
        await db.CategoryGroups.Where(g => groups.Contains(g.Id)).ExecuteDeleteAsync(ct);
        await db.Accounts.Where(a => a.OwnerUserId == userId).ExecuteUpdateAsync(u => u.SetProperty(a => a.GroupId, a => a.GroupId >= lo && a.GroupId < hi ? null : a.GroupId), ct);
        await db.Accounts.Where(a => accounts.Contains(a.Id)).ExecuteDeleteAsync(ct);
        await db.AccountGroups.Where(g => g.OwnerUserId == userId && g.Id >= lo && g.Id < hi).ExecuteUpdateAsync(u => u.SetProperty(g => g.ParentGroupId, (long?)null), ct);
        await db.AccountGroups.Where(g => g.OwnerUserId == userId && g.Id >= lo && g.Id < hi).ExecuteDeleteAsync(ct);

        state.LastUploadAt = null;
        state.LastUploadSizeBytes = null;
        state.StructureTakenOverAt = null;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        foreach (var s in statements) store.DeleteFile(s.FilePath);
    }
}
