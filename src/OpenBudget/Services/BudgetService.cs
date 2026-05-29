using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;

namespace OpenBudget.Services;

public class BudgetService
{
    private readonly IDbContextFactory<BudgetDbContext> _dbFactory;
    private readonly CategoryHierarchyService _hierarchy;
    private readonly ProjectService _projects;
    private readonly BudgetEntryService _budgets;
    private readonly ChangeNotificationService _notifier;
    private readonly ChangeOrigin _origin;

    public BudgetService(
        IDbContextFactory<BudgetDbContext> dbFactory,
        CategoryHierarchyService hierarchy,
        ProjectService projects,
        BudgetEntryService budgets,
        ChangeNotificationService notifier,
        ChangeOrigin origin)
    {
        _dbFactory = dbFactory;
        _hierarchy = hierarchy;
        _projects = projects;
        _budgets = budgets;
        _notifier = notifier;
        _origin = origin;
    }

    // Publish a change stamped with this circuit's origin so our own components skip the echo.
    private void Notify(DataChange change) => _notifier.Publish(change, _origin.Id);

    /// <summary>
    /// Spending summary for a project across a month range [fromMonth, toMonth] within a year.
    /// Tracked items come from the project's live ProjectTrackingItems so changes show up
    /// without re-running the budget wizard. Budget amounts are read from the active Budget
    /// for the year if one exists; otherwise the per-row Budget is 0 and only Spent is
    /// meaningful. Pass fromMonth == toMonth for a single month.
    /// </summary>
    public async Task<ProjectSpendingSummary> GetSpendingSummaryAsync(int projectId, int year, int fromMonth, int toMonth)
    {
        if (fromMonth < 1 || fromMonth > 12) throw new ArgumentOutOfRangeException(nameof(fromMonth));
        if (toMonth < 1 || toMonth > 12) throw new ArgumentOutOfRangeException(nameof(toMonth));
        if (toMonth < fromMonth) throw new ArgumentException("toMonth must be >= fromMonth", nameof(toMonth));

        await using var db = await _dbFactory.CreateDbContextAsync();

        var project = await _projects.GetProjectAsync(projectId);
        if (project == null)
            throw new ArgumentException("Project not found", nameof(projectId));

        var budgets = await _budgets.GetBudgetsForProjectAsync(projectId);
        // Prefer the budget the user explicitly marked active; fall back to the most recently
        // modified one for the year if nothing is active (legacy data, edge cases).
        var activeBudget = budgets.FirstOrDefault(b => b.Year == year && b.IsActive)
            ?? budgets.FirstOrDefault(b => b.Year == year);

        // Resolve tracked items from the project's live tracking config.
        var trackedKeys = project.TrackingItems.ToTrackedKeys().ToList();
        var trackedKeySet = trackedKeys.ToHashSet();

        // Per-month stored amounts. Seed every tracked group missing an entry with zero so
        // ComputeEffectiveAmounts can auto-sum from its descendants — that's how a tracked
        // group's row shows a meaningful budget when its amounts live on its sub-cats (the
        // post-normalization invariant: budgets carry on one level per subtree).
        var storedAmounts = activeBudget != null
            ? activeBudget.MonthlyEntries.ToDictionary(
                e => ((long Id, CategoryItemType ItemType))(e.CategoryId, e.ItemType),
                e => new[]
                {
                    e.Month1, e.Month2, e.Month3, e.Month4, e.Month5, e.Month6,
                    e.Month7, e.Month8, e.Month9, e.Month10, e.Month11, e.Month12
                })
            : new Dictionary<(long Id, CategoryItemType ItemType), decimal[]>();
        foreach (var key in trackedKeys.Where(k => k.ItemType == CategoryItemType.Group))
        {
            if (!storedAmounts.ContainsKey(key))
                storedAmounts[key] = new decimal[12];
        }

        var allCategoriesWithCounts = await _hierarchy.GetAllCategoriesWithChildCountsAsync(project.SourceId);
        var effective = BudgetEntryService.ComputeEffectiveAmounts(storedAmounts, allCategoriesWithCounts);

        var budgetAmounts = effective.Effective.ToDictionary(
            kvp => kvp.Key,
            kvp =>
            {
                decimal sum = 0m;
                for (int m = fromMonth; m <= toMonth; m++) sum += kvp.Value[m - 1];
                return sum;
            });

        // ieBatch gives each tracked row its natural total (leaf sets overlap, so a Group's
        // numbers include any tracked sub-cat under it). That's what we want for per-row display.
        // Aggregate totals (MonthIncome/MonthExpenses) come from a separate leaf-union pass so
        // they don't double-count across overlapping rows.
        var ieBatch = await GetMonthlyIncomeAndExpenseBatchAsync(trackedKeys, year, project.SourceId, projectId);
        var incomeItemsSet = await GetIncomeItemsForProjectAsync(projectId);

        var groupNameLookup = allCategoriesWithCounts
            .Where(c => c.ItemType == CategoryItemType.Group)
            .ToDictionary(c => c.Id, c => c.CategoryGroup.Name ?? "Unknown");
        var leafCategoryNameLookup = (await db.Categories.AsNoTracking().Where(c => c.SourceId == project.SourceId).ToListAsync())
            .ToDictionary(c => c.Id, c => c.Name ?? "Unknown");

        var (monthIncome, monthExpenses) = await AggregateIncomeExpenseForRangeAsync(
            trackedKeys, year, fromMonth, toMonth, project.SourceId, projectId);

        // When a tracked parent has tracked descendants, both rows would otherwise display
        // the same money — the parent's per-row Spent is the leaf union, which already
        // includes any tracked sub-cat. Collapse to the most specific level by hiding the
        // parent. The tracking list itself is unchanged; this is just a display rule.
        var shadowedParents = new HashSet<(long Id, CategoryItemType ItemType)>();
        foreach (var key in trackedKeys.Where(k => k.ItemType == CategoryItemType.Group))
        {
            var descendants = CategoryGroupWithChildCount.CollectDescendants(
                key.Id, allCategoriesWithCounts, includeLeafCategories: true);
            if (descendants.Any(d => trackedKeySet.Contains((d.Id, d.ItemType))))
                shadowedParents.Add(key);
        }

        // Resolve the visible rows first (drop shadowed parents and any key whose name no longer
        // resolves), then disambiguate names across that set: two tracked leaves that share a bare
        // name — e.g. an umlagefähig vs. nicht-umlagefähig "Reparaturen" — read as "Parent › Name".
        var entryByKey = allCategoriesWithCounts.ToDictionary(c => (c.Id, c.ItemType));
        var visible = new List<((long Id, CategoryItemType ItemType) Key, string BaseName, string? ParentName)>();
        foreach (var key in trackedKeys)
        {
            if (shadowedParents.Contains(key)) continue;

            string? name = key.ItemType == CategoryItemType.Group
                ? groupNameLookup.GetValueOrDefault(key.Id)
                : leafCategoryNameLookup.GetValueOrDefault(key.Id);
            if (name == null) continue;

            string? parentName = entryByKey.TryGetValue(key, out var entry) && entry.ParentCategoryGroupId.HasValue
                ? groupNameLookup.GetValueOrDefault(entry.ParentCategoryGroupId.Value)
                : null;
            visible.Add((key, name, parentName));
        }
        var displayNames = CategoryDisplay.Disambiguate(visible);

        var categorySpending = new List<CategorySpending>();
        foreach (var (key, _, _) in visible)
        {
            bool isIncome = incomeItemsSet.Contains(key);
            decimal spent = 0m;
            if (ieBatch.TryGetValue(key, out var ie))
            {
                for (int m = fromMonth; m <= toMonth; m++)
                {
                    var idx = m - 1;
                    spent += isIncome ? ie.Income[idx] : ie.Expenses[idx];
                }
            }

            var budgetAmount = budgetAmounts.GetValueOrDefault(key, 0m);
            categorySpending.Add(new CategorySpending
            {
                CategoryId = key.Id,
                ItemType = key.ItemType,
                CategoryName = displayNames[key],
                IsIncome = isIncome,
                Budget = budgetAmount,
                Spent = spent,
                Remaining = budgetAmount - spent
            });
        }

        // Budget total: sum the user's actual saved entries (storedAmounts) directly. Using
        // categorySpending.Budget would inherit effective.Effective's auto-sum, which replaces
        // a tracked parent's own stored amount with the descendant sum — so anything the user
        // typed on the parent alongside children would silently disappear from the total.
        // No double-counting risk: each MonthlyBudgetEntry is its own key, summed once.
        // TotalSpent / MonthExpenses still come from the leaf-union aggregate so they don't
        // double-count across a tracked group + tracked sub-cat on the actuals side.
        var totalBudget = 0m;
        foreach (var (key, months) in storedAmounts)
        {
            if (incomeItemsSet.Contains(key)) continue;
            for (int m = fromMonth; m <= toMonth; m++) totalBudget += months[m - 1];
        }

        return new ProjectSpendingSummary
        {
            ProjectId = projectId,
            SourceId = project.SourceId,
            ProjectName = project.Name,
            Year = year,
            FromMonth = fromMonth,
            ToMonth = toMonth,
            CategorySpending = categorySpending,
            TotalBudget = totalBudget,
            TotalSpent = monthExpenses,
            TotalRemaining = totalBudget - monthExpenses,
            HasBudget = activeBudget != null,
            MonthIncome = monthIncome,
            MonthExpenses = monthExpenses
        };
    }

