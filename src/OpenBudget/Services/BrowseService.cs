using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>
/// Builds the left-rail tree for the per-project browser: linked accounts on top, then the
/// project's tracked-category subtree decorated with period spent / budget / remaining at
/// every node. One transactions query covers the whole tree — leaf-rollup happens in-memory
/// using the same primitives (<see cref="CategoryHierarchyService.BuildItemToLeafCategoryIds"/>,
/// <see cref="BudgetEntryService.ComputeEffectiveAmounts"/>) the dashboard uses, so the
/// numbers tie out to <see cref="BudgetService.GetSpendingSummaryAsync"/> at the same scope.
/// </summary>
public class BrowseService
{
    private readonly IDbContextFactory<BudgetDbContext> _dbFactory;
    private readonly CategoryHierarchyService _hierarchy;
    private readonly ProjectService _projects;
    private readonly BudgetEntryService _entries;
    private readonly BudgetService _budgets;
    private readonly AccessScopeProvider? _access;

    public BrowseService(
        IDbContextFactory<BudgetDbContext> dbFactory,
        CategoryHierarchyService hierarchy,
        ProjectService projects,
        BudgetEntryService entries,
        BudgetService budgets,
        AccessScopeProvider? access = null)
    {
        _access = access;
        _dbFactory = dbFactory;
        _hierarchy = hierarchy;
        _projects = projects;
        _entries = entries;
        _budgets = budgets;
    }

