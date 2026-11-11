using Microsoft.EntityFrameworkCore;
using OpenBudget.Connectors;
using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>A downloaded document as the Documents view shows it.</summary>
/// <param name="AccountName">The account's name; for a document without an account, the bank login's name.</param>
public sealed record DocumentItem(long Id, long? AccountId, string AccountName, DateTime Date, string Name, bool Unread);

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
/// account) plus the account-less documents of the current user's own bank logins linked to them, each with the
/// user's own unread state, and the per-account download settings. Every document query runs through the
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
            .Select(s => new { s.Id, s.AccountId, s.BankLoginId, s.Date, s.Name, s.CreatedAt, Read = db.StatementReads.Any(r => r.StatementId == s.Id && !r.Unread), MarkedUnread = db.StatementReads.Any(r => r.StatementId == s.Id && r.Unread) })
            .ToListAsync(ct);
        var since = await UserCreatedAtAsync(db, userId, ct);
        var accountIds = rows.Where(r => r.AccountId != null).Select(r => r.AccountId!.Value).Distinct().ToList();
        var accountNames = await db.Accounts.AsNoTracking().Where(a => accountIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Name ?? "?", ct);
        var loginIds = rows.Where(r => r.AccountId == null).Where(r => r.BankLoginId != null).Select(r => r.BankLoginId!.Value).Distinct().ToList();
        var loginNames = await db.BankLogins.AsNoTracking().Where(l => loginIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => l.DisplayName, ct);
        return rows.Select(r => new DocumentItem(
            r.Id,
            r.AccountId,
            r.AccountId is { } a ? accountNames.GetValueOrDefault(a, "?") : r.BankLoginId is { } l ? loginNames.GetValueOrDefault(l, "?") : "?",
            r.Date,
            r.Name,
            r.MarkedUnread || (!r.Read && r.CreatedAt > since))).ToList();
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

    /// <summary>Statements of the project's accounts plus the user's own account-less login documents of logins linked to them.</summary>
    private static IQueryable<BankStatement> DocumentsQuery(BudgetDbContext db, int projectId)
    {
        var accountIds = db.ProjectAccountAssignments.Where(a => a.ProjectId == projectId).Select(a => a.AccountId);
        var loginIds = db.BankLoginAccounts
            .Where(a => a.LinkedAccountId != null && accountIds.Contains(a.LinkedAccountId.Value))
            .Select(a => a.BankLoginId);
        return db.BankStatements.AsNoTracking().Where(s =>
            (s.AccountId != null && accountIds.Contains(s.AccountId.Value))
            || (s.AccountId == null && s.BankLoginId != null && loginIds.Contains(s.BankLoginId.Value)));
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
