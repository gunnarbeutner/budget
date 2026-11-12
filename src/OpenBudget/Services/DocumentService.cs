using Microsoft.EntityFrameworkCore;
using OpenBudget.Connectors;
using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>A downloaded document as the Documents view shows it.</summary>
/// <param name="AccountName">
/// The account's name; for a document without an account, the bank login's name or the bank it was imported from.
/// </param>
/// <param name="Institution">For a document without an account and login: the bank it was imported from.</param>
/// <param name="CanAssign">Whether the current user owns the document and so may assign it to an account.</param>
public sealed record DocumentItem(long Id, long? AccountId, string AccountName, DateTime Date, string Name, bool Unread, string? Institution = null, bool CanAssign = false);

/// <summary>An account documents can be assigned to.</summary>
public sealed record AssignableAccount(long Id, string Name);

/// <summary>An account in the cross-project "Documents to assign" picker, listed under each project that holds it.</summary>
/// <param name="GroupName">The account group it sits in within its source (null: none).</param>
public sealed record AssignTarget(int ProjectId, string ProjectName, long AccountId, string Name, string? GroupName, string Icon, bool Closed);

/// <summary>Why an account cannot download statements (null: it can).</summary>
public enum StatementBlock
{
    Manual,
    MoneyMoney,
    Closed,
    NotOffered,
}

/// <summary>One account of the project in the "Statement download" dialog.</summary>
/// <param name="CanChange">Only the bank login's owner may change the setting; everyone else sees it read-only.</param>
public sealed record StatementSetting(
    long AccountId,
    string Name,
    StatementBlock? Block,
    int? LoginId,
    int? LoginAccountId,
    bool Enabled,
    StatementsInterval Interval,
    DateTime? LastStatementAt,
    bool CanChange);

/// <summary>
/// The Documents view of a project: the statements of its accounts (visible to everyone who can see the
/// account) plus the account-less documents of the current user's own bank logins linked to them and the
/// current user's imported documents that belong to no account and no login, each with the user's own unread
/// state, and the per-account download settings. Every document query runs through the
/// user-scoped context, so the access rules of <see cref="BankStatement"/> apply unchanged.
/// </summary>
public class DocumentService
{
    private readonly IDbContextFactory<BudgetDbContext> _dbFactory;
    private readonly SystemDbContextFactory _system;
    private readonly AccessScopeProvider _access;
    private readonly ConnectorRegistry _registry;

    public DocumentService(IDbContextFactory<BudgetDbContext> dbFactory, SystemDbContextFactory system, AccessScopeProvider access, ConnectorRegistry registry)
    {
        _dbFactory = dbFactory;
        _system = system;
        _access = access;
        _registry = registry;
    }

    /// <summary>Raised (on this circuit) after read marks changed, so the sidebar count can follow.</summary>
    public event Action? ReadsChanged;

    /// <summary>The project's documents for the current user, newest first.</summary>
    public async Task<List<DocumentItem>> GetDocumentsAsync(int projectId, CancellationToken ct = default)
    {
        var userId = (await _access.GetAsync()).UserId;
        if (userId == null) return [];
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var query = DocumentsQuery(db, projectId);
        var rows = await query
            .OrderByDescending(s => s.Date).ThenByDescending(s => s.Id)
            .Select(s => new { s.Id, s.AccountId, s.BankLoginId, s.Institution, s.OwnerUserId, s.Date, s.Name, s.CreatedAt, Read = db.StatementReads.Any(r => r.StatementId == s.Id && !r.Unread), MarkedUnread = db.StatementReads.Any(r => r.StatementId == s.Id && r.Unread) })
            .ToListAsync(ct);
        var since = await UserCreatedAtAsync(db, userId, ct);
        var accountIds = rows.Where(r => r.AccountId != null).Select(r => r.AccountId!.Value).Distinct().ToList();
        var accountNames = await db.Accounts.AsNoTracking().Where(a => accountIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Name ?? "?", ct);
        var loginIds = rows.Where(r => r.AccountId == null).Where(r => r.BankLoginId != null).Select(r => r.BankLoginId!.Value).Distinct().ToList();
        var loginNames = await db.BankLogins.AsNoTracking().Where(l => loginIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => l.DisplayName, ct);
        return rows.Select(r => new DocumentItem(
            r.Id,
            r.AccountId,
            r.AccountId is { } a ? accountNames.GetValueOrDefault(a, "?") : r.BankLoginId is { } l ? loginNames.GetValueOrDefault(l, "?") : r.Institution ?? "?",
            r.Date,
            r.Name,
            r.MarkedUnread || (!r.Read && r.CreatedAt > since),
            r.AccountId == null && r.BankLoginId == null ? r.Institution : null,
            r.OwnerUserId == userId)).ToList();
    }