    /// <summary>
    /// Returns leaf category IDs that (a) sit underneath any of the given ancestor groups
    /// (recursive) and (b) have at least one transaction in the trailing 12 months ending at
    /// <paramref name="endUtc"/> (defaults to now). Used by BudgetEdit to surface "in-use"
    /// sub-categories under a tracked group as shadow rows the user can promote to real
    /// budget rows just by typing an amount. The trailing-window lookback works on Jan 1
    /// (when the budget year has no transactions yet) and stays stable mid-year.
    /// </summary>
    public async Task<HashSet<long>> GetLeafCategoriesWithRecentTransactionsAsync(
        IEnumerable<long> ancestorGroupIds, int sourceId, DateTime? endUtc = null)
    {
        var ancestorList = ancestorGroupIds.Distinct().ToList();
        if (ancestorList.Count == 0) return new HashSet<long>();

        await using var db = await _dbFactory.CreateDbContextAsync();
        var hierarchy = await _hierarchy.GetAllCategoriesWithChildCountsAsync(sourceId);
        var candidateLeaves = new HashSet<long>();
        foreach (var groupId in ancestorList)
        {
            var descendants = CategoryGroupWithChildCount.CollectDescendants(
                groupId, hierarchy, includeLeafCategories: true);
            foreach (var d in descendants)
                if (d.ItemType == CategoryItemType.Category) candidateLeaves.Add(d.Id);
        }
        if (candidateLeaves.Count == 0) return new HashSet<long>();

        var end = endUtc ?? DateTime.UtcNow;
        var range = TimeRange.Between(end.AddYears(-1), end);

        var leafList = candidateLeaves.ToList();
        var hits = await db.Transactions
            .Where(t => t.SourceId == sourceId)
            .Where(t => t.CategoryId.HasValue && leafList.Contains(t.CategoryId.Value))
            .WithinUtc(range)
            .Select(t => t.CategoryId!.Value)
            .Distinct()
            .ToListAsync();

        return hits.ToHashSet();
    }