    public async Task<BrowseTree?> GetTreeAsync(int projectId, int year, int fromMonth, int toMonth, CancellationToken ct = default)
    {
        if (fromMonth < 1 || fromMonth > 12) throw new ArgumentOutOfRangeException(nameof(fromMonth));
        if (toMonth < fromMonth || toMonth > 12) throw new ArgumentOutOfRangeException(nameof(toMonth));

        var project = await _projects.GetProjectAsync(projectId);
        if (project == null) return null;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var hierarchy = await _hierarchy.GetAllCategoriesWithChildCountsAsync(project.SourceId);
        var trackedKeys = project.TrackingItems.ToTrackedKeys().ToList();
        var trackedSet = trackedKeys.ToHashSet();
        var incomeItems = await _budgets.GetIncomeItemsForProjectAsync(projectId);

        // Stored monthly amounts for the active budget. Mirror BudgetService's seeding so every
        // tracked group has an entry — ComputeEffectiveAmounts then auto-sums when descendants
        // carry the actual amounts.
        var allBudgets = await _entries.GetBudgetsForProjectAsync(projectId);
        var activeBudget = allBudgets.FirstOrDefault(b => b.Year == year && b.IsActive)
            ?? allBudgets.FirstOrDefault(b => b.Year == year);
        var stored = activeBudget != null
            ? activeBudget.MonthlyEntries.ToDictionary(
                e => (e.CategoryId, e.ItemType),
                e => new[]
                {
                    e.Month1, e.Month2, e.Month3, e.Month4, e.Month5, e.Month6,
                    e.Month7, e.Month8, e.Month9, e.Month10, e.Month11, e.Month12
                })
            : new Dictionary<(long, CategoryItemType), decimal[]>();
        foreach (var key in trackedKeys.Where(k => k.ItemType == CategoryItemType.Group))
        {
            if (!stored.ContainsKey(key)) stored[key] = new decimal[12];
        }
        var effective = BudgetEntryService.ComputeEffectiveAmounts(stored, hierarchy);

        // Roots of the tree: the project's assigned CategoryGroups (legacy table is named
        // ProjectCategoryAssignments + a CategoryId column, but the values it stores are
        // CategoryGroup IDs — confirmed by ProjectDetail.razor's GetCategoryWithSubCategoriesAsync
        // lookup). When a project has assignments, we use those as the anchors and include the
        // full descendant hierarchy beneath each — so a tracked sub-category appears with its
        // intermediate parent groups still in the path between it and the assigned root, but
        // we don't walk above the assigned root.
        //
        // When no assignments exist (older projects that only set TrackingItems), we fall back
        // to using tracking items themselves as roots so those projects keep rendering.
        var assignedGroupIds = await db.ProjectCategoryAssignments
            .Where(a => a.ProjectId == projectId)
            .Select(a => a.CategoryId)
            .ToListAsync(ct);

        var inTree = new HashSet<(long Id, CategoryItemType ItemType)>();
        var rootKeys = new List<(long Id, CategoryItemType ItemType)>();

        if (assignedGroupIds.Count > 0)
        {
            foreach (var gid in assignedGroupIds)
            {
                var key = (gid, CategoryItemType.Group);
                rootKeys.Add(key);
                inTree.Add(key);
                foreach (var d in CategoryGroupWithChildCount.CollectDescendants(gid, hierarchy, includeLeafCategories: true))
                {
                    inTree.Add((d.Id, d.ItemType));
                }
            }
            // Orphan tracked items (those outside any assigned group) shouldn't disappear from
            // the tree — surface them as extra roots so a misconfigured project still shows
            // its budgeted rows.
            foreach (var key in trackedKeys)
            {
                if (inTree.Contains(key)) continue;
                rootKeys.Add(key);
                inTree.Add(key);
                if (key.ItemType == CategoryItemType.Group)
                {
                    foreach (var d in CategoryGroupWithChildCount.CollectDescendants(key.Id, hierarchy, includeLeafCategories: true))
                    {
                        inTree.Add((d.Id, d.ItemType));
                    }
                }
            }
        }
        else
        {
            // No assignments configured — fall back to tracking items themselves as roots so
            // legacy projects keep rendering. Nest tracked leaves under their tracked-group
            // ancestor rather than duplicating them at the top.
            bool HasTrackedAncestor((long Id, CategoryItemType ItemType) key)
            {
                var entry = hierarchy.FirstOrDefault(h => h.Id == key.Id && h.ItemType == key.ItemType);
                if (entry == null) return false;
                var parentId = entry.ParentCategoryGroupId;
                while (parentId.HasValue)
                {
                    if (trackedSet.Contains((parentId.Value, CategoryItemType.Group))) return true;
                    var parentEntry = hierarchy.FirstOrDefault(h => h.Id == parentId.Value && h.ItemType == CategoryItemType.Group);
                    parentId = parentEntry?.ParentCategoryGroupId;
                }
                return false;
            }
            foreach (var key in trackedKeys)
            {
                inTree.Add(key);
                if (key.ItemType != CategoryItemType.Group) continue;
                foreach (var d in CategoryGroupWithChildCount.CollectDescendants(key.Id, hierarchy, includeLeafCategories: true))
                {
                    inTree.Add((d.Id, d.ItemType));
                }
            }
            rootKeys.AddRange(trackedKeys.Where(k => !HasTrackedAncestor(k)));
        }

        // Resolve every node-in-tree to its leaf categories so we can query transactions once.
        var nodeKeysList = inTree.ToList();
        var itemToLeafIds = CategoryHierarchyService.BuildItemToLeafCategoryIds(nodeKeysList, hierarchy);
        var allLeafIds = itemToLeafIds.Values.SelectMany(s => s).Distinct().ToList();

        var range = TimeRange.Between(
            new DateTime(year, fromMonth, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(year, toMonth, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1));

        var perLeafSumCents = new Dictionary<long, long>();
        if (allLeafIds.Count > 0)
        {
            var raw = await db.Transactions
                .Where(t => t.SourceId == project.SourceId)
                .CountedAsFlow()
                .Where(t => t.CategoryId.HasValue && allLeafIds.Contains(t.CategoryId.Value))
                .WithinUtc(range)
                .Select(t => new { CategoryId = t.CategoryId!.Value, t.Amount })
                .ToListAsync(ct);
            foreach (var r in raw)
            {
                perLeafSumCents.TryGetValue(r.CategoryId, out var s);
                perLeafSumCents[r.CategoryId] = s + r.Amount;
            }
        }

        decimal SumForItem((long Id, CategoryItemType ItemType) key, bool isIncome)
        {
            if (!itemToLeafIds.TryGetValue(key, out var leaves)) return 0m;
            long sumCents = 0;
            foreach (var leafId in leaves)
            {
                if (perLeafSumCents.TryGetValue(leafId, out var s)) sumCents += s;
            }
            // Income items: keep the natural positive sign of incoming money. Expense items:
            // The source app stores expenses as negative cents, so we flip so "spent" is positive.
            return isIncome ? sumCents / 100m : -sumCents / 100m;
        }

        decimal BudgetForItem((long Id, CategoryItemType ItemType) key)
        {
            if (!effective.Effective.TryGetValue(key, out var months)) return 0m;
            decimal sum = 0m;
            for (int m = fromMonth; m <= toMonth; m++) sum += months[m - 1];
            return sum;
        }

        bool IsIncomeFor((long Id, CategoryItemType ItemType) key) => incomeItems.Contains(key);

        // Build child-by-parent index over only the nodes in the tree. Sort order matches
        // the source's native ordering: the per-row "sort" column (nulls last), then name.
        // Mixed groups + leaves at the same parent level interleave by that single key, the
        // same way they appear inside the source app.
        static long SortKey(CategoryGroupWithChildCount h)
        {
            long? raw = h.ItemType == CategoryItemType.Category ? h.Category?.Sort : h.CategoryGroup.Sort;
            return raw ?? long.MaxValue;
        }
        var byParent = hierarchy
            .Where(h => inTree.Contains((h.Id, h.ItemType)))
            .GroupBy(h => h.ParentCategoryGroupId)
            .ToDictionary(g => g.Key ?? -1L, g => g.OrderBy(SortKey).ThenBy(h => h.Name, StringComparer.Ordinal).ToList());


        BrowseCategoryNode BuildNode(CategoryGroupWithChildCount entry)
        {
            var key = (entry.Id, entry.ItemType);
            var isIncome = IsIncomeFor(key);
            var spent = SumForItem(key, isIncome);
            decimal? budget = null;
            decimal? remaining = null;
            if (effective.Effective.ContainsKey(key))
            {
                var b = BudgetForItem(key);
                // Only surface budget when there's an actual non-zero amount; an auto-summed
                // zero (group with no tracked descendants carrying amounts) shouldn't read as
                // "budget = €0, remaining = -spent".
                if (b != 0m)
                {
                    budget = b;
                    remaining = b - spent;
                }
            }

            var children = new List<BrowseCategoryNode>();
            if (entry.ItemType == CategoryItemType.Group && byParent.TryGetValue(entry.Id, out var rawChildren))
            {
                foreach (var c in rawChildren) children.Add(BuildNode(c));
            }

            return new BrowseCategoryNode
            {
                Id = entry.Id,
                ItemType = entry.ItemType,
                Name = entry.Name,
                Color = entry.ItemType == CategoryItemType.Category
                    ? entry.Category?.Color
                    : entry.CategoryGroup.Color,
                IsTracked = trackedSet.Contains(key),
                IsIncome = isIncome,
                Spent = spent,
                Budget = budget,
                Remaining = remaining,
                Children = children
            };
        }

        var topEntries = rootKeys
            .Select(k => hierarchy.FirstOrDefault(h => h.Id == k.Id && h.ItemType == k.ItemType))
            .Where(e => e != null)
            .Select(e => e!)
            .ToList();
        var categoryNodes = topEntries
            .OrderBy(SortKey)
            .ThenBy(e => e.Name, StringComparer.Ordinal)
            .Select(BuildNode)
            .ToList();

        // Accounts: project-linked, plus any account that has ever had a transaction in one
        // of the project's tracked categories. Without the latter, a transaction in a tracked
        // category but on an unassigned account would be visible in the tx list yet the rail
        // wouldn't offer a way to filter to that account.
        var assignedAccountIds = await db.ProjectAccountAssignments
            .Where(a => a.ProjectId == projectId)
            .Select(a => a.AccountId)
            .ToListAsync(ct);
        var accountIdSet = new HashSet<long>(assignedAccountIds);
        if (allLeafIds.Count > 0)
        {
            var matchingAccountIds = await db.Transactions
                .Where(t => t.SourceId == project.SourceId)
                .Where(t => t.Splitted != 1)
                .Where(t => t.AccountId.HasValue && t.CategoryId.HasValue && allLeafIds.Contains(t.CategoryId.Value))
                .Select(t => t.AccountId!.Value)
                .Distinct()
                .ToListAsync(ct);
            foreach (var id in matchingAccountIds) accountIdSet.Add(id);
        }
        var accountIds = accountIdSet.ToList();
        var accountNodes = new List<BrowseAccountNode>();
        if (accountIds.Count > 0)
        {
            var accounts = await db.Accounts
                .AsNoTracking()
                .Where(a => accountIds.Contains(a.Id))
                .OrderBy(a => a.Sort)
                .ThenBy(a => a.Name)
                .ToListAsync(ct);

            // Per-account net flow over the period — one query covering all linked accounts.
            var accountPeriodSumsCents = await db.Transactions
                .Where(t => t.SourceId == project.SourceId)
                .Where(t => t.Splitted != 1)
                .Where(t => t.AccountId.HasValue && accountIds.Contains(t.AccountId.Value))
                .WithinUtc(range)
                .GroupBy(t => t.AccountId!.Value)
                .Select(g => new { AccountId = g.Key, Sum = g.Sum(t => t.Amount) })
                .ToDictionaryAsync(x => x.AccountId, x => x.Sum, ct);

            // Depot accounts (type 7 / portfolio flag) report holdings market value as their
            // balance — the packed "balances" blob doesn't reflect securities. One grouped query
            // over all depots in view; non-depot accounts keep their parsed cash balance.
            var depotIds = accounts
                .Where(a => a.Type == 7 || a.Portfolio == 1)
                .Select(a => a.Id)
                .ToList();
            var holdingsValueByAccount = depotIds.Count == 0
                ? new Dictionary<long, double>()
                : await db.Holdings
                    .Where(h => depotIds.Contains(h.AccountId))
                    .GroupBy(h => h.AccountId)
                    .Select(g => new { AccountId = g.Key, Value = g.Sum(h => h.Amount ?? 0) })
                    .ToDictionaryAsync(x => x.AccountId, x => x.Value, ct);

            var assignedAccountSet = new HashSet<long>(assignedAccountIds);

            // Header details. Bank logins are owner-only through the query filter, so a viewer of a
            // shared account gets no link here: no institution name, no sync button, and the account's
            // own refresh timestamp as "last updated".
            var viewerId = _access == null ? null : (await _access.GetAsync()).UserId;
            var links = await db.BankLoginAccounts.AsNoTracking()
                .Include(l => l.BankLogin)
                .Where(l => l.LinkedAccountId != null && accountIds.Contains(l.LinkedAccountId.Value))
                .ToListAsync(ct);
            var linkByAccount = links.GroupBy(l => l.LinkedAccountId!.Value).ToDictionary(g => g.Key, g => g.First());
            var pendingCents = await db.Transactions
                .Where(t => !t.Booked && t.Splitted != 1)
                .Where(t => t.AccountId.HasValue && accountIds.Contains(t.AccountId.Value))
                .GroupBy(t => t.AccountId!.Value)
                .Select(g => new { AccountId = g.Key, Sum = g.Sum(t => t.Amount) })
                .ToDictionaryAsync(x => x.AccountId, x => x.Sum, ct);
            foreach (var a in accounts)
            {
                bool isPortfolio = a.Type == 7 || a.Portfolio == 1;
                decimal? balance = isPortfolio
                    ? (holdingsValueByAccount.TryGetValue(a.Id, out var hv) ? (decimal)hv : a.CurrentBalance)
                    : a.CurrentBalance;
                linkByAccount.TryGetValue(a.Id, out var link);
                accountNodes.Add(new BrowseAccountNode
                {
                    GroupId = a.GroupId,
                    Sort = a.Sort,
                    Type = a.Type,
                    Iban = a.Iban,
                    Institution = link?.BankLogin.DisplayName,
                    LastUpdatedUtc = AccountDisplay.SelectLastUpdated(link?.LastSuccessAt, a.RefreshTimestamp),
                    Error = FirstNonEmpty(link?.LastError, link?.BankLogin.LastError, a.Error),
                    PendingBalance = pendingCents.TryGetValue(a.Id, out var pc) && pc != 0 ? pc / 100m : null,
                    SyncLoginId = link != null && viewerId != null && link.BankLogin.OwnerUserId == viewerId ? link.BankLoginId : null,
                    SyncLoginAccountId = link != null && viewerId != null && link.BankLogin.OwnerUserId == viewerId && !link.Closed ? link.Id : null,
                    Closed = link?.Closed ?? false,
                    IsMoneyMoney = string.Equals(a.Provider, AccountProviders.MoneyMoney, StringComparison.Ordinal),
                    MarkedClosed = a.Closed,
                    CanEnterTransactions = string.Equals(a.Provider, AccountProviders.Manual, StringComparison.Ordinal)
                        && (db.Scope == null || db.Scope.OwnsSource(a.SourceId)),
                    Id = a.Id,
                    Name = a.Name ?? "",
                    Balance = balance,
                    Currency = a.Currency,
                    PeriodSum = accountPeriodSumsCents.TryGetValue(a.Id, out var s) ? s / 100m : 0m,
                    IsPortfolio = isPortfolio,
                    IsAssigned = assignedAccountSet.Contains(a.Id),
                    Icon = a.Icon,
                    Color = a.Color,
                });
            }
        }

        var groupSources = accountNodes.Count == 0
            ? []
            : await db.Accounts.AsNoTracking().Where(a => accountIds.Contains(a.Id)).Select(a => a.SourceId).Distinct().ToListAsync(ct);
        var accountGroups = new List<AccountGroupInfo>();
        if (groupSources.Count > 0)
        {
            var rows = await db.AccountGroups.AsNoTracking().Where(g => groupSources.Contains(g.SourceId)).ToListAsync(ct);
            // Groups that are empty in their source (e.g. just created in the sidebar) still show, so they can be filled.
            var filled = (await db.Accounts.AsNoTracking().Where(a => groupSources.Contains(a.SourceId) && a.GroupId != null).Select(a => a.GroupId!.Value).Distinct().ToListAsync(ct))
                .Concat(rows.Where(g => g.ParentGroupId != null).Select(g => g.ParentGroupId!.Value)).ToHashSet();
            // Only the owner sees every account of a source, so only for them "no accounts visible" means empty.
            var scope = _access == null ? null : await _access.GetAsync();
            accountGroups.AddRange(rows.Select(g => new AccountGroupInfo(g.Id, g.ParentGroupId, g.Name ?? "", g.Sort,
                !filled.Contains(g.Id) && (scope == null || scope.OwnsSource(g.SourceId)), g.Icon, g.Color)));
        }

        // only the project's own accounts: another project's account of the same source is that project's to-do
        var projectAccountIds = db.ProjectAccountAssignments.Where(p => p.ProjectId == project.Id).Select(p => (long?)p.AccountId);
        var uncategorizedCount = await db.Transactions
            .Where(t => t.SourceId == project.SourceId && projectAccountIds.Contains(t.AccountId))
            .CountedAsFlow()
            .WhereUncategorized(db, project.SourceId)
            .CountAsync(ct);

        return new BrowseTree
        {
            ProjectId = projectId,
            Year = year,
            FromMonth = fromMonth,
            ToMonth = toMonth,
            Accounts = accountNodes,
            AccountGroups = accountGroups,
            Categories = categoryNodes,
            UncategorizedCount = uncategorizedCount
        };
    }

    private static string? FirstNonEmpty(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}