    /// <summary>How many of the project's documents the current user has not read yet.</summary>
    public async Task<int> GetUnreadCountAsync(int projectId, CancellationToken ct = default)
    {
        var userId = (await _access.GetAsync()).UserId;
        if (userId == null) return 0;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var since = await UserCreatedAtAsync(db, userId, ct);
        return await DocumentsQuery(db, projectId)
            .CountAsync(s => db.StatementReads.Any(r => r.StatementId == s.Id && r.Unread)
                || (s.CreatedAt > since && !db.StatementReads.Any(r => r.StatementId == s.Id)), ct);
    }

    /// <summary>Marks documents read for the current user (ones they cannot see are ignored).</summary>
    public Task MarkReadAsync(IReadOnlyCollection<long> statementIds, CancellationToken ct = default) => SetUnreadAsync(statementIds, false, ct);

    /// <summary>Marks documents unread again for the current user (ones they cannot see are ignored).</summary>
    public Task MarkUnreadAsync(IReadOnlyCollection<long> statementIds, CancellationToken ct = default) => SetUnreadAsync(statementIds, true, ct);

    private async Task SetUnreadAsync(IReadOnlyCollection<long> statementIds, bool unread, CancellationToken ct)
    {
        var userId = (await _access.GetAsync()).UserId;
        if (userId == null || statementIds.Count == 0) return;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var ids = statementIds.Distinct().ToList();
        var visible = await db.BankStatements.AsNoTracking().Where(s => ids.Contains(s.Id)).Select(s => s.Id).ToListAsync(ct);
        var existing = await db.StatementReads.Where(r => visible.Contains(r.StatementId)).ToDictionaryAsync(r => r.StatementId, ct);
        var now = DateTime.UtcNow;
        foreach (var id in visible)
        {
            if (existing.TryGetValue(id, out var row))
            {
                if (row.Unread == unread) continue;
                row.Unread = unread;
                row.ReadAt = now;
            }
            else
                db.StatementReads.Add(new StatementRead { UserId = userId, StatementId = id, ReadAt = now, Unread = unread });
        }
        if (db.ChangeTracker.HasChanges())
        {
            await db.SaveChangesAsync(ct);
            ReadsChanged?.Invoke();
        }
    }

    /// <summary>
    /// Statements of the project's accounts, the user's own account-less login documents of logins linked to them,
    /// and documents with neither account nor login (only their owner sees those, through the access filter).
    /// </summary>
    private static IQueryable<BankStatement> DocumentsQuery(BudgetDbContext db, int projectId)
    {
        var accountIds = db.ProjectAccountAssignments.Where(a => a.ProjectId == projectId).Select(a => a.AccountId);
        var loginIds = db.BankLoginAccounts
            .Where(a => a.LinkedAccountId != null && accountIds.Contains(a.LinkedAccountId.Value))
            .Select(a => a.BankLoginId);
        return db.BankStatements.AsNoTracking().Where(s =>
            (s.AccountId != null && accountIds.Contains(s.AccountId.Value))
            || (s.AccountId == null && s.BankLoginId != null && loginIds.Contains(s.BankLoginId.Value))
            || (s.AccountId == null && s.BankLoginId == null));
    }