    /// <summary>
    /// Shared by the income/expense aggregation methods: expands each tracked item to its leaf
    /// categories, unions them (so a tracked group + tracked sub-cat under it don't double-count),
    /// and classifies each leaf as income or expense. Classification is project-aware when
    /// <paramref name="projectId"/> is given (type assignments + ancestor inheritance), else it
    /// falls back to MoneyMoney's native Type==2 marker. Reads on <paramref name="db"/> so the
    /// caller's single short-lived context is reused.
    /// </summary>
    private async Task<(Dictionary<(long, CategoryItemType), HashSet<long>> ItemToLeafIds,
                        List<long> AllLeafIds,
                        Dictionary<long, bool> LeafIsIncome)>
        ResolveLeavesAndIncomeAsync(
            BudgetDbContext db,
            IReadOnlyCollection<(long Id, CategoryItemType ItemType)> items,
            int sourceId,
            int? projectId)
    {
        var allCategoriesWithCounts = await _hierarchy.GetAllCategoriesWithChildCountsAsync(sourceId);
        var itemToLeafIds = CategoryHierarchyService.BuildItemToLeafCategoryIds(items, allCategoriesWithCounts);
        var allLeafIds = itemToLeafIds.Values.SelectMany(s => s).Distinct().ToList();

        Dictionary<long, bool> leafIsIncome;
        if (allLeafIds.Count == 0)
        {
            leafIsIncome = new Dictionary<long, bool>();
        }
        else if (projectId.HasValue)
        {
            var typeAssignments = await db.CategoryTypeAssignments
                .AsNoTracking()
                .Where(cta => cta.ProjectId == projectId.Value)
                .ToListAsync();
            var assignmentLookup = typeAssignments.ToDictionary(
                ta => (ta.CategoryId, ta.ItemType),
                ta => ta.IsIncome);
            leafIsIncome = allLeafIds.ToDictionary(
                id => id,
                id => ResolveCategoryTypeWithInheritance(id, CategoryItemType.Category, allCategoriesWithCounts, assignmentLookup));
        }
        else
        {
            var types = await db.Categories
                .Where(c => c.SourceId == sourceId && allLeafIds.Contains(c.Id))
                .Select(c => new { c.Id, c.Type })
                .ToListAsync();
            leafIsIncome = types.ToDictionary(c => c.Id, c => c.Type == 2);
        }

        return (itemToLeafIds, allLeafIds, leafIsIncome);
    }

