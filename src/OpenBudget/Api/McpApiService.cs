using System.Globalization;
using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.Api;

/// <summary>
/// The read-only data behind the MCP API (docs/mcp-api.md). Every read goes through the user-scoped context
/// (<see cref="IDbContextFactory{TContext}"/> → <see cref="ScopedDbContextFactory"/>) and the app's own services, so the
/// API sees exactly what the token's user sees in the UI: their own accounts plus the accounts of projects shared with
/// them. Nothing here writes.
/// </summary>
public sealed class McpApiService
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 500;
    private const string UncategorizedName = "Uncategorized";

    private readonly IDbContextFactory<BudgetDbContext> _dbFactory;
    private readonly AccessScopeProvider _access;
    private readonly TransactionQueryService _transactions;
    private readonly ProjectService _projects;
    private readonly BudgetEntryService _budgetEntries;
    private readonly BudgetService _budgets;
    private readonly TimeProvider _time;

    public McpApiService(
        IDbContextFactory<BudgetDbContext> dbFactory,
        AccessScopeProvider access,
        TransactionQueryService transactions,
        ProjectService projects,
        BudgetEntryService budgetEntries,
        BudgetService budgets,
        TimeProvider time)
    {
        _dbFactory = dbFactory;
        _access = access;
        _transactions = transactions;
        _projects = projects;
        _budgetEntries = budgetEntries;
        _budgets = budgets;
        _time = time;
    }

    public async Task<McpAccountList> GetAccountsAsync(CancellationToken ct)
    {
        var scope = await _access.GetAsync();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var accounts = await VisibleAccountsAsync(db, ct);
        return new McpAccountList(accounts.Select(a => new McpAccount(
            a.Id, a.Name ?? "", TypeName(a.Kind), CurrencySums.Normalize(a.Currency), a.Closed,
            scope.Owns(a.OwnerUserId) ? "own" : "shared")).ToList());
    }

    /// <summary>
    /// Current balances as the app shows them (ProjectService.GetAllProjectsBalancesAsync,
    /// TransactionQueryService.GetAccountBalanceHistoryAsync): a depot is worth its holdings, any other account has the
    /// balance its bank reports, else its initial balance plus every booking in its currency.
    /// </summary>
    public async Task<McpBalances> GetBalancesAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var accounts = await VisibleAccountsAsync(db, ct);
        var ids = accounts.Select(a => a.Id).ToList();
        var holdings = await db.Holdings.AsNoTracking().Where(h => ids.Contains(h.AccountId))
            .GroupBy(h => h.AccountId).Select(g => new { g.Key, Value = g.Sum(h => h.Amount ?? 0) })
            .ToDictionaryAsync(x => x.Key, x => (decimal)x.Value, ct);
        var withoutBankBalance = accounts.Where(a => a.CurrentBalance == null).Select(a => a.Id).ToList();
        var flows = withoutBankBalance.Count == 0
            ? []
            : await db.Transactions.AsNoTracking()
                .Where(t => t.AccountId != null && withoutBankBalance.Contains(t.AccountId.Value) && t.Booked && t.Splitted != SplitState.Parent)
                .Select(t => new { AccountId = t.AccountId!.Value, t.Amount, t.Currency })
                .ToListAsync(ct);
        var flowsByAccount = flows.ToLookup(f => f.AccountId);

        var rows = new List<McpBalance>();
        foreach (var a in accounts)
        {
            var currency = CurrencySums.Normalize(a.Currency);
            decimal balance = a.IsDepot && holdings.TryGetValue(a.Id, out var hv)
                ? hv
                : a.CurrentBalance ?? (a.InitialBalance + flowsByAccount[a.Id].Where(f => CurrencySums.Normalize(f.Currency) == currency).Sum(f => f.Amount)) / 100m;
            rows.Add(new McpBalance(a.Id, a.Name ?? "", TypeName(a.Kind), a.Closed, new McpMoney(balance, currency),
                a.LastRefreshUtc is { } r ? new DateTimeOffset(r, TimeSpan.Zero) : null));
        }
        var totals = CurrencySums.Group(rows.Where(r => !r.Closed).Select(r => ((string?)r.Balance.Currency, r.Balance.Amount)))
            .Select(c => new McpMoney(c.Amount, c.Currency)).ToList();
        return new McpBalances(rows, totals);
    }

    public async Task<McpTransactionList> SearchTransactionsAsync(McpTransactionFilter filter, CancellationToken ct)
    {
        var scope = await _access.GetAsync();
        var leafIds = filter.Category is { Length: > 0 } c ? await ResolveCategoryAsync(c, ct) : null;
        if (leafIds is { Count: 0 }) return new McpTransactionList([], 0, filter.Limit);

        var rows = new List<TransactionRow>();
        var total = 0;
        // TransactionQueryService queries one owner's data at a time; the user sees their own and their sharers'.
        foreach (var owner in scope.VisibleOwnerIds)
        {
            var page = await _transactions.QueryAsync(new TransactionQuery
            {
                OwnerUserId = owner,
                SearchText = filter.Query,
                FromUtc = filter.FromUtc,
                ToUtcExclusive = filter.ToUtcExclusive,
                AccountIds = filter.AccountId is { } a ? [a] : null,
                CategoryLeafIds = leafIds,
                Take = filter.Limit,
            }, ct);
            rows.AddRange(page.Rows);
            total += page.TotalCount;
        }
        var items = rows.OrderByDescending(r => r.Date).ThenByDescending(r => r.Id).Take(filter.Limit)
            .Select(r => new McpTransaction(
                r.Id,
                DateOnly.FromDateTime(r.Date),
                r.ValueDate is { } v ? DateOnly.FromDateTime(v) : null,
                new McpMoney(r.Amount, CurrencySums.Normalize(r.Currency)),
                r.Name,
                r.Purpose,
                r.Comment,
                r.Note,
                r.CategoryId is not null && !string.IsNullOrEmpty(r.CategoryName) ? new McpRef(r.CategoryId.Value, r.CategoryName) : null,
                r.AccountId is { } acc ? new McpRef(acc, r.AccountName ?? "") : null,
                r.IsPending,
                r.IsSplitChild))
            .ToList();
        return new McpTransactionList(items, total, filter.Limit);
    }

    /// <summary>
    /// Income and expenses in [from, to), by category or by month and per currency. Like the app's spending figures it
    /// leaves out split parents (their parts count), transfers between own accounts (paired bookings) and transfer
    /// categories.
    /// </summary>
    public async Task<McpSpendingSummary> SummarizeSpendingAsync(DateTime fromUtc, DateTime toUtcExclusive, bool byMonth, CancellationToken ct)
    {
        var scope = await _access.GetAsync();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var transferCategories = await CategoryHierarchyService.TransferCategoryIdsAsync(db, null, ct);
        var categoryNames = await db.Categories.AsNoTracking().Select(c => new { c.Id, c.Name }).ToDictionaryAsync(c => c.Id, c => c.Name, ct);

        var bookings = new List<(long Timestamp, long Amount, string? Currency, long? CategoryId)>();
        foreach (var owner in scope.VisibleOwnerIds)
        {
            var q = TransactionQueryService.BuildFilteredQuery(db, new TransactionQuery { OwnerUserId = owner, FromUtc = fromUtc, ToUtcExclusive = toUtcExclusive })
                .Where(t => t.TransferPeerId == null);
            var part = await q.Select(t => new { t.Timestamp, t.Amount, t.Currency, t.CategoryId }).ToListAsync(ct);
            bookings.AddRange(part.Where(t => t.CategoryId is not { } id || !transferCategories.Contains(id))
                .Select(t => (t.Timestamp, t.Amount, t.Currency, t.CategoryId)));
        }

        string CategoryKey(long? id) => id is { } i && categoryNames.TryGetValue(i, out var n) && !string.IsNullOrEmpty(n) ? n : UncategorizedName;
        var groups = bookings
            .GroupBy(b => (
                Key: byMonth
                    ? DateTimeOffset.FromUnixTimeSeconds(b.Timestamp).UtcDateTime.ToString("yyyy-MM", CultureInfo.InvariantCulture)
                    : CategoryKey(b.CategoryId),
                CategoryId: byMonth ? null : (b.CategoryId is { } i && categoryNames.TryGetValue(i, out var n) && !string.IsNullOrEmpty(n) ? b.CategoryId : null),
                Currency: CurrencySums.Normalize(b.Currency)))
            .Select(g => new McpSpendingGroup(
                g.Key.Key,
                g.Key.CategoryId,
                g.Key.Currency,
                g.Where(b => b.Amount > 0).Sum(b => b.Amount) / 100m,
                -g.Where(b => b.Amount < 0).Sum(b => b.Amount) / 100m,
                g.Sum(b => b.Amount) / 100m,
                g.Count()))
            .ToList();
        groups = byMonth
            ? groups.OrderBy(g => g.Key, StringComparer.Ordinal).ThenBy(g => g.Currency, StringComparer.Ordinal).ToList()
            : groups.OrderByDescending(g => g.Expenses).ThenBy(g => g.Key, StringComparer.Ordinal).ToList();
        var totals = bookings.GroupBy(b => CurrencySums.Normalize(b.Currency), StringComparer.Ordinal)
            .Select(g => new McpSpendingTotal(g.Key, g.Where(b => b.Amount > 0).Sum(b => b.Amount) / 100m,
                -g.Where(b => b.Amount < 0).Sum(b => b.Amount) / 100m, g.Sum(b => b.Amount) / 100m))
            .OrderBy(t => t.Currency, StringComparer.Ordinal).ToList();
        return new McpSpendingSummary(DateOnly.FromDateTime(fromUtc), DateOnly.FromDateTime(toUtcExclusive.AddDays(-1)),
            byMonth ? "month" : "category", groups, totals);
    }

    /// <summary>
    /// Every visible project's budget for <paramref name="year"/> (the active one, as the Budget view picks it) with
    /// budgeted and actual amounts per tracked item, year to date — the figures of the app's Budget view
    /// (<see cref="BudgetService.GetSpendingSummaryAsync"/>).
    /// </summary>
    public async Task<McpBudgetList> GetBudgetsAsync(int year, CancellationToken ct)
    {
        var scope = await _access.GetAsync();
        var now = _time.GetUtcNow().UtcDateTime;
        var toMonth = year == now.Year ? now.Month : 12;
        var result = new List<McpProjectBudget>();
        foreach (var project in await _projects.GetProjectsAsync())
        {
            ct.ThrowIfCancellationRequested();
            var budgets = await _budgetEntries.GetBudgetsForProjectAsync(project.Id);
            var budget = budgets.FirstOrDefault(b => b.Year == year && b.IsActive) ?? budgets.FirstOrDefault(b => b.Year == year);
            if (budget == null) continue;
            var summary = await _budgets.GetSpendingSummaryAsync(project.Id, year, 1, toMonth);
            result.Add(new McpProjectBudget(
                project.Id,
                project.Name,
                RoleName(scope.RoleFor(project.Id)),
                budget.Id,
                budget.Name,
                year,
                1,
                toMonth,
                "EUR",
                summary.TotalBudget,
                summary.TotalSpent,
                summary.TotalRemaining,
                summary.CategorySpending.Select(c => new McpBudgetLine(
                    c.CategoryId, c.ItemType == CategoryItemType.Group ? "group" : "category", c.CategoryName, c.IsIncome,
                    c.Budget, c.Spent, c.Remaining)).ToList()));
        }
        return new McpBudgetList(year, result);
    }

    private static async Task<List<Account>> VisibleAccountsAsync(BudgetDbContext db, CancellationToken ct) =>
        (await db.Accounts.AsNoTracking().Where(a => a.Type == null || a.Type != (long)AccountType.Group).ToListAsync(ct))
            .OrderBy(a => a.Closed).ThenBy(a => a.Sort ?? long.MaxValue).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>
    /// The visible categories a filter names: by id, or by name (case-insensitive) of a category or a category group,
    /// a group standing for every category below it. Empty when nothing matches.
    /// </summary>
    private async Task<List<long>> ResolveCategoryAsync(string filter, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var categories = await db.Categories.AsNoTracking().Select(c => new { c.Id, c.Name, c.GroupId }).ToListAsync(ct);
        var groups = await db.CategoryGroups.AsNoTracking().Select(g => new { g.Id, g.Name, g.ParentGroupId }).ToListAsync(ct);
        var needle = filter.Trim();
        if (long.TryParse(needle, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            return categories.Where(c => c.Id == id).Select(c => c.Id).ToList();

        var matchedGroups = groups.Where(g => string.Equals(g.Name?.Trim(), needle, StringComparison.OrdinalIgnoreCase)).Select(g => g.Id).ToHashSet();
        // Expand to every subgroup (guarding against cycles).
        var children = groups.Where(g => g.ParentGroupId != null).ToLookup(g => g.ParentGroupId!.Value, g => g.Id);
        var pending = new Queue<long>(matchedGroups);
        while (pending.Count > 0)
            foreach (var child in children[pending.Dequeue()])
                if (matchedGroups.Add(child)) pending.Enqueue(child);

        return categories
            .Where(c => string.Equals(c.Name?.Trim(), needle, StringComparison.OrdinalIgnoreCase) || (c.GroupId is { } g && matchedGroups.Contains(g)))
            .Select(c => c.Id).ToList();
    }

    private static string RoleName(ProjectRole role) => role switch
    {
        ProjectRole.Owner => "owner",
        ProjectRole.Editor => "editor",
        _ => "viewer",
    };

    private static string TypeName(AccountType? kind) => kind switch
    {
        AccountType.Giro => "checking",
        AccountType.Savings => "savings",
        AccountType.CreditCard => "creditCard",
        AccountType.Loan => "loan",
        AccountType.Manual => "manual",
        AccountType.Cash => "cash",
        AccountType.Depot => "portfolio",
        _ => "other",
    };
}

public sealed record McpTransactionFilter(string? Query, DateTime? FromUtc, DateTime? ToUtcExclusive, long? AccountId, string? Category, int Limit);

public sealed record McpMoney(decimal Amount, string Currency);
public sealed record McpRef(long Id, string Name);
public sealed record McpAccount(long Id, string Name, string Type, string Currency, bool Closed, string Ownership);
public sealed record McpAccountList(IReadOnlyList<McpAccount> Accounts);
public sealed record McpBalance(long AccountId, string Name, string Type, bool Closed, McpMoney Balance, DateTimeOffset? RefreshedAt);
public sealed record McpBalances(IReadOnlyList<McpBalance> Balances, IReadOnlyList<McpMoney> Totals);
public sealed record McpTransaction(
    long Id, DateOnly Date, DateOnly? ValueDate, McpMoney Amount, string? Payee, string? Purpose, string? Comment, string? Note,
    McpRef? Category, McpRef? Account, bool Pending, bool SplitPart);
public sealed record McpTransactionList(IReadOnlyList<McpTransaction> Transactions, int TotalCount, int Limit);
public sealed record McpSpendingGroup(string Key, long? CategoryId, string Currency, decimal Income, decimal Expenses, decimal Net, int Count);
public sealed record McpSpendingTotal(string Currency, decimal Income, decimal Expenses, decimal Net);
public sealed record McpSpendingSummary(DateOnly From, DateOnly To, string GroupBy, IReadOnlyList<McpSpendingGroup> Groups, IReadOnlyList<McpSpendingTotal> Totals);
public sealed record McpBudgetLine(long Id, string Kind, string Name, bool IsIncome, decimal Budget, decimal Actual, decimal Remaining);
public sealed record McpProjectBudget(
    int ProjectId, string ProjectName, string Role, int BudgetId, string BudgetName, int Year, int FromMonth, int ToMonth,
    string Currency, decimal TotalBudget, decimal TotalActual, decimal TotalRemaining, IReadOnlyList<McpBudgetLine> Lines);
public sealed record McpBudgetList(int Year, IReadOnlyList<McpProjectBudget> Projects);