    /// <summary>The project's accounts the current user may assign documents to (those whose transactions they may edit).</summary>
    public async Task<List<AssignableAccount>> GetAssignableAccountsAsync(int projectId, CancellationToken ct = default)
    {
        var scope = await _access.GetAsync();
        if (!scope.IsAuthenticated) return [];
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var accounts = await db.Accounts.AsNoTracking()
            .Where(a => db.ProjectAccountAssignments.Any(p => p.ProjectId == projectId && p.AccountId == a.Id))
            .Select(a => new { a.Id, a.SourceId, a.Name })
            .ToListAsync(ct);
        return accounts
            .Where(a => scope.CanEditTransactionsOf(a.SourceId, a.Id))
            .Select(a => new AssignableAccount(a.Id, a.Name ?? "?"))
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Files the current user's own documents among <paramref name="statementIds"/> under <paramref name="accountId"/>,
    /// which must be an account whose transactions the user may edit. Returns how many documents were assigned.
    /// </summary>
    public async Task<int> AssignAsync(IReadOnlyCollection<long> statementIds, long accountId, CancellationToken ct = default)
    {
        var scope = await _access.GetAsync();
        if (!scope.IsAuthenticated || statementIds.Count == 0) return 0;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var account = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == accountId, ct);
        if (account == null || !scope.CanEditTransactionsOf(account.SourceId, account.Id))
            throw new AccessDeniedException("Documents can only be assigned to accounts you may edit.");
        var ids = statementIds.Distinct().ToList();
        var rows = await db.BankStatements.Where(s => ids.Contains(s.Id) && s.OwnerUserId == scope.UserId).ToListAsync(ct);
        foreach (var row in rows)
        {
            row.AccountId = account.Id;
            row.SourceId = account.SourceId;
        }
        if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(ct);
        return rows.Count;
    }

    /// <summary>
    /// The current user's own documents that no project shows under an account: those without an account, and those
    /// whose account is gone or in none of the user's projects. Labelled by account, login or bank, newest first.
    /// </summary>
    public async Task<List<DocumentItem>> GetUnassignedAsync(CancellationToken ct = default)
    {
        var userId = (await _access.GetAsync()).UserId;
        if (userId == null) return [];
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await UnassignedQuery(db, userId)
            .OrderByDescending(s => s.Date).ThenByDescending(s => s.Id)
            .Select(s => new { s.Id, s.AccountId, s.BankLoginId, s.Institution, s.Date, s.Name, s.CreatedAt, Read = db.StatementReads.Any(r => r.StatementId == s.Id && !r.Unread), MarkedUnread = db.StatementReads.Any(r => r.StatementId == s.Id && r.Unread) })
            .ToListAsync(ct);
        var since = await UserCreatedAtAsync(db, userId, ct);
        var accountIds = rows.Where(r => r.AccountId != null).Select(r => r.AccountId!.Value).Distinct().ToList();
        var accountNames = await db.Accounts.AsNoTracking().Where(a => accountIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Name ?? "?", ct);
        var loginIds = rows.Where(r => r.BankLoginId != null).Select(r => r.BankLoginId!.Value).Distinct().ToList();
        var loginNames = await db.BankLogins.AsNoTracking().Where(l => loginIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => l.DisplayName, ct);
        return rows.Select(r => new DocumentItem(
            r.Id,
            r.AccountId,
            r.AccountId is { } a && accountNames.TryGetValue(a, out var an) ? an
                : r.BankLoginId is { } l && loginNames.TryGetValue(l, out var ln) ? ln
                : r.Institution ?? "?",
            r.Date,
            r.Name,
            r.MarkedUnread || (!r.Read && r.CreatedAt > since),
            r.Institution,
            true)).ToList();
    }

    /// <summary>How many documents <see cref="GetUnassignedAsync"/> lists.</summary>
    public async Task<int> GetUnassignedCountAsync(CancellationToken ct = default)
    {
        var userId = (await _access.GetAsync()).UserId;
        if (userId == null) return 0;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await UnassignedQuery(db, userId).CountAsync(ct);
    }

    private static IQueryable<BankStatement> UnassignedQuery(BudgetDbContext db, string userId)
    {
        var inProjects = db.ProjectAccountAssignments.Select(p => p.AccountId);
        return db.BankStatements.AsNoTracking().Where(s =>
            s.OwnerUserId == userId && (s.AccountId == null || !inProjects.Contains(s.AccountId.Value)));
    }