    /// <summary>
    /// Computes the net income/expense aggregate over a month range for a set of tracked items,
    /// deduplicated via the leaf-category union so a tracked group + tracked sub-cat under it
    /// don't double-count. Per-leaf classification (project-aware if projectId given) routes
    /// each leaf's transaction sum into income or expense.
    /// </summary>
    private async Task<(decimal Income, decimal Expense)> AggregateIncomeExpenseForRangeAsync(
        IReadOnlyCollection<(long Id, CategoryItemType ItemType)> items,
        int year, int fromMonth, int toMonth, int sourceId, int? projectId)
    {
        if (items.Count == 0) return (0m, 0m);

        await using var db = await _dbFactory.CreateDbContextAsync();
        var (_, allLeafIds, leafIsIncome) = await ResolveLeavesAndIncomeAsync(db, items, sourceId, projectId);
        if (allLeafIds.Count == 0) return (0m, 0m);

        var range = TimeRange.Between(
            new DateTime(year, fromMonth, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(year, toMonth, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1));

        var raw = await db.Transactions
            .Where(t => t.SourceId == sourceId)
            .Where(t => t.CategoryId.HasValue && allLeafIds.Contains(t.CategoryId.Value))
            .WithinUtc(range)
            .Select(t => new { CategoryId = t.CategoryId!.Value, t.Amount })
            .ToListAsync();

        long incomeCents = 0, expenseCents = 0;
        foreach (var t in raw)
        {
            if (leafIsIncome.GetValueOrDefault(t.CategoryId, false)) incomeCents += t.Amount;
            else expenseCents += -t.Amount;
        }
        return (incomeCents / 100m, expenseCents / 100m);
    }

    /// <summary>
    /// Get category type assignments for a project
    /// </summary>
    public async Task<List<CategoryTypeAssignment>> GetCategoryTypeAssignmentsAsync(int projectId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.CategoryTypeAssignments
            .AsNoTracking()
            .Where(cta => cta.ProjectId == projectId)
            .ToListAsync();
    }

    /// <summary>
    /// Returns the set of items (groups and categories) that resolve as Income for a project,
    /// taking inheritance from ancestor groups into account.
    /// </summary>
    public async Task<HashSet<(long Id, CategoryItemType ItemType)>> GetIncomeItemsForProjectAsync(int projectId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var typeAssignments = await db.CategoryTypeAssignments
            .AsNoTracking()
            .Where(cta => cta.ProjectId == projectId)
            .ToListAsync();

        var result = new HashSet<(long, CategoryItemType)>();
        if (!typeAssignments.Any())
            return result;

        var assignmentLookup = typeAssignments.ToDictionary(
            ta => (ta.CategoryId, ta.ItemType),
            ta => ta.IsIncome);

        var sourceId = await _projects.GetSourceIdForProjectAsync(projectId);
        var allCategories = await _hierarchy.GetAllCategoriesWithChildCountsAsync(sourceId);

        foreach (var item in allCategories)
        {
            if (ResolveCategoryTypeWithInheritance(item.Id, item.ItemType, allCategories, assignmentLookup))
            {
                result.Add((item.Id, item.ItemType));
            }
        }

        return result;
    }

    /// <summary>
    /// Set whether a category is income or expense for a project
    /// </summary>
    public async Task SetCategoryTypeAsync(int projectId, long categoryId, CategoryItemType itemType, bool isIncome)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var existing = await db.CategoryTypeAssignments
            .FirstOrDefaultAsync(cta => cta.ProjectId == projectId && cta.CategoryId == categoryId && cta.ItemType == itemType);

        if (existing != null)
        {
            existing.IsIncome = isIncome;
        }
        else
        {
            db.CategoryTypeAssignments.Add(new CategoryTypeAssignment
            {
                ProjectId = projectId,
                CategoryId = categoryId,
                ItemType = itemType,
                IsIncome = isIncome
            });
        }

        await db.SaveChangesAsync();
        Notify(new DataChange(DataArea.Projects, ProjectId: projectId));
    }

    /// <summary>
    /// Delete a category type assignment
    /// </summary>
    public async Task DeleteCategoryTypeAsync(int projectId, long categoryId, CategoryItemType itemType)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var existing = await db.CategoryTypeAssignments
            .FirstOrDefaultAsync(cta => cta.ProjectId == projectId && cta.CategoryId == categoryId && cta.ItemType == itemType);

        if (existing != null)
        {
            db.CategoryTypeAssignments.Remove(existing);
            await db.SaveChangesAsync();
            Notify(new DataChange(DataArea.Projects, ProjectId: projectId));
        }
    }

    /// <summary>
    /// Resolves a category's income/expense type with inheritance support.
    /// Checks for explicit assignment first, then walks up parent chain for inherited assignments.
    /// Returns true if category is income (explicit or inherited), false otherwise (default: expense).
    /// </summary>
    private bool ResolveCategoryTypeWithInheritance(
        long categoryId,
        CategoryItemType itemType,
        List<CategoryGroupWithChildCount> allCategories,
        Dictionary<(long Id, CategoryItemType ItemType), bool> assignmentLookup)
    {
        // Check for explicit assignment on this specific item
        if (assignmentLookup.TryGetValue((categoryId, itemType), out bool isIncome))
        {
            return isIncome;
        }

        // For Categories (leaf nodes), check inheritance from parent CategoryGroup
        if (itemType == CategoryItemType.Category)
        {
            var category = allCategories.FirstOrDefault(c => c.ItemType == CategoryItemType.Category && c.Id == categoryId);
            if (category != null && category.ParentCategoryGroupId.HasValue)
            {
                // Check parent CategoryGroup (recursive to support multi-level inheritance)
                return ResolveCategoryTypeWithInheritance(
                    category.ParentCategoryGroupId.Value,
                    CategoryItemType.Group,
                    allCategories,
                    assignmentLookup);
            }
        }
        // For CategoryGroups, walk up the parent chain
        else if (itemType == CategoryItemType.Group)
        {
            var categoryGroup = allCategories.FirstOrDefault(c => c.ItemType == CategoryItemType.Group && c.Id == categoryId);
            if (categoryGroup != null && categoryGroup.ParentCategoryGroupId.HasValue)
            {
                // Check parent CategoryGroup
                if (assignmentLookup.TryGetValue((categoryGroup.ParentCategoryGroupId.Value, CategoryItemType.Group), out bool parentIsIncome))
                {
                    return parentIsIncome;
                }

                // Recursively check ancestors
                return ResolveCategoryTypeWithInheritance(
                    categoryGroup.ParentCategoryGroupId.Value,
                    CategoryItemType.Group,
                    allCategories,
                    assignmentLookup);
            }
        }

        // Default to expense if no assignment found
        return false;
    }

    /// <summary>
    /// Loads category hierarchy and type assignments once, then performs a single transactions
    /// query for the entire year covering every tracked item's leaf categories. Replaces an
    /// N×24-query pattern with one query.
    /// </summary>
    public async Task<Dictionary<(long Id, CategoryItemType ItemType), (decimal[] Income, decimal[] Expenses)>>
        GetMonthlyIncomeAndExpenseBatchAsync(
            IReadOnlyCollection<(long Id, CategoryItemType ItemType)> items,
            int year,
            int sourceId,
            int? projectId)
    {
        var result = new Dictionary<(long, CategoryItemType), (decimal[], decimal[])>();
        foreach (var item in items)
            result[item] = (new decimal[12], new decimal[12]);

        if (!items.Any()) return result;

        await using var db = await _dbFactory.CreateDbContextAsync();
        var (itemToLeafIds, allLeafIds, leafIsIncome) = await ResolveLeavesAndIncomeAsync(db, items, sourceId, projectId);
        if (!allLeafIds.Any()) return result;

        var raw = await db.Transactions
            .Where(t => t.SourceId == sourceId)
            .Where(t => t.CategoryId.HasValue && allLeafIds.Contains(t.CategoryId.Value))
            .WithinUtc(TimeRange.Year(year))
            .Select(t => new { CategoryId = t.CategoryId!.Value, t.Amount, t.Timestamp })
            .ToListAsync();

        var perCategoryMonthly = new Dictionary<long, decimal[]>();
        foreach (var t in raw)
        {
            var month = DateTimeOffset.FromUnixTimeSeconds(t.Timestamp).UtcDateTime.Month - 1;
            if (!perCategoryMonthly.TryGetValue(t.CategoryId, out var arr))
            {
                arr = new decimal[12];
                perCategoryMonthly[t.CategoryId] = arr;
            }
            arr[month] += t.Amount / 100m;
        }

        foreach (var item in items)
        {
            var (income, expenses) = result[item];
            foreach (var leafId in itemToLeafIds[item])
            {
                if (!perCategoryMonthly.TryGetValue(leafId, out var monthly)) continue;
                bool isIncome = leafIsIncome.GetValueOrDefault(leafId, false);
                for (int m = 0; m < 12; m++)
                {
                    if (isIncome) income[m] += monthly[m];
                    else expenses[m] += -monthly[m];
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Returns income totals per tracked item for a single calendar month. Used by the
    /// Cashflow page's "Last month repeated" projection mode to flatten last-completed-month's
    /// income forward across the remaining months.
    /// Mirrors GetMonthlyIncomeAndExpenseBatchAsync's leaf-resolution + project-aware income
    /// classification, but scopes the transaction query to one month and returns income only.
    /// </summary>
    public async Task<Dictionary<(long Id, CategoryItemType ItemType), decimal>>
        GetMonthlyIncomePerItemAsync(
            IReadOnlyCollection<(long Id, CategoryItemType ItemType)> items,
            int sourceId,
            int? projectId,
            int year,
            int month)
    {
        var result = new Dictionary<(long, CategoryItemType), decimal>();
        foreach (var item in items) result[item] = 0m;
        if (!items.Any()) return result;

        await using var db = await _dbFactory.CreateDbContextAsync();
        var (itemToLeafIds, allLeafIds, leafIsIncome) = await ResolveLeavesAndIncomeAsync(db, items, sourceId, projectId);
        if (!allLeafIds.Any()) return result;

        var raw = await db.Transactions
            .Where(t => t.SourceId == sourceId)
            .Where(t => t.CategoryId.HasValue && allLeafIds.Contains(t.CategoryId.Value))
            .WithinUtc(TimeRange.Month(year, month))
            .Select(t => new { CategoryId = t.CategoryId!.Value, t.Amount })
            .ToListAsync();

        var perCategorySum = new Dictionary<long, decimal>();
        foreach (var t in raw)
        {
            perCategorySum.TryGetValue(t.CategoryId, out var s);
            perCategorySum[t.CategoryId] = s + t.Amount / 100m;
        }

        foreach (var item in items)
        {
            decimal sum = 0m;
            foreach (var leafId in itemToLeafIds[item])
            {
                if (!perCategorySum.TryGetValue(leafId, out var s)) continue;
                if (!leafIsIncome.GetValueOrDefault(leafId, false)) continue;
                sum += s; // income txns have positive amounts in MoneyMoney
            }
            result[item] = sum;
        }
        return result;
    }

    /// <summary>
    /// Returns the calendar years that have at least one transaction for a given source,
    /// newest first. Min/max of the timestamp column rather than a DISTINCT year scan — gap
    /// years (rare) are still returned for UI continuity.
    /// </summary>
    public async Task<List<int>> GetYearsWithTransactionsAsync(int sourceId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var minTs = await db.Transactions
            .Where(t => t.SourceId == sourceId)
            .MinAsync(t => (long?)t.Timestamp);
        var maxTs = await db.Transactions
            .Where(t => t.SourceId == sourceId)
            .MaxAsync(t => (long?)t.Timestamp);
        return YearsBetween(minTs, maxTs);
    }

    /// <summary>
    /// Same as <see cref="GetYearsWithTransactionsAsync"/> but restricted to the leaves of a
    /// project's tracking items — so the year picker on a project page doesn't offer years
    /// where the tracked categories don't actually have any data.
    /// </summary>
    public async Task<List<int>> GetYearsWithTransactionsForProjectAsync(int projectId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var project = await _projects.GetProjectAsync(projectId);
        if (project == null) return new List<int>();

        var trackedKeys = project.TrackingItems.ToTrackedKeys().ToList();
        if (!trackedKeys.Any()) return new List<int>();

        var allCategoriesWithCounts = await _hierarchy.GetAllCategoriesWithChildCountsAsync(project.SourceId);
        var itemToLeafIds = CategoryHierarchyService.BuildItemToLeafCategoryIds(trackedKeys, allCategoriesWithCounts);
        var allLeafIds = itemToLeafIds.Values.SelectMany(s => s).Distinct().ToList();
        if (!allLeafIds.Any()) return new List<int>();

        var minTs = await db.Transactions
            .Where(t => t.SourceId == project.SourceId)
            .Where(t => t.CategoryId.HasValue && allLeafIds.Contains(t.CategoryId.Value))
            .MinAsync(t => (long?)t.Timestamp);
        var maxTs = await db.Transactions
            .Where(t => t.SourceId == project.SourceId)
            .Where(t => t.CategoryId.HasValue && allLeafIds.Contains(t.CategoryId.Value))
            .MaxAsync(t => (long?)t.Timestamp);
        return YearsBetween(minTs, maxTs);
    }

    private static List<int> YearsBetween(long? minTs, long? maxTs)
    {
        if (!minTs.HasValue || !maxTs.HasValue) return new List<int>();
        var minYear = DateTimeOffset.FromUnixTimeSeconds(minTs.Value).UtcDateTime.Year;
        var maxYear = DateTimeOffset.FromUnixTimeSeconds(maxTs.Value).UtcDateTime.Year;
        var years = new List<int>();
        for (int y = maxYear; y >= minYear; y--)
            years.Add(y);
        return years;
    }

    #region Project Account Assignments

    /// <summary>
    /// Returns all MoneyMoney accounts for a source ordered by MoneyMoney sort field, then name.
    /// Used by the project Accounts card and the assignment picker.
    /// </summary>
    public async Task<List<Account>> GetAllAccountsAsync(int sourceId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.Accounts
            .AsNoTracking()
            .Where(a => a.SourceId == sourceId)
            .OrderBy(a => a.Sort)
            .ThenBy(a => a.Name)
            .ToListAsync();
    }

    /// <summary>
    /// Returns the MoneyMoney account groups for a source, ordered by sort then name. The
    /// Add-Account picker turns this list (plus the accounts) into a tree.
    /// </summary>
    public async Task<List<AccountGroup>> GetAllAccountGroupsAsync(int sourceId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.AccountGroups
            .AsNoTracking()
            .Where(g => g.SourceId == sourceId)
            .OrderBy(g => g.Sort)
            .ThenBy(g => g.Name)
            .ToListAsync();
    }

    public async Task<List<ProjectAccountAssignment>> GetProjectAccountAssignmentsAsync(int projectId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.ProjectAccountAssignments
            .AsNoTracking()
            .Where(paa => paa.ProjectId == projectId)
            .ToListAsync();
    }

    public async Task AssignAccountToProjectAsync(int projectId, long accountId, AccountScope scope)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var existing = await db.ProjectAccountAssignments
            .FirstOrDefaultAsync(paa => paa.ProjectId == projectId && paa.AccountId == accountId);
        if (existing != null)
        {
            existing.Scope = scope;
        }
        else
        {
            db.ProjectAccountAssignments.Add(new ProjectAccountAssignment
            {
                ProjectId = projectId,
                AccountId = accountId,
                Scope = scope
            });
        }
        await db.SaveChangesAsync();
        Notify(new DataChange(DataArea.Projects, ProjectId: projectId));
    }

    public async Task SetAccountScopeAsync(int projectId, long accountId, AccountScope scope)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var existing = await db.ProjectAccountAssignments
            .FirstOrDefaultAsync(paa => paa.ProjectId == projectId && paa.AccountId == accountId);
        if (existing == null) return;
        existing.Scope = scope;
        await db.SaveChangesAsync();
        Notify(new DataChange(DataArea.Projects, ProjectId: projectId));
    }

    public async Task RemoveAccountFromProjectAsync(int projectId, long accountId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var existing = await db.ProjectAccountAssignments
            .FirstOrDefaultAsync(paa => paa.ProjectId == projectId && paa.AccountId == accountId);
        if (existing == null) return;
        db.ProjectAccountAssignments.Remove(existing);
        await db.SaveChangesAsync();
        Notify(new DataChange(DataArea.Projects, ProjectId: projectId));
    }

    #endregion
}

public enum CategoryItemType
{
    Group,
    Category
}

public class CategoryGroupWithChildCount
{
    public CategoryGroup CategoryGroup { get; set; } = null!;
    public Category? Category { get; set; }
    public CategoryItemType ItemType { get; set; }
    public int DirectChildCount { get; set; }
    public bool IsRootCategory { get; set; }
    public long? ParentCategoryGroupId { get; set; }

    // Unified ID property
    public long Id => ItemType == CategoryItemType.Category ? (Category?.Id ?? 0) : CategoryGroup.Id;

    // Unified Name property
    public string Name => ItemType == CategoryItemType.Category ? (Category?.Name ?? "") : (CategoryGroup.Name ?? "");

    /// <summary>
    /// Checks if a category item is a descendant of a given ancestor category ID.
    /// Handles ID collision by properly filtering CategoryGroups vs Categories.
    /// </summary>
    public static bool IsDescendantOf(CategoryGroupWithChildCount item, long ancestorId, List<CategoryGroupWithChildCount> allCategories)
    {
        var current = item;
        while (current != null && current.ParentCategoryGroupId.HasValue)
        {
            if (current.ParentCategoryGroupId.Value == ancestorId)
                return true;

            // Move up to parent (must be a Group since only Groups can be parents)
            current = allCategories.FirstOrDefault(c =>
                c.ItemType == CategoryItemType.Group &&
                c.Id == current.ParentCategoryGroupId.Value);
        }
        return false;
    }

    /// <summary>
    /// Recursively collects all descendant IDs from a starting category.
    /// Handles ID collision by properly filtering CategoryGroups vs Categories.
    /// </summary>
    public static void CollectDescendantIds(
        long categoryId,
        List<CategoryGroupWithChildCount> allCategories,
        HashSet<long> result,
        bool includeLeafCategories = true)
    {
        // Find all children of this category
        var children = allCategories.Where(c => c.ParentCategoryGroupId == categoryId).ToList();

        foreach (var child in children)
        {
            // Only CategoryGroups can have children (Categories are leaf nodes)
            bool isGroup = child.ItemType == CategoryItemType.Group;

            // Add to result if: it's a Group OR we're including leaf categories
            if (isGroup || includeLeafCategories)
            {
                result.Add(child.Id);
            }

            // Only recurse into CategoryGroups (Categories can't have children)
            if (isGroup)
            {
                CollectDescendantIds(child.Id, allCategories, result, includeLeafCategories);
            }
        }
    }

    /// <summary>
    /// Collects all descendants (both CategoryGroups and Categories) of a given category.
    /// Returns the actual CategoryGroupWithChildCount objects, not just IDs.
    /// This avoids ID collision issues when filtering.
    /// </summary>
    public static HashSet<CategoryGroupWithChildCount> CollectDescendants(
        long categoryId,
        List<CategoryGroupWithChildCount> allCategories,
        bool includeLeafCategories = true)
    {
        var result = new HashSet<CategoryGroupWithChildCount>();
        CollectDescendantsRecursive(categoryId, allCategories, result, includeLeafCategories);
        return result;
    }

    private static void CollectDescendantsRecursive(
        long categoryId,
        List<CategoryGroupWithChildCount> allCategories,
        HashSet<CategoryGroupWithChildCount> result,
        bool includeLeafCategories)
    {
        // Find all children of this category
        var children = allCategories.Where(c => c.ParentCategoryGroupId == categoryId).ToList();

        foreach (var child in children)
        {
            // Only CategoryGroups can have children (Categories are leaf nodes)
            bool isGroup = child.ItemType == CategoryItemType.Group;

            // Add to result if: it's a Group OR we're including leaf categories
            if (isGroup || includeLeafCategories)
            {
                result.Add(child);
            }

            // Only recurse into CategoryGroups (Categories can't have children)
            if (isGroup)
            {
                CollectDescendantsRecursive(child.Id, allCategories, result, includeLeafCategories);
            }
        }
    }
}