    /// <summary>
    /// Every account in the current user's projects that they may assign documents to (those whose transactions they
    /// may edit), by project and then account group, closed accounts after the open ones of their group (old statements
    /// usually belong to closed accounts).
    /// </summary>
    public async Task<List<AssignTarget>> GetAssignTargetsAsync(CancellationToken ct = default)
    {
        var scope = await _access.GetAsync();
        if (!scope.IsAuthenticated) return [];
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await (
            from p in db.ProjectAccountAssignments
            join pr in db.Projects on p.ProjectId equals pr.Id
            join a in db.Accounts on p.AccountId equals a.Id
            select new { pr.Id, ProjectName = pr.Name, pr.SortOrder, Account = a, GroupName = a.Group!.Name })
            .AsNoTracking()
            .ToListAsync(ct);
        return rows
            .Where(r => scope.CanEditTransactionsOf(r.Account.SourceId, r.Account.Id))
            .OrderBy(r => r.SortOrder).ThenBy(r => r.ProjectName, StringComparer.CurrentCultureIgnoreCase).ThenBy(r => r.Id)
            .ThenBy(r => r.GroupName ?? "", StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(r => r.Account.Closed)
            .ThenBy(r => r.Account.Sort ?? long.MaxValue).ThenBy(r => r.Account.Name ?? "", StringComparer.CurrentCultureIgnoreCase)
            .Select(r => new AssignTarget(r.Id, r.ProjectName, r.Account.Id, r.Account.Name ?? "?", r.GroupName, r.Account.Icon ?? r.Account.KindIcon, r.Account.Closed))
            .ToList();
    }

    private static async Task<DateTime> UserCreatedAtAsync(BudgetDbContext db, string userId, CancellationToken ct) =>
        await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => (DateTime?)u.CreatedAt).FirstOrDefaultAsync(ct) ?? DateTime.MinValue;

    /// <summary>
    /// Every account of the project with its download setting, or the reason it cannot download. The bank-login
    /// link is read without the owner filter (only for accounts the user can see) so that viewers see the setting
    /// read-only; changes go through <see cref="BankLoginService"/>, which enforces ownership.
    /// </summary>
    public async Task<List<StatementSetting>> GetSettingsAsync(int projectId, CancellationToken ct = default)
    {
        var userId = (await _access.GetAsync()).UserId;
        if (userId == null) return [];
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var accounts = await db.Accounts.AsNoTracking()
            .Where(a => db.ProjectAccountAssignments.Any(p => p.ProjectId == projectId && p.AccountId == a.Id))
            .Select(a => new { a.Id, a.Name, a.Provider, a.Sort })
            .ToListAsync(ct);
        var ids = accounts.Select(a => a.Id).ToList();

        await using var system = await _system.CreateDbContextAsync(ct);
        var links = await system.BankLoginAccounts.AsNoTracking()
            .Where(a => a.LinkedAccountId != null && ids.Contains(a.LinkedAccountId.Value))
            .Select(a => new { a.Id, a.BankLoginId, AccountId = a.LinkedAccountId!.Value, a.Closed, a.StatementsEnabled, a.StatementsInterval, a.LastStatementAt, a.BankLogin.ConnectorType, a.BankLogin.OwnerUserId })
            .ToListAsync(ct);

        var result = new List<StatementSetting>();
        foreach (var a in accounts.OrderBy(a => a.Name ?? "", StringComparer.CurrentCultureIgnoreCase))
        {
            var link = links.Where(l => l.AccountId == a.Id).OrderBy(l => l.Closed).ThenBy(l => l.Id).FirstOrDefault();
            if (link == null)
            {
                var block = a.Provider == AccountProviders.Manual ? StatementBlock.Manual : StatementBlock.MoneyMoney;
                result.Add(new StatementSetting(a.Id, a.Name ?? "?", block, null, null, false, StatementsInterval.Monthly, null, false));
                continue;
            }
            StatementBlock? reason = link.Closed ? StatementBlock.Closed
                : _registry.Find(link.ConnectorType) is { Statements: true } ? null
                : StatementBlock.NotOffered;
            result.Add(new StatementSetting(a.Id, a.Name ?? "?", reason, link.BankLoginId, link.Id,
                reason == null && link.StatementsEnabled, link.StatementsInterval, link.LastStatementAt, link.OwnerUserId == userId));
        }
        return result;
    }
}
