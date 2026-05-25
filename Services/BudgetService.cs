using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;

namespace OpenBudget.Services;

public class BudgetService
{
    private readonly BudgetDbContext _budgetDb;

    public BudgetService(BudgetDbContext budgetDb)
    {
        _budgetDb = budgetDb;
    }

    /// <summary>
    /// Get all budgets
    /// </summary>
    public async Task<List<Project>> GetProjectsAsync()
    {
        return await _budgetDb.Projects
            .Include(b => b.CategoryAssignments)
            .OrderBy(b => b.SortOrder)
            .ThenByDescending(b => b.CreatedAt)
            .ToListAsync();
    }

    /// <summary>
    /// Get a specific budget with its assignments
    /// </summary>
    public async Task<Project?> GetProjectAsync(int projectId)
    {
        return await _budgetDb.Projects
            .Include(b => b.CategoryAssignments)
            .Include(b => b.TrackingItems)
            .Include(b => b.CategoryTypeAssignments)
            .FirstOrDefaultAsync(b => b.Id == projectId);
    }

    /// <summary>
    /// Create a new budget bound to a specific MoneyMoney source.
    /// </summary>
    public async Task<Project> CreateProjectAsync(string name, int sourceId, string? description = null)
    {
        var maxSortOrder = await _budgetDb.Projects.AnyAsync()
            ? await _budgetDb.Projects.MaxAsync(p => p.SortOrder)
            : -1;

        var project = new Project
        {
            Name = name,
            Description = description,
            CreatedAt = DateTime.UtcNow,
            SortOrder = maxSortOrder + 1,
            SourceId = sourceId
        };

        _budgetDb.Projects.Add(project);
        await _budgetDb.SaveChangesAsync();

        return project;
    }

    /// <summary>
    /// Move a project up (direction = -1) or down (+1) in the dashboard order by swapping
    /// SortOrder with its immediate neighbor. No-op when already at the edge.
    /// </summary>
    public async Task MoveProjectAsync(int projectId, int direction)
    {
        if (direction != -1 && direction != 1)
            throw new ArgumentException("direction must be -1 or 1", nameof(direction));

        var ordered = await _budgetDb.Projects
            .OrderBy(p => p.SortOrder)
            .ThenByDescending(p => p.CreatedAt)
            .ToListAsync();

        var index = ordered.FindIndex(p => p.Id == projectId);
        if (index < 0) return;

        var swapIndex = index + direction;
        if (swapIndex < 0 || swapIndex >= ordered.Count) return;

        // Renumber so the swap is unambiguous even if any rows shared a SortOrder before.
        for (int i = 0; i < ordered.Count; i++)
            ordered[i].SortOrder = i;
        (ordered[index].SortOrder, ordered[swapIndex].SortOrder) =
            (ordered[swapIndex].SortOrder, ordered[index].SortOrder);

        await _budgetDb.SaveChangesAsync();
    }

    /// <summary>
    /// Delete a budget
    /// </summary>
    public async Task DeleteProjectAsync(int projectId)
    {
        var project = await _budgetDb.Projects.FindAsync(projectId);
        if (project != null)
        {
            _budgetDb.Projects.Remove(project);
            await _budgetDb.SaveChangesAsync();
        }
    }

    /// <summary>
    /// Immediate children (one level) of a project, ordered by SortOrder. Used by the
    /// Cashflow page to roll sub-projects' tracked flows into the parent's view.
    /// </summary>
    public async Task<List<Project>> GetChildProjectsAsync(int parentId)
    {
        return await _budgetDb.Projects
            .Where(p => p.ParentProjectId == parentId)
            .OrderBy(p => p.SortOrder)
            .ThenByDescending(p => p.CreatedAt)
            .ToListAsync();
    }

    /// <summary>
    /// Set or clear a project's parent. Rejected when: parent is the project itself,
    /// parent belongs to a different MoneyMoney source, parent is one of this project's
    /// own descendants (cycle), or parent already has its own parent (we only support
    /// single-level hierarchies for now). Returns the resolved Project on success.
    /// </summary>
    public async Task<Project> SetProjectParentAsync(int projectId, int? parentId)
    {
        var project = await _budgetDb.Projects.FindAsync(projectId)
            ?? throw new ArgumentException("Project not found", nameof(projectId));

        if (parentId == null)
        {
            project.ParentProjectId = null;
            await _budgetDb.SaveChangesAsync();
            return project;
        }

        if (parentId.Value == projectId)
            throw new InvalidOperationException("A project cannot be its own parent.");

        var parent = await _budgetDb.Projects.FindAsync(parentId.Value)
            ?? throw new ArgumentException("Parent project not found", nameof(parentId));

        if (parent.SourceId != project.SourceId)
            throw new InvalidOperationException(
                "Parent and child must share the same MoneyMoney source.");
        if (parent.ParentProjectId != null)
            throw new InvalidOperationException(
                "Only single-level hierarchies are supported; the chosen parent is itself a child.");
        // A would-be cycle requires the parent to already be a descendant of this project —
        // but since we only allow single-level hierarchies, a project that already has children
        // can't itself become someone else's child.
        var hasChildren = await _budgetDb.Projects.AnyAsync(p => p.ParentProjectId == projectId);
        if (hasChildren)
            throw new InvalidOperationException(
                "This project already has child projects; it can't itself become a child.");

        project.ParentProjectId = parentId.Value;
        await _budgetDb.SaveChangesAsync();
        return project;
    }

    /// <summary>
    /// Assign a category to a project. Budget amounts are configured on the Budget model.
    /// </summary>
    public async Task AssignCategoryToProjectAsync(int projectId, long categoryId)
    {
        var existing = await _budgetDb.ProjectCategoryAssignments
            .FirstOrDefaultAsync(a => a.ProjectId == projectId && a.CategoryId == categoryId);

        if (existing != null) return;

        _budgetDb.ProjectCategoryAssignments.Add(new ProjectCategoryAssignment
        {
            ProjectId = projectId,
            CategoryId = categoryId
        });

        await _budgetDb.SaveChangesAsync();
    }

    /// <summary>
    /// Remove a category assignment from a budget
    /// </summary>
    public async Task RemoveCategoryFromProjectAsync(int projectId, long categoryId)
    {
        var assignment = await _budgetDb.ProjectCategoryAssignments
            .FirstOrDefaultAsync(a => a.ProjectId == projectId && a.CategoryId == categoryId);

        if (assignment != null)
        {
            _budgetDb.ProjectCategoryAssignments.Remove(assignment);
            await _budgetDb.SaveChangesAsync();
        }
    }

    /// <summary>
    /// Resolve the SourceId for a project. Single FK lookup; callers that already loaded the
    /// project should pass <c>project.SourceId</c> directly instead.
    /// </summary>
    public async Task<int> GetSourceIdForProjectAsync(int projectId)
    {
        return await _budgetDb.Projects
            .Where(p => p.Id == projectId)
            .Select(p => p.SourceId)
            .FirstOrDefaultAsync();
    }

    /// <summary>
    /// All registered MoneyMoney sources, ordered by name.
    /// </summary>
    public async Task<List<ImportSource>> GetAllSourcesAsync()
    {
        return await _budgetDb.ImportSources
            .OrderBy(s => s.Name)
            .ToListAsync();
    }

    /// <summary>
    /// Calculate average monthly spending for a category group and all its categories/sub-groups
    /// over the last N months
    /// </summary>
    public async Task<decimal> CalculateAverageMonthlySpendingAsync(long categoryGroupId, int sourceId, int months = 12)
    {
        // Get all category IDs for this group (including sub-groups)
        var categoryIds = await GetAllCategoryIdsForGroupAsync(categoryGroupId, sourceId);

        if (!categoryIds.Any())
            return 0;

        // Calculate date range
        var endDate = DateTimeOffset.UtcNow;
        var startDate = endDate.AddMonths(-months);

        var startTimestamp = startDate.ToUnixTimeSeconds();
        var endTimestamp = endDate.ToUnixTimeSeconds();

        // Get all transactions for these categories in the time range
        var transactions = await _budgetDb.Transactions
            .Where(t => t.SourceId == sourceId)
            .Where(t => t.CategoryId.HasValue && categoryIds.Contains(t.CategoryId.Value))
            .Where(t => t.Timestamp >= startTimestamp && t.Timestamp <= endTimestamp)
            .Where(t => t.Amount < 0) // Only expenses (negative amounts)
            .ToListAsync();

        if (!transactions.Any())
            return 0;

        // Calculate total spending (convert to positive)
        var totalSpending = transactions.Sum(t => Math.Abs((decimal)t.Amount));

        // Return average per month
        return totalSpending / months;
    }

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

        var project = await GetProjectAsync(projectId);
        if (project == null)
            throw new ArgumentException("Project not found", nameof(projectId));

        var budgets = await GetBudgetsForProjectAsync(projectId);
        // Prefer the budget the user explicitly marked active; fall back to the most recently
        // modified one for the year if nothing is active (legacy data, edge cases).
        var activeBudget = budgets.FirstOrDefault(b => b.Year == year && b.IsActive)
            ?? budgets.FirstOrDefault(b => b.Year == year);
        var budgetAmounts = activeBudget != null
            ? activeBudget.MonthlyEntries.ToDictionary(
                e => ((long Id, CategoryItemType ItemType))(e.CategoryId, e.ItemType),
                e => Enumerable.Range(fromMonth, toMonth - fromMonth + 1).Sum(m => e.GetMonthAmount(m)))
            : new Dictionary<(long Id, CategoryItemType ItemType), decimal>();

        // Resolve tracked items from the project's live tracking config.
        var trackedKeys = new List<(long Id, CategoryItemType ItemType)>();
        foreach (var ti in project.TrackingItems)
        {
            if (ti.CategoryGroupId.HasValue)
                trackedKeys.Add((ti.CategoryGroupId.Value, CategoryItemType.Group));
            else if (ti.CategoryId.HasValue)
                trackedKeys.Add((ti.CategoryId.Value, CategoryItemType.Category));
        }

        // ieBatch gives each tracked row its natural total (leaf sets overlap, so a Group's
        // numbers include any tracked sub-cat under it). That's what we want for per-row display.
        // Aggregate totals (MonthIncome/MonthExpenses) come from a separate leaf-union pass so
        // they don't double-count across overlapping rows.
        var ieBatch = await GetMonthlyIncomeAndExpenseBatchAsync(trackedKeys, year, project.SourceId, projectId);
        var incomeItemsSet = await GetIncomeItemsForProjectAsync(projectId);

        var allCategoriesWithCounts = await GetAllCategoriesWithChildCountsAsync(project.SourceId);
        var groupNameLookup = allCategoriesWithCounts
            .Where(c => c.ItemType == CategoryItemType.Group)
            .ToDictionary(c => c.Id, c => c.CategoryGroup.Name ?? "Unknown");
        var leafCategoryNameLookup = (await _budgetDb.Categories.Where(c => c.SourceId == project.SourceId).ToListAsync())
            .ToDictionary(c => c.Id, c => c.Name ?? "Unknown");

        var (monthIncome, monthExpenses) = await AggregateIncomeExpenseForRangeAsync(
            trackedKeys, year, fromMonth, toMonth, project.SourceId, projectId);

        var categorySpending = new List<CategorySpending>();
        foreach (var key in trackedKeys)
        {
            string? name = key.ItemType == CategoryItemType.Group
                ? groupNameLookup.GetValueOrDefault(key.Id)
                : leafCategoryNameLookup.GetValueOrDefault(key.Id);
            if (name == null) continue;

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
                CategoryName = name,
                IsIncome = isIncome,
                Budget = budgetAmount,
                Spent = spent,
                Remaining = budgetAmount - spent
            });
        }

        // Budget totals rely on the post-migration invariant: at any subtree only one level
        // carries non-zero stored amounts, so summing all expense rows' Budget gives the
        // correct total. TotalSpent uses the leaf-union aggregate to avoid double-counting
        // across a tracked group + tracked sub-cat.
        var expenseRows = categorySpending.Where(c => !c.IsIncome).ToList();
        var totalBudget = expenseRows.Sum(c => c.Budget);

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

        var hierarchy = await GetAllCategoriesWithChildCountsAsync(sourceId);
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
        var start = end.AddYears(-1);
        var startTs = ((DateTimeOffset)start).ToUnixTimeSeconds();
        var endTs = ((DateTimeOffset)end).ToUnixTimeSeconds();

        var leafList = candidateLeaves.ToList();
        var hits = await _budgetDb.Transactions
            .Where(t => t.SourceId == sourceId)
            .Where(t => t.CategoryId.HasValue && leafList.Contains(t.CategoryId.Value))
            .Where(t => t.Timestamp >= startTs && t.Timestamp < endTs)
            .Select(t => t.CategoryId!.Value)
            .Distinct()
            .ToListAsync();

        return hits.ToHashSet();
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

        var allCategoriesWithCounts = await GetAllCategoriesWithChildCountsAsync(sourceId);
        var itemToLeafIds = BuildItemToLeafCategoryIds(items, allCategoriesWithCounts);
        var allLeafIds = itemToLeafIds.Values.SelectMany(s => s).Distinct().ToList();
        if (allLeafIds.Count == 0) return (0m, 0m);

        Dictionary<long, bool> leafIsIncome;
        if (projectId.HasValue)
        {
            var typeAssignments = await _budgetDb.CategoryTypeAssignments
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
            var types = await _budgetDb.Categories
                .Where(c => c.SourceId == sourceId && allLeafIds.Contains(c.Id))
                .Select(c => new { c.Id, c.Type })
                .ToListAsync();
            leafIsIncome = types.ToDictionary(c => c.Id, c => c.Type == 2);
        }

        var startDate = new DateTime(year, fromMonth, 1, 0, 0, 0, DateTimeKind.Utc);
        var endDate = new DateTime(year, toMonth, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1);
        var startTs = ((DateTimeOffset)startDate).ToUnixTimeSeconds();
        var endTs = ((DateTimeOffset)endDate).ToUnixTimeSeconds();

        var raw = await _budgetDb.Transactions
            .Where(t => t.SourceId == sourceId)
            .Where(t => t.CategoryId.HasValue && allLeafIds.Contains(t.CategoryId.Value))
            .Where(t => t.Timestamp >= startTs && t.Timestamp < endTs)
            .Select(t => new { CategoryId = t.CategoryId!.Value, t.Amount })
            .ToListAsync();

        decimal income = 0m, expense = 0m;
        foreach (var t in raw)
        {
            var amt = (decimal)t.Amount;
            if (leafIsIncome.GetValueOrDefault(t.CategoryId, false)) income += amt;
            else expense += -amt;
        }
        return (income, expense);
    }

    /// <summary>
    /// Get all category IDs for a category group (including all sub-groups and their categories)
    /// </summary>
    private async Task<List<long>> GetAllCategoryIdsForGroupAsync(long categoryGroupId, int sourceId)
    {
        var categoryIds = new List<long>();

        // Get all category group IDs (including sub-groups)
        var groupIds = await GetAllCategoryGroupIdsRecursiveAsync(categoryGroupId, sourceId);

        // Get all categories that belong to these groups
        foreach (var groupId in groupIds)
        {
            var categoriesInGroup = await _budgetDb.Categories
                .Where(c => c.SourceId == sourceId && c.GroupId == groupId)
                .Select(c => c.Id)
                .ToListAsync();

            categoryIds.AddRange(categoriesInGroup);
        }

        return categoryIds;
    }

    /// <summary>
    /// Recursively get all category group IDs including sub-groups
    /// </summary>
    private async Task<List<long>> GetAllCategoryGroupIdsRecursiveAsync(long categoryGroupId, int sourceId)
    {
        var groupIds = new List<long> { categoryGroupId };

        // Get sub-groups
        var subGroups = await _budgetDb.CategoryGroups
            .Where(cg => cg.SourceId == sourceId && cg.ParentGroupId == categoryGroupId)
            .ToListAsync();

        foreach (var subGroup in subGroups)
        {
            var subGroupIds = await GetAllCategoryGroupIdsRecursiveAsync(subGroup.Id, sourceId);
            groupIds.AddRange(subGroupIds);
        }

        return groupIds;
    }

    /// <summary>
    /// Get all categories for a specific source (from mm_categories).
    /// </summary>
    public async Task<List<Category>> GetAllCategoriesAsync(int sourceId)
    {
        return await _budgetDb.Categories
            .Where(c => c.SourceId == sourceId)
            .OrderBy(c => c.Name)
            .ToListAsync();
    }

    /// <summary>
    /// Get all category groups and individual categories with their sub-group counts, scoped
    /// to one source. Sentinel rowid-0/1 entries (MoneyMoney's hidden "Umbuchungen" root) are
    /// filtered out per source.
    /// </summary>
    public async Task<List<CategoryGroupWithChildCount>> GetAllCategoriesWithChildCountsAsync(int sourceId)
    {
        var sentinelRoot = SourceScope.SentinelRoot(sourceId);

        var allCategoryGroups = await _budgetDb.CategoryGroups
            .Where(cg => cg.SourceId == sourceId)
            .OrderBy(cg => cg.Name)
            .ToListAsync();

        var allCategories = await _budgetDb.Categories
            .Where(c => c.SourceId == sourceId)
            .OrderBy(c => c.Name)
            .ToListAsync();

        var result = new List<CategoryGroupWithChildCount>();

        foreach (var categoryGroup in allCategoryGroups)
        {
            // Skip the source's sentinel root category group (no name, holds "Umbuchungen").
            if (categoryGroup.Id == sentinelRoot && string.IsNullOrEmpty(categoryGroup.Name))
                continue;

            // Count direct children (both sub-groups and individual categories)
            var subGroupCount = allCategoryGroups.Count(cg => cg.ParentGroupId == categoryGroup.Id);
            var categoryCount = allCategories.Count(c => c.GroupId == categoryGroup.Id);
            var childCount = subGroupCount + categoryCount;

            // A category group is a root category if:
            // 1. It has no parent (group_key is null or 0), OR
            // 2. It's a child of the sentinel root group which we're hiding
            var isRootCategory = categoryGroup.ParentGroupId == null ||
                                 categoryGroup.ParentGroupId == 0 ||
                                 categoryGroup.ParentGroupId == sentinelRoot;

            result.Add(new CategoryGroupWithChildCount
            {
                CategoryGroup = categoryGroup,
                ItemType = CategoryItemType.Group,
                DirectChildCount = childCount,
                IsRootCategory = isRootCategory,
                ParentCategoryGroupId = categoryGroup.ParentGroupId
            });
        }

        // Add individual categories as items
        foreach (var category in allCategories)
        {
            // Skip if no GroupId (shouldn't happen but be safe)
            if (!category.GroupId.HasValue)
                continue;

            result.Add(new CategoryGroupWithChildCount
            {
                CategoryGroup = new CategoryGroup { Id = category.GroupId.Value }, // Placeholder for parent reference
                Category = category,
                ItemType = CategoryItemType.Category,
                DirectChildCount = 0, // Categories don't have children
                IsRootCategory = false,
                ParentCategoryGroupId = category.GroupId
            });
        }

        return result;
    }

    /// <summary>
    /// Get all root category groups for a source (groups with no parent or parented to the
    /// sentinel root group).
    /// </summary>
    public async Task<List<CategoryGroup>> GetRootCategoriesAsync(int sourceId)
    {
        var sentinelRoot = SourceScope.SentinelRoot(sourceId);
        return await _budgetDb.CategoryGroups
            .Where(cg => cg.SourceId == sourceId)
            .Where(cg => cg.ParentGroupId == null || cg.ParentGroupId == 0 || cg.ParentGroupId == sentinelRoot)
            .Where(cg => cg.Id != sentinelRoot)
            .OrderBy(cg => cg.Name)
            .ToListAsync();
    }

    /// <summary>
    /// Get a category group by namespaced ID. The ID alone is unique across sources because
    /// of namespacing, so no source filter is needed at the boundary.
    /// </summary>
    public async Task<CategoryGroup?> GetCategoryWithSubCategoriesAsync(long categoryGroupId)
    {
        return await _budgetDb.CategoryGroups
            .FirstOrDefaultAsync(cg => cg.Id == categoryGroupId);
    }

    /// <summary>
    /// Get monthly spending for an individual category over the last N months
    /// </summary>
    public async Task<List<MonthlySpending>> GetMonthlySpendingForCategoryAsync(long categoryId, int sourceId, int months = 12, bool isIncome = false)
    {
        // Get calendar month boundaries - last N months including current month
        var now = DateTimeOffset.UtcNow;
        var startDate = new DateTimeOffset(new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc)).AddMonths(-(months - 1));

        var results = new List<MonthlySpending>();

        for (int i = 0; i < months; i++)
        {
            var monthStart = startDate.AddMonths(i);
            var monthEnd = monthStart.AddMonths(1);

            var startTimestamp = monthStart.ToUnixTimeSeconds();
            var endTimestamp = monthEnd.ToUnixTimeSeconds();

            var spent = isIncome
                ? await _budgetDb.Transactions
                    .Where(t => t.SourceId == sourceId)
                    .Where(t => t.CategoryId == categoryId)
                    .Where(t => t.Timestamp >= startTimestamp && t.Timestamp < endTimestamp)
                    .Where(t => t.Amount > 0)
                    .SumAsync(t => (decimal)t.Amount)
                : await _budgetDb.Transactions
                    .Where(t => t.SourceId == sourceId)
                    .Where(t => t.CategoryId == categoryId)
                    .Where(t => t.Timestamp >= startTimestamp && t.Timestamp < endTimestamp)
                    .Where(t => t.Amount < 0)
                    .SumAsync(t => -(decimal)t.Amount);

            results.Add(new MonthlySpending
            {
                Year = monthStart.Year,
                Month = monthStart.Month,
                Amount = spent
            });
        }

        return results;
    }

    /// <summary>
    /// Get monthly spending for a category group over the last N months. When isIncome=true,
    /// sums positive transactions (income) instead of negated negatives (expenses).
    /// </summary>
    public async Task<List<MonthlySpending>> GetMonthlySpendingAsync(long categoryGroupId, int sourceId, int months = 12, bool isIncome = false)
    {
        var categoryIds = await GetAllCategoryIdsForGroupAsync(categoryGroupId, sourceId);

        if (!categoryIds.Any())
            return new List<MonthlySpending>();

        // Get calendar month boundaries - last N months including current month
        var now = DateTimeOffset.UtcNow;
        var startDate = new DateTimeOffset(new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc)).AddMonths(-(months - 1));

        var results = new List<MonthlySpending>();

        for (int i = 0; i < months; i++)
        {
            var monthStart = startDate.AddMonths(i);
            var monthEnd = monthStart.AddMonths(1);

            var startTimestamp = monthStart.ToUnixTimeSeconds();
            var endTimestamp = monthEnd.ToUnixTimeSeconds();

            var spent = isIncome
                ? await _budgetDb.Transactions
                    .Where(t => t.SourceId == sourceId)
                    .Where(t => t.CategoryId.HasValue && categoryIds.Contains(t.CategoryId.Value))
                    .Where(t => t.Timestamp >= startTimestamp && t.Timestamp < endTimestamp)
                    .Where(t => t.Amount > 0)
                    .SumAsync(t => (decimal)t.Amount)
                : await _budgetDb.Transactions
                    .Where(t => t.SourceId == sourceId)
                    .Where(t => t.CategoryId.HasValue && categoryIds.Contains(t.CategoryId.Value))
                    .Where(t => t.Timestamp >= startTimestamp && t.Timestamp < endTimestamp)
                    .Where(t => t.Amount < 0)
                    .SumAsync(t => -(decimal)t.Amount);

            results.Add(new MonthlySpending
            {
                Year = monthStart.Year,
                Month = monthStart.Month,
                Amount = spent
            });
        }

        return results;
    }

    /// <summary>
    /// Get spending breakdown by category for a specific month. Must be scoped to a project so
    /// the source is known; calling with projectId==0 returns an empty list.
    /// </summary>
    public async Task<List<CategorySpendingDetail>> GetCategorySpendingForMonthAsync(
        int year,
        int month,
        int projectId,
        HashSet<long>? selectedCategoryGroupIds = null,
        HashSet<long>? selectedCategoryIds = null)
    {
        var startDate = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
        var endDate = startDate.AddMonths(1);

        var startTimestamp = ((DateTimeOffset)startDate).ToUnixTimeSeconds();
        var endTimestamp = ((DateTimeOffset)endDate).ToUnixTimeSeconds();

        if (projectId <= 0)
            return new List<CategorySpendingDetail>();

        var project = await GetProjectAsync(projectId);
        if (project == null)
            return new List<CategorySpendingDetail>();

        var sourceId = project.SourceId;
        var budgetCategoryGroupIds = new HashSet<long>();
        foreach (var assignment in project.CategoryAssignments)
        {
            budgetCategoryGroupIds.Add(assignment.CategoryId);
            var subGroupIds = await GetAllCategoryGroupIdsRecursiveAsync(assignment.CategoryId, sourceId);
            foreach (var subGroupId in subGroupIds)
            {
                budgetCategoryGroupIds.Add(subGroupId);
            }
        }

        var categoryGroupsToProcess = await _budgetDb.CategoryGroups
            .Where(cg => cg.SourceId == sourceId && budgetCategoryGroupIds.Contains(cg.Id))
            .OrderBy(cg => cg.Name)
            .ToListAsync();

        var results = new List<CategorySpendingDetail>();

        // Process selected category groups
        if (selectedCategoryGroupIds != null && selectedCategoryGroupIds.Any())
        {
            var groupsToProcess = categoryGroupsToProcess
                .Where(cg => selectedCategoryGroupIds.Contains(cg.Id))
                .ToList();

            foreach (var categoryGroup in groupsToProcess)
            {
                var categoryIds = await GetAllCategoryIdsForGroupAsync(categoryGroup.Id, sourceId);

                if (!categoryIds.Any())
                    continue;

                var spent = await _budgetDb.Transactions
                    .Where(t => t.SourceId == sourceId)
                    .Where(t => t.CategoryId.HasValue && categoryIds.Contains(t.CategoryId.Value))
                    .Where(t => t.Timestamp >= startTimestamp && t.Timestamp < endTimestamp)
                    .Where(t => t.Amount < 0)
                    .SumAsync(t => -(decimal)t.Amount);

                if (spent > 0)
                {
                    results.Add(new CategorySpendingDetail
                    {
                        CategoryGroupId = categoryGroup.Id,
                        CategoryGroupName = categoryGroup.Name ?? "Unknown",
                        Amount = spent
                    });
                }
            }
        }

        // Process selected individual categories
        if (selectedCategoryIds != null && selectedCategoryIds.Any())
        {
            foreach (var categoryId in selectedCategoryIds)
            {
                var category = await _budgetDb.Categories
                    .FirstOrDefaultAsync(c => c.SourceId == sourceId && c.Id == categoryId);

                if (category == null)
                    continue;

                var spent = await _budgetDb.Transactions
                    .Where(t => t.SourceId == sourceId)
                    .Where(t => t.CategoryId == categoryId)
                    .Where(t => t.Timestamp >= startTimestamp && t.Timestamp < endTimestamp)
                    .Where(t => t.Amount < 0)
                    .SumAsync(t => -(decimal)t.Amount);

                if (spent > 0)
                {
                    results.Add(new CategorySpendingDetail
                    {
                        CategoryGroupId = category.GroupId ?? 0,
                        CategoryGroupName = category.Name ?? "Unknown",
                        Amount = spent
                    });
                }
            }
        }

        return results.OrderByDescending(r => r.Amount).ToList();
    }

    #region Budget Management

    /// <summary>
    /// Get all budgets for a project
    /// </summary>
    public async Task<List<Budget>> GetBudgetsForProjectAsync(int projectId)
    {
        return await _budgetDb.Budgets
            .Where(b => b.ProjectId == projectId)
            .Include(b => b.MonthlyEntries)
            .OrderByDescending(b => b.Year)
            .ThenByDescending(b => b.IsActive)
            .ThenByDescending(b => b.LastModifiedAt ?? b.CreatedAt)
            .ToListAsync();
    }

    /// <summary>
    /// Get a specific budget
    /// </summary>
    public async Task<Budget?> GetBudgetAsync(int budgetId)
    {
        return await _budgetDb.Budgets
            .Include(b => b.MonthlyEntries)
            .Include(b => b.Project)
            .ThenInclude(p => p.CategoryAssignments)
            .FirstOrDefaultAsync(b => b.Id == budgetId);
    }

    /// <summary>
    /// Create a new budget. By default the new budget becomes the active one for its
    /// (project, year), deactivating any sibling that was active. If <paramref name="name"/>
    /// is null/empty we use today's date in YYYY-MM-DD form.
    /// </summary>
    public async Task<Budget> CreateBudgetAsync(int projectId, int year, string? name = null, string? notes = null, bool makeActive = true)
    {
        var createdAt = DateTime.UtcNow;
        var budget = new Budget
        {
            ProjectId = projectId,
            Year = year,
            Name = string.IsNullOrWhiteSpace(name) ? createdAt.ToString("yyyy-MM-dd") : name.Trim(),
            Notes = notes,
            CreatedAt = createdAt,
            IsActive = makeActive
        };

        if (makeActive)
        {
            // Clear any other active budget for the same (project, year) so the new one wins.
            var siblings = await _budgetDb.Budgets
                .Where(b => b.ProjectId == projectId && b.Year == year && b.IsActive)
                .ToListAsync();
            foreach (var sibling in siblings)
                sibling.IsActive = false;
        }

        _budgetDb.Budgets.Add(budget);
        await _budgetDb.SaveChangesAsync();

        return budget;
    }

    /// <summary>
    /// Mark a budget as the active one for its (project, year), deactivating siblings.
    /// </summary>
    public async Task SetActiveBudgetAsync(int budgetId)
    {
        var target = await _budgetDb.Budgets.FindAsync(budgetId);
        if (target == null) return;

        var siblings = await _budgetDb.Budgets
            .Where(b => b.ProjectId == target.ProjectId && b.Year == target.Year && b.Id != target.Id && b.IsActive)
            .ToListAsync();
        foreach (var sibling in siblings)
            sibling.IsActive = false;

        target.IsActive = true;
        await _budgetDb.SaveChangesAsync();
    }

    /// <summary>
    /// Update a budget. If the caller flipped IsActive on, deactivate every other budget for
    /// the same (project, year) in the same transaction so the unique-active invariant holds.
    /// </summary>
    public async Task UpdateBudgetAsync(Budget budget)
    {
        budget.LastModifiedAt = DateTime.UtcNow;

        if (budget.IsActive)
        {
            var siblings = await _budgetDb.Budgets
                .Where(b => b.ProjectId == budget.ProjectId
                            && b.Year == budget.Year
                            && b.Id != budget.Id
                            && b.IsActive)
                .ToListAsync();
            foreach (var sibling in siblings)
                sibling.IsActive = false;
        }

        _budgetDb.Budgets.Update(budget);
        await _budgetDb.SaveChangesAsync();
    }

    /// <summary>
    /// Delete a budget
    /// </summary>
    public async Task DeleteBudgetAsync(int budgetId)
    {
        var budget = await _budgetDb.Budgets.FindAsync(budgetId);
        if (budget != null)
        {
            _budgetDb.Budgets.Remove(budget);
            await _budgetDb.SaveChangesAsync();
        }
    }

    /// <summary>
    /// Replace the full set of MonthlyBudgetEntry rows for a budget. Existing rows whose
    /// (CategoryId, ItemType) key is absent from <paramref name="entries"/> are deleted;
    /// the rest are upserted. Touches LastModifiedAt and writes everything in one
    /// SaveChangesAsync so partial failures don't leave the budget half-rewritten.
    /// </summary>
    public async Task SaveBudgetEntriesAsync(
        int budgetId,
        IReadOnlyCollection<(long Id, CategoryItemType ItemType, decimal[] Months)> entries)
    {
        var budget = await _budgetDb.Budgets.FindAsync(budgetId);
        if (budget == null)
            throw new ArgumentException("Budget not found", nameof(budgetId));

        var project = await _budgetDb.Projects.FindAsync(budget.ProjectId);
        var sourceId = project?.SourceId ?? SourceScope.LegacySourceId;
        var hierarchy = await GetAllCategoriesWithChildCountsAsync(sourceId);
        var normalized = NormalizeBudgetEntries(entries, hierarchy);

        var existing = await _budgetDb.MonthlyBudgetEntries
            .Where(e => e.BudgetId == budgetId)
            .ToListAsync();

        var desiredKeys = normalized.Select(e => (e.Id, e.ItemType)).ToHashSet();
        foreach (var row in existing.Where(e => !desiredKeys.Contains((e.CategoryId, e.ItemType))))
        {
            _budgetDb.MonthlyBudgetEntries.Remove(row);
        }

        foreach (var (id, itemType, months) in normalized)
        {
            if (months.Length != 12)
                throw new ArgumentException($"Entry ({id}, {itemType}) needs exactly 12 monthly amounts; got {months.Length}.");

            var row = existing.FirstOrDefault(e => e.CategoryId == id && e.ItemType == itemType);
            if (row == null)
            {
                row = new MonthlyBudgetEntry
                {
                    BudgetId = budgetId,
                    CategoryId = id,
                    ItemType = itemType
                };
                _budgetDb.MonthlyBudgetEntries.Add(row);
            }
            for (int m = 1; m <= 12; m++)
                row.SetMonthAmount(m, months[m - 1]);
        }

        budget.LastModifiedAt = DateTime.UtcNow;
        await _budgetDb.SaveChangesAsync();
    }

    /// <summary>
    /// Enforce the one-level-per-subtree invariant in the entries being saved: any Group entry
    /// whose descendants in the same set have non-zero amounts gets its own months zeroed. The
    /// UI should prevent this state, but this guards against drift between the UI's editing
    /// state and what's submitted.
    /// </summary>
    private static List<(long Id, CategoryItemType ItemType, decimal[] Months)> NormalizeBudgetEntries(
        IReadOnlyCollection<(long Id, CategoryItemType ItemType, decimal[] Months)> entries,
        List<CategoryGroupWithChildCount> hierarchy)
    {
        var byKey = entries.ToDictionary(e => (e.Id, e.ItemType), e => e.Months);
        var result = new List<(long Id, CategoryItemType ItemType, decimal[] Months)>(entries.Count);
        foreach (var entry in entries)
        {
            if (entry.ItemType != CategoryItemType.Group)
            {
                result.Add(entry);
                continue;
            }
            var descendants = CategoryGroupWithChildCount.CollectDescendants(
                entry.Id, hierarchy, includeLeafCategories: true);
            bool hasActiveDescendant = descendants.Any(d =>
                byKey.TryGetValue((d.Id, d.ItemType), out var dMonths) && dMonths.Any(m => m != 0m));
            result.Add(hasActiveDescendant
                ? (entry.Id, entry.ItemType, new decimal[12])
                : entry);
        }
        return result;
    }

    /// <summary>
    /// Add or update a monthly budget entry
    /// </summary>
    public async Task<MonthlyBudgetEntry> SaveMonthlyBudgetEntryAsync(MonthlyBudgetEntry entry)
    {
        var existing = await _budgetDb.MonthlyBudgetEntries
            .FirstOrDefaultAsync(e => e.BudgetId == entry.BudgetId
                && e.CategoryId == entry.CategoryId
                && e.ItemType == entry.ItemType);

        if (existing != null)
        {
            existing.Month1 = entry.Month1;
            existing.Month2 = entry.Month2;
            existing.Month3 = entry.Month3;
            existing.Month4 = entry.Month4;
            existing.Month5 = entry.Month5;
            existing.Month6 = entry.Month6;
            existing.Month7 = entry.Month7;
            existing.Month8 = entry.Month8;
            existing.Month9 = entry.Month9;
            existing.Month10 = entry.Month10;
            existing.Month11 = entry.Month11;
            existing.Month12 = entry.Month12;

            _budgetDb.MonthlyBudgetEntries.Update(existing);
        }
        else
        {
            _budgetDb.MonthlyBudgetEntries.Add(entry);
        }

        await _budgetDb.SaveChangesAsync();
        return existing ?? entry;
    }

    #endregion

    #region Budget Prediction

    /// <summary>
    /// Predict monthly budgets for a project for a specific year using historical data with seasonal adjustment
    /// </summary>
    public async Task<Dictionary<long, decimal[]>> PredictMonthlyBudgetsAsync(int projectId, int targetYear)
    {
        var project = await GetProjectAsync(projectId);
        if (project == null)
            return new Dictionary<long, decimal[]>();

        var predictions = new Dictionary<long, decimal[]>();

        foreach (var assignment in project.CategoryAssignments)
        {
            var monthlyPredictions = await PredictMonthlySpendingForCategoryAsync(assignment.CategoryId, targetYear, project.SourceId);
            predictions[assignment.CategoryId] = monthlyPredictions;
        }

        return predictions;
    }

    /// <summary>
    /// Reads the 12 actual monthly spending amounts for a single tracked item in a specific
    /// calendar year. Used as the prediction for already-elapsed target years where seasonal
    /// scaling against today's level would be nonsensical.
    /// </summary>
    private async Task<decimal[]> GetYearActualsAsync((long Id, CategoryItemType ItemType) key, int year, int sourceId, bool isIncome = false)
    {
        var result = new decimal[12];
        var batch = await GetMonthlySpendingBatchAsync(new[] { key }, year, sourceId, isIncome);
        if (batch.TryGetValue(key, out var trend))
        {
            for (int i = 0; i < 12 && i < trend.Count; i++)
                result[i] = trend[i].Amount;
        }
        return result;
    }

    /// <summary>
    /// Predict monthly spending for a leaf (MoneyMoney Category) using seasonal adjustment —
    /// same algorithm as <see cref="PredictMonthlySpendingForCategoryAsync"/> but driven by
    /// per-leaf history (<see cref="GetMonthlySpendingForCategoryAsync"/>) instead of summing
    /// a group's descendants.
    /// </summary>
    public async Task<decimal[]> PredictMonthlySpendingForLeafCategoryAsync(long categoryId, int targetYear, int sourceId, bool isIncome = false)
    {
        var predictions = new decimal[12];

        // For a completely elapsed year, just use that year's own actuals — no scaling needed.
        if (targetYear < DateTime.UtcNow.Year)
        {
            return await GetYearActualsAsync((categoryId, CategoryItemType.Category), targetYear, sourceId, isIncome);
        }

        var monthlyData = await GetMonthlySpendingForCategoryAsync(categoryId, sourceId, 24, isIncome);

        if (!monthlyData.Any())
            return predictions;

        var previousYearData = monthlyData
            .Where(m => m.Year == targetYear - 1)
            .OrderBy(m => m.Month)
            .ToList();

        var now = DateTime.UtcNow;
        var recentData = monthlyData
            .Where(m => m.Year < now.Year || (m.Year == now.Year && m.Month < now.Month))
            .OrderByDescending(m => m.Year)
            .ThenByDescending(m => m.Month)
            .Take(12)
            .ToList();

        if (!recentData.Any())
        {
            var avg = monthlyData.Average(m => m.Amount);
            for (int i = 0; i < 12; i++) predictions[i] = avg;
            return predictions;
        }

        var recentTotal = recentData.Sum(m => m.Amount);

        if (previousYearData.Count == 12)
        {
            var previousYearTotal = previousYearData.Sum(m => m.Amount);
            if (previousYearTotal > 0)
            {
                for (int month = 1; month <= 12; month++)
                {
                    var previousMonth = previousYearData.FirstOrDefault(m => m.Month == month);
                    predictions[month - 1] = previousMonth != null
                        ? recentTotal * (previousMonth.Amount / previousYearTotal)
                        : recentTotal / 12;
                }
                return predictions;
            }
        }

        var monthlyAvg = recentTotal / 12;
        for (int i = 0; i < 12; i++) predictions[i] = monthlyAvg;
        return predictions;
    }

    /// <summary>
    /// Predict monthly spending for a specific category group using seasonal adjustment
    /// This looks at the same month last year and adjusts based on overall trend
    /// </summary>
    public async Task<decimal[]> PredictMonthlySpendingForCategoryAsync(long categoryGroupId, int targetYear, int sourceId, bool isIncome = false)
    {
        var predictions = new decimal[12];

        // For a completely elapsed year, just use that year's own actuals — no scaling needed.
        if (targetYear < DateTime.UtcNow.Year)
        {
            return await GetYearActualsAsync((categoryGroupId, CategoryItemType.Group), targetYear, sourceId, isIncome);
        }

        // Get 2 years of historical data
        var monthlyData = await GetMonthlySpendingAsync(categoryGroupId, sourceId, 24, isIncome);

        if (!monthlyData.Any())
        {
            // No historical data, return zeros
            return predictions;
        }

        // Get previous calendar year data for distribution pattern
        var previousYearData = monthlyData
            .Where(m => m.Year == targetYear - 1)
            .OrderBy(m => m.Month)
            .ToList();

        // Get last 12 months (excluding current month) for recent spending level
        var now = DateTime.UtcNow;
        var recentData = monthlyData
            .Where(m => m.Year < now.Year || (m.Year == now.Year && m.Month < now.Month))
            .OrderByDescending(m => m.Year)
            .ThenByDescending(m => m.Month)
            .Take(12)
            .ToList();

        if (!recentData.Any())
        {
            // No recent data, fall back to simple averaging
            var avgAmount = monthlyData.Average(m => m.Amount);
            for (int i = 0; i < 12; i++)
            {
                predictions[i] = avgAmount;
            }
            return predictions;
        }

        // Calculate total from recent 12 months
        var recentTotal = recentData.Sum(m => m.Amount);

        if (previousYearData.Any() && previousYearData.Count == 12)
        {
            // Use previous year's distribution pattern with recent total
            var previousYearTotal = previousYearData.Sum(m => m.Amount);

            if (previousYearTotal > 0)
            {
                // Apply previous year's distribution to recent spending level
                for (int month = 1; month <= 12; month++)
                {
                    var previousYearMonth = previousYearData.FirstOrDefault(m => m.Month == month);
                    if (previousYearMonth != null)
                    {
                        var distribution = previousYearMonth.Amount / previousYearTotal;
                        predictions[month - 1] = recentTotal * distribution;
                    }
                    else
                    {
                        // Missing month, use average
                        predictions[month - 1] = recentTotal / 12;
                    }
                }
            }
            else
            {
                // Previous year total is 0, distribute evenly
                var monthlyAvg = recentTotal / 12;
                for (int i = 0; i < 12; i++)
                {
                    predictions[i] = monthlyAvg;
                }
            }
        }
        else
        {
            // Don't have complete previous year, use even distribution of recent total
            var monthlyAvg = recentTotal / 12;
            for (int i = 0; i < 12; i++)
            {
                predictions[i] = monthlyAvg;
            }
        }

        return predictions;
    }

    /// <summary>
    /// Generate a complete budget for a project based on predictions
    /// </summary>
    public async Task<Budget> GeneratePredictedBudgetAsync(int projectId, int year, string? notes = null)
    {
        // Create the budget
        var budget = await CreateBudgetAsync(projectId, year, notes ?? "Auto-generated from historical data");

        // Get predictions
        var predictions = await PredictMonthlyBudgetsAsync(projectId, year);

        // Create monthly budget entries — these come from ProjectCategoryAssignments which
        // historically only stored Group references.
        foreach (var (categoryId, monthlyAmounts) in predictions)
        {
            var entry = new MonthlyBudgetEntry
            {
                BudgetId = budget.Id,
                CategoryId = categoryId,
                ItemType = CategoryItemType.Group,
                Month1 = monthlyAmounts[0],
                Month2 = monthlyAmounts[1],
                Month3 = monthlyAmounts[2],
                Month4 = monthlyAmounts[3],
                Month5 = monthlyAmounts[4],
                Month6 = monthlyAmounts[5],
                Month7 = monthlyAmounts[6],
                Month8 = monthlyAmounts[7],
                Month9 = monthlyAmounts[8],
                Month10 = monthlyAmounts[9],
                Month11 = monthlyAmounts[10],
                Month12 = monthlyAmounts[11]
            };

            await SaveMonthlyBudgetEntryAsync(entry);
        }

        // Reload budget with entries
        return await GetBudgetAsync(budget.Id) ?? budget;
    }

    #endregion

    #region Project Tracking Items

    /// <summary>
    /// Get all tracking items for a project
    /// </summary>
    public async Task<List<ProjectTrackingItem>> GetProjectTrackingItemsAsync(int projectId)
    {
        return await _budgetDb.ProjectTrackingItems
            .Where(pti => pti.ProjectId == projectId)
            .ToListAsync();
    }

    /// <summary>
    /// Add tracking items for any keys not already tracked by the project; never removes
    /// existing ones. Used by BudgetEdit's save path so promoting a shadow row into a real
    /// budget entry also makes that sub-cat tracked everywhere else.
    /// </summary>
    public async Task EnsureProjectTrackingIncludesAsync(
        int projectId,
        IEnumerable<(long Id, CategoryItemType ItemType)> keys)
    {
        var keyList = keys.Distinct().ToList();
        if (keyList.Count == 0) return;

        var existing = await _budgetDb.ProjectTrackingItems
            .Where(pti => pti.ProjectId == projectId)
            .ToListAsync();
        var have = existing
            .Select(e => e.CategoryGroupId.HasValue
                ? (e.CategoryGroupId.Value, CategoryItemType.Group)
                : (e.CategoryId!.Value, CategoryItemType.Category))
            .ToHashSet();

        bool added = false;
        foreach (var key in keyList)
        {
            if (have.Contains(key)) continue;
            _budgetDb.ProjectTrackingItems.Add(new ProjectTrackingItem
            {
                ProjectId = projectId,
                CategoryGroupId = key.ItemType == CategoryItemType.Group ? key.Id : null,
                CategoryId = key.ItemType == CategoryItemType.Category ? key.Id : null
            });
            added = true;
        }
        if (added) await _budgetDb.SaveChangesAsync();
    }

    /// <summary>
    /// Update the tracking items for a project
    /// </summary>
    public async Task UpdateProjectTrackingItemsAsync(int projectId, HashSet<long> categoryGroupIds, HashSet<long> categoryIds)
    {
        // Remove existing tracking items
        var existing = await _budgetDb.ProjectTrackingItems
            .Where(pti => pti.ProjectId == projectId)
            .ToListAsync();
        _budgetDb.ProjectTrackingItems.RemoveRange(existing);

        // Add new tracking items for category groups
        foreach (var groupId in categoryGroupIds)
        {
            _budgetDb.ProjectTrackingItems.Add(new ProjectTrackingItem
            {
                ProjectId = projectId,
                CategoryGroupId = groupId,
                CategoryId = null
            });
        }

        // Add new tracking items for individual categories
        foreach (var categoryId in categoryIds)
        {
            _budgetDb.ProjectTrackingItems.Add(new ProjectTrackingItem
            {
                ProjectId = projectId,
                CategoryGroupId = null,
                CategoryId = categoryId
            });
        }

        await _budgetDb.SaveChangesAsync();
    }

    #endregion

    #region Budget Trend Analysis

    /// <summary>
    /// Returns this budget's stored Income Contribution splits as a dictionary keyed by
    /// (CategoryId, ItemType). Empty dictionary if no splits are saved.
    /// </summary>
    public async Task<Dictionary<(long Id, CategoryItemType ItemType), decimal>> GetBudgetContributionSplitsAsync(int budgetId)
    {
        var rows = await _budgetDb.BudgetContributionSplits
            .Where(s => s.BudgetId == budgetId)
            .ToListAsync();
        return rows.ToDictionary(r => (r.CategoryId, r.ItemType), r => r.Percent);
    }

    /// <summary>
    /// Replace the budget's contribution-split rows with the supplied set. Rows whose
    /// (CategoryId, ItemType) key isn't in the new set get deleted, others are upserted.
    /// </summary>
    public async Task SaveBudgetContributionSplitsAsync(
        int budgetId,
        IReadOnlyCollection<(long Id, CategoryItemType ItemType, decimal Percent)> splits)
    {
        var existing = await _budgetDb.BudgetContributionSplits
            .Where(s => s.BudgetId == budgetId)
            .ToListAsync();

        var desiredKeys = splits.Select(s => (s.Id, s.ItemType)).ToHashSet();
        foreach (var row in existing.Where(r => !desiredKeys.Contains((r.CategoryId, r.ItemType))))
        {
            _budgetDb.BudgetContributionSplits.Remove(row);
        }

        foreach (var (id, itemType, percent) in splits)
        {
            var row = existing.FirstOrDefault(r => r.CategoryId == id && r.ItemType == itemType);
            if (row == null)
            {
                _budgetDb.BudgetContributionSplits.Add(new BudgetContributionSplit
                {
                    BudgetId = budgetId,
                    CategoryId = id,
                    ItemType = itemType,
                    Percent = percent
                });
            }
            else
            {
                row.Percent = percent;
            }
        }

        await _budgetDb.SaveChangesAsync();
    }

    /// <summary>
    /// Returns the 12-month budget amounts for every MonthlyBudgetEntry on a budget, keyed by
    /// (CategoryId, ItemType) — Group and Category IDs share a numeric space so the
    /// discriminator is required.
    /// </summary>
    public async Task<Dictionary<(long Id, CategoryItemType ItemType), decimal[]>> GetMonthlyBudgetAmountsAsync(int budgetId)
    {
        var entries = await _budgetDb.MonthlyBudgetEntries
            .Where(e => e.BudgetId == budgetId)
            .ToListAsync();

        return entries.ToDictionary(
            e => (e.CategoryId, e.ItemType),
            e => new[]
            {
                e.Month1, e.Month2, e.Month3, e.Month4, e.Month5, e.Month6,
                e.Month7, e.Month8, e.Month9, e.Month10, e.Month11, e.Month12
            });
    }

    /// <summary>
    /// Effective + stored budget amounts for a budget. A group whose any descendant has a
    /// non-zero stored amount gets its effective months replaced by the sum of all stored
    /// amounts beneath it (the model invariant: budget amounts live at one level per subtree,
    /// so the sum is unambiguous post-normalization). Such groups are flagged in AutoSummed
    /// so the UI can render their inputs read-only.
    /// </summary>
    public async Task<BudgetEffectiveAmounts> ComputeBudgetEffectiveAmountsAsync(int budgetId)
    {
        var budget = await _budgetDb.Budgets
            .Include(b => b.MonthlyEntries)
            .Include(b => b.Project)
            .FirstOrDefaultAsync(b => b.Id == budgetId);
        if (budget == null) return new BudgetEffectiveAmounts();

        var stored = budget.MonthlyEntries.ToDictionary(
            e => (e.CategoryId, e.ItemType),
            e => new[]
            {
                e.Month1, e.Month2, e.Month3, e.Month4, e.Month5, e.Month6,
                e.Month7, e.Month8, e.Month9, e.Month10, e.Month11, e.Month12
            });

        var sourceId = budget.Project?.SourceId ?? SourceScope.LegacySourceId;
        var hierarchy = await GetAllCategoriesWithChildCountsAsync(sourceId);
        return ComputeEffectiveAmounts(stored, hierarchy);
    }

    /// <summary>
    /// Pure version of <see cref="ComputeBudgetEffectiveAmountsAsync"/> for callers that
    /// already have the stored amounts + hierarchy in memory (e.g. BudgetEdit reacts to every
    /// keystroke without a round trip). Returns a new <see cref="BudgetEffectiveAmounts"/>.
    /// </summary>
    public static BudgetEffectiveAmounts ComputeEffectiveAmounts(
        Dictionary<(long Id, CategoryItemType ItemType), decimal[]> stored,
        List<CategoryGroupWithChildCount> hierarchy)
    {
        var result = new BudgetEffectiveAmounts { Stored = stored };
        foreach (var (key, months) in stored)
            result.Effective[key] = (decimal[])months.Clone();

        foreach (var groupKey in stored.Keys.Where(k => k.ItemType == CategoryItemType.Group).ToList())
        {
            var descendants = CategoryGroupWithChildCount.CollectDescendants(
                groupKey.Id, hierarchy, includeLeafCategories: true);

            var sum = new decimal[12];
            bool anyDescendantHasAmount = false;
            foreach (var d in descendants)
            {
                if (!stored.TryGetValue((d.Id, d.ItemType), out var dMonths)) continue;
                for (int m = 0; m < 12; m++) sum[m] += dMonths[m];
                if (dMonths.Any(x => x != 0m)) anyDescendantHasAmount = true;
            }

            if (anyDescendantHasAmount)
            {
                result.Effective[groupKey] = sum;
                result.AutoSummed.Add(groupKey);
            }
        }
        return result;
    }

    public async Task<List<MonthlySpending>> GetBudgetTrendAsync(long categoryId, CategoryItemType itemType, int budgetId)
    {
        var budget = await _budgetDb.Budgets.FindAsync(budgetId);
        if (budget == null) return new List<MonthlySpending>();

        var entry = await _budgetDb.MonthlyBudgetEntries
            .FirstOrDefaultAsync(e => e.BudgetId == budgetId
                && e.CategoryId == categoryId
                && e.ItemType == itemType);

        if (entry == null) return new List<MonthlySpending>();

        var monthlyBudgets = new[]
        {
            entry.Month1, entry.Month2, entry.Month3, entry.Month4,
            entry.Month5, entry.Month6, entry.Month7, entry.Month8,
            entry.Month9, entry.Month10, entry.Month11, entry.Month12
        };

        var result = new List<MonthlySpending>();
        for (int month = 1; month <= 12; month++)
        {
            result.Add(new MonthlySpending
            {
                Year = budget.Year,
                Month = month,
                Amount = monthlyBudgets[month - 1]
            });
        }
        return result;
    }

    /// <summary>
    /// Get category type assignments for a project
    /// </summary>
    public async Task<List<CategoryTypeAssignment>> GetCategoryTypeAssignmentsAsync(int projectId)
    {
        return await _budgetDb.CategoryTypeAssignments
            .Where(cta => cta.ProjectId == projectId)
            .ToListAsync();
    }

    /// <summary>
    /// Returns the set of items (groups and categories) that resolve as Income for a project,
    /// taking inheritance from ancestor groups into account.
    /// </summary>
    public async Task<HashSet<(long Id, CategoryItemType ItemType)>> GetIncomeItemsForProjectAsync(int projectId)
    {
        var typeAssignments = await _budgetDb.CategoryTypeAssignments
            .Where(cta => cta.ProjectId == projectId)
            .ToListAsync();

        var result = new HashSet<(long, CategoryItemType)>();
        if (!typeAssignments.Any())
            return result;

        var assignmentLookup = typeAssignments.ToDictionary(
            ta => (ta.CategoryId, ta.ItemType),
            ta => ta.IsIncome);

        var sourceId = await GetSourceIdForProjectAsync(projectId);
        var allCategories = await GetAllCategoriesWithChildCountsAsync(sourceId);

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
        var existing = await _budgetDb.CategoryTypeAssignments
            .FirstOrDefaultAsync(cta => cta.ProjectId == projectId && cta.CategoryId == categoryId && cta.ItemType == itemType);

        if (existing != null)
        {
            existing.IsIncome = isIncome;
        }
        else
        {
            _budgetDb.CategoryTypeAssignments.Add(new CategoryTypeAssignment
            {
                ProjectId = projectId,
                CategoryId = categoryId,
                ItemType = itemType,
                IsIncome = isIncome
            });
        }

        await _budgetDb.SaveChangesAsync();
    }

    /// <summary>
    /// Delete a category type assignment
    /// </summary>
    public async Task DeleteCategoryTypeAsync(int projectId, long categoryId, CategoryItemType itemType)
    {
        var existing = await _budgetDb.CategoryTypeAssignments
            .FirstOrDefaultAsync(cta => cta.ProjectId == projectId && cta.CategoryId == categoryId && cta.ItemType == itemType);

        if (existing != null)
        {
            _budgetDb.CategoryTypeAssignments.Remove(existing);
            await _budgetDb.SaveChangesAsync();
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
    /// Get monthly income and expenses for a category group in a specific year
    /// Uses project-specific category type assignments if projectId is provided
    /// </summary>
    public async Task<(decimal[] Income, decimal[] Expenses)> GetMonthlyIncomeAndExpenseAsync(long categoryGroupId, int year, int sourceId, int? projectId = null)
    {
        // Check if this is a leaf category (has no children) or a category group
        var isLeafCategory = await _budgetDb.Categories.AnyAsync(c => c.SourceId == sourceId && c.Id == categoryGroupId);

        List<long> categoryIds;
        if (isLeafCategory)
        {
            // It's a leaf category, use it directly
            categoryIds = new List<long> { categoryGroupId };
        }
        else
        {
            // It's a category group, get all descendant categories
            categoryIds = await GetAllCategoryIdsForGroupAsync(categoryGroupId, sourceId);
        }

        if (!categoryIds.Any())
            return (new decimal[12], new decimal[12]);

        HashSet<long> incomeCategoryIds;
        HashSet<long> expenseCategoryIds;

        if (projectId.HasValue)
        {
            // Use project-specific category type assignments with inheritance
            var allCategories = await GetAllCategoriesWithChildCountsAsync(sourceId);
            var typeAssignments = await _budgetDb.CategoryTypeAssignments
                .Where(cta => cta.ProjectId == projectId.Value)
                .ToListAsync();

            var assignmentLookup = typeAssignments.ToDictionary(
                ta => (ta.CategoryId, ta.ItemType),
                ta => ta.IsIncome);

            incomeCategoryIds = new HashSet<long>();
            expenseCategoryIds = new HashSet<long>();

            // Resolve each leaf category's type with inheritance
            foreach (var categoryId in categoryIds)
            {
                bool isIncome = ResolveCategoryTypeWithInheritance(categoryId, CategoryItemType.Category, allCategories, assignmentLookup);
                if (isIncome)
                    incomeCategoryIds.Add(categoryId);
                else
                    expenseCategoryIds.Add(categoryId);
            }
        }
        else
        {
            // Fallback to MoneyMoney Category.Type
            var categories = await _budgetDb.Categories
                .Where(c => c.SourceId == sourceId && categoryIds.Contains(c.Id))
                .ToDictionaryAsync(c => c.Id, c => c.Type ?? 0);

            // Type 2 = Income categories, all others = Expense categories
            incomeCategoryIds = categories.Where(kvp => kvp.Value == 2).Select(kvp => kvp.Key).ToHashSet();
            expenseCategoryIds = categories.Where(kvp => kvp.Value != 2).Select(kvp => kvp.Key).ToHashSet();
        }

        var income = new decimal[12];
        var expenses = new decimal[12];

        for (int month = 1; month <= 12; month++)
        {
            var startDate = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
            var endDate = startDate.AddMonths(1);

            var startTimestamp = ((DateTimeOffset)startDate).ToUnixTimeSeconds();
            var endTimestamp = ((DateTimeOffset)endDate).ToUnixTimeSeconds();

            // Get income from income categories (sum all amounts)
            if (incomeCategoryIds.Any())
            {
                var monthlyIncome = await _budgetDb.Transactions
                    .Where(t => t.SourceId == sourceId)
                    .Where(t => t.CategoryId.HasValue && incomeCategoryIds.Contains(t.CategoryId.Value))
                    .Where(t => t.Timestamp >= startTimestamp && t.Timestamp < endTimestamp)
                    .SumAsync(t => (decimal)t.Amount);

                income[month - 1] = monthlyIncome;
            }

            // Get expenses from expense categories (sum all amounts, negate to make positive)
            if (expenseCategoryIds.Any())
            {
                var monthlyExpense = await _budgetDb.Transactions
                    .Where(t => t.SourceId == sourceId)
                    .Where(t => t.CategoryId.HasValue && expenseCategoryIds.Contains(t.CategoryId.Value))
                    .Where(t => t.Timestamp >= startTimestamp && t.Timestamp < endTimestamp)
                    .SumAsync(t => -(decimal)t.Amount);

                expenses[month - 1] = monthlyExpense;
            }
        }

        return (income, expenses);
    }

    /// <summary>
    /// Batched version of GetMonthlyIncomeAndExpenseAsync. Loads category hierarchy and type
    /// assignments once, then performs a single transactions query for the entire year covering
    /// every tracked item's leaf categories. Replaces an N×24-query pattern with one query.
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

        var allCategoriesWithCounts = await GetAllCategoriesWithChildCountsAsync(sourceId);
        var itemToLeafIds = BuildItemToLeafCategoryIds(items, allCategoriesWithCounts);

        var allLeafIds = itemToLeafIds.Values.SelectMany(s => s).Distinct().ToList();
        if (!allLeafIds.Any()) return result;

        Dictionary<long, bool> leafIsIncome;
        if (projectId.HasValue)
        {
            var typeAssignments = await _budgetDb.CategoryTypeAssignments
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
            var types = await _budgetDb.Categories
                .Where(c => c.SourceId == sourceId && allLeafIds.Contains(c.Id))
                .Select(c => new { c.Id, c.Type })
                .ToListAsync();
            leafIsIncome = types.ToDictionary(c => c.Id, c => c.Type == 2);
        }

        var yearStart = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var startTs = ((DateTimeOffset)yearStart).ToUnixTimeSeconds();
        var endTs = ((DateTimeOffset)yearStart.AddYears(1)).ToUnixTimeSeconds();

        var raw = await _budgetDb.Transactions
            .Where(t => t.SourceId == sourceId)
            .Where(t => t.CategoryId.HasValue && allLeafIds.Contains(t.CategoryId.Value))
            .Where(t => t.Timestamp >= startTs && t.Timestamp < endTs)
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
            arr[month] += (decimal)t.Amount;
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

        var allCategoriesWithCounts = await GetAllCategoriesWithChildCountsAsync(sourceId);
        var itemToLeafIds = BuildItemToLeafCategoryIds(items, allCategoriesWithCounts);

        var allLeafIds = itemToLeafIds.Values.SelectMany(s => s).Distinct().ToList();
        if (!allLeafIds.Any()) return result;

        Dictionary<long, bool> leafIsIncome;
        if (projectId.HasValue)
        {
            var typeAssignments = await _budgetDb.CategoryTypeAssignments
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
            var types = await _budgetDb.Categories
                .Where(c => c.SourceId == sourceId && allLeafIds.Contains(c.Id))
                .Select(c => new { c.Id, c.Type })
                .ToListAsync();
            leafIsIncome = types.ToDictionary(c => c.Id, c => c.Type == 2);
        }

        var monthStartUtc = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
        var startTs = ((DateTimeOffset)monthStartUtc).ToUnixTimeSeconds();
        var endTs = ((DateTimeOffset)monthStartUtc.AddMonths(1)).ToUnixTimeSeconds();

        var raw = await _budgetDb.Transactions
            .Where(t => t.SourceId == sourceId)
            .Where(t => t.CategoryId.HasValue && allLeafIds.Contains(t.CategoryId.Value))
            .Where(t => t.Timestamp >= startTs && t.Timestamp < endTs)
            .Select(t => new { CategoryId = t.CategoryId!.Value, t.Amount })
            .ToListAsync();

        var perCategorySum = new Dictionary<long, decimal>();
        foreach (var t in raw)
        {
            perCategorySum.TryGetValue(t.CategoryId, out var s);
            perCategorySum[t.CategoryId] = s + (decimal)t.Amount;
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
        var minTs = await _budgetDb.Transactions
            .Where(t => t.SourceId == sourceId)
            .MinAsync(t => (long?)t.Timestamp);
        var maxTs = await _budgetDb.Transactions
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
        var project = await GetProjectAsync(projectId);
        if (project == null) return new List<int>();

        var trackedKeys = new List<(long Id, CategoryItemType ItemType)>();
        foreach (var ti in project.TrackingItems)
        {
            if (ti.CategoryGroupId.HasValue)
                trackedKeys.Add((ti.CategoryGroupId.Value, CategoryItemType.Group));
            else if (ti.CategoryId.HasValue)
                trackedKeys.Add((ti.CategoryId.Value, CategoryItemType.Category));
        }
        if (!trackedKeys.Any()) return new List<int>();

        var allCategoriesWithCounts = await GetAllCategoriesWithChildCountsAsync(project.SourceId);
        var itemToLeafIds = BuildItemToLeafCategoryIds(trackedKeys, allCategoriesWithCounts);
        var allLeafIds = itemToLeafIds.Values.SelectMany(s => s).Distinct().ToList();
        if (!allLeafIds.Any()) return new List<int>();

        var minTs = await _budgetDb.Transactions
            .Where(t => t.SourceId == project.SourceId)
            .Where(t => t.CategoryId.HasValue && allLeafIds.Contains(t.CategoryId.Value))
            .MinAsync(t => (long?)t.Timestamp);
        var maxTs = await _budgetDb.Transactions
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

    /// <summary>
    /// Batched monthly outflow (negative transactions, negated to positive) for a calendar year.
    /// Items with no leaf categories get an empty list, matching the per-item method's behavior.
    /// </summary>
    public async Task<Dictionary<(long Id, CategoryItemType ItemType), List<MonthlySpending>>>
        GetMonthlySpendingBatchAsync(
            IReadOnlyCollection<(long Id, CategoryItemType ItemType)> items,
            int year,
            int sourceId,
            bool isIncome = false)
    {
        const int months = 12;
        var result = new Dictionary<(long, CategoryItemType), List<MonthlySpending>>();
        foreach (var item in items)
            result[item] = new List<MonthlySpending>();

        if (!items.Any()) return result;

        var startDate = new DateTimeOffset(new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var allCategoriesWithCounts = await GetAllCategoriesWithChildCountsAsync(sourceId);
        var itemToLeafIds = BuildItemToLeafCategoryIds(items, allCategoriesWithCounts);

        // Seed N month entries for items that have any leaf categories
        foreach (var item in items)
        {
            if (!itemToLeafIds[item].Any()) continue;
            var list = result[item];
            for (int m = 0; m < months; m++)
            {
                var d = startDate.AddMonths(m);
                list.Add(new MonthlySpending { Year = d.Year, Month = d.Month, Amount = 0 });
            }
        }

        var allLeafIds = itemToLeafIds.Values.SelectMany(s => s).Distinct().ToList();
        if (!allLeafIds.Any()) return result;

        var startTs = startDate.ToUnixTimeSeconds();
        var endTs = startDate.AddMonths(months).ToUnixTimeSeconds();

        var rawQuery = _budgetDb.Transactions
            .Where(t => t.SourceId == sourceId)
            .Where(t => t.CategoryId.HasValue && allLeafIds.Contains(t.CategoryId.Value))
            .Where(t => t.Timestamp >= startTs && t.Timestamp < endTs);
        rawQuery = isIncome
            ? rawQuery.Where(t => t.Amount > 0)
            : rawQuery.Where(t => t.Amount < 0);
        var raw = await rawQuery
            .Select(t => new { CategoryId = t.CategoryId!.Value, t.Amount, t.Timestamp })
            .ToListAsync();

        var perCategoryMonthly = new Dictionary<long, decimal[]>();
        foreach (var t in raw)
        {
            var ts = DateTimeOffset.FromUnixTimeSeconds(t.Timestamp).UtcDateTime;
            var monthIndex = ((ts.Year - startDate.Year) * 12) + (ts.Month - startDate.Month);
            if (monthIndex < 0 || monthIndex >= months) continue;
            if (!perCategoryMonthly.TryGetValue(t.CategoryId, out var arr))
            {
                arr = new decimal[months];
                perCategoryMonthly[t.CategoryId] = arr;
            }
            arr[monthIndex] += isIncome ? (decimal)t.Amount : -(decimal)t.Amount;
        }

        foreach (var item in items)
        {
            var list = result[item];
            if (!list.Any()) continue;
            foreach (var leafId in itemToLeafIds[item])
            {
                if (!perCategoryMonthly.TryGetValue(leafId, out var monthly)) continue;
                for (int m = 0; m < months; m++)
                    list[m].Amount += monthly[m];
            }
        }

        return result;
    }

    /// <summary>
    /// Resolves the set of leaf (MoneyMoney Category) IDs covered by each tracking item, using
    /// the already-loaded hierarchy to avoid additional DB calls. Each item's leaf set is its
    /// natural recursive expansion — overlaps between a group and a tracked descendant are
    /// preserved. Per-row totals are therefore the natural sum of everything under the item;
    /// aggregate totals across items must use the leaf union (Distinct) to avoid double-counting.
    /// </summary>
    private static Dictionary<(long, CategoryItemType), HashSet<long>> BuildItemToLeafCategoryIds(
        IEnumerable<(long Id, CategoryItemType ItemType)> items,
        List<CategoryGroupWithChildCount> allCategoriesWithCounts)
    {
        var map = new Dictionary<(long, CategoryItemType), HashSet<long>>();
        foreach (var item in items)
        {
            var leafIds = new HashSet<long>();
            if (item.ItemType == CategoryItemType.Category)
            {
                leafIds.Add(item.Id);
            }
            else
            {
                var descendants = CategoryGroupWithChildCount.CollectDescendants(
                    item.Id, allCategoriesWithCounts, includeLeafCategories: true);
                foreach (var d in descendants)
                {
                    if (d.ItemType == CategoryItemType.Category)
                        leafIds.Add(d.Id);
                }
            }
            map[item] = leafIds;
        }
        return map;
    }

    #endregion

    #region Project Account Assignments

    /// <summary>
    /// Returns all MoneyMoney accounts for a source ordered by MoneyMoney sort field, then name.
    /// Used by the project Accounts card and the assignment picker.
    /// </summary>
    public async Task<List<Account>> GetAllAccountsAsync(int sourceId)
    {
        return await _budgetDb.Accounts
            .Where(a => a.SourceId == sourceId)
            .OrderBy(a => a.Sort)
            .ThenBy(a => a.Name)
            .ToListAsync();
    }

    public async Task<List<ProjectAccountAssignment>> GetProjectAccountAssignmentsAsync(int projectId)
    {
        return await _budgetDb.ProjectAccountAssignments
            .Where(paa => paa.ProjectId == projectId)
            .ToListAsync();
    }

    public async Task AssignAccountToProjectAsync(int projectId, long accountId, AccountScope scope)
    {
        var existing = await _budgetDb.ProjectAccountAssignments
            .FirstOrDefaultAsync(paa => paa.ProjectId == projectId && paa.AccountId == accountId);
        if (existing != null)
        {
            existing.Scope = scope;
        }
        else
        {
            _budgetDb.ProjectAccountAssignments.Add(new ProjectAccountAssignment
            {
                ProjectId = projectId,
                AccountId = accountId,
                Scope = scope
            });
        }
        await _budgetDb.SaveChangesAsync();
    }

    public async Task SetAccountScopeAsync(int projectId, long accountId, AccountScope scope)
    {
        var existing = await _budgetDb.ProjectAccountAssignments
            .FirstOrDefaultAsync(paa => paa.ProjectId == projectId && paa.AccountId == accountId);
        if (existing == null) return;
        existing.Scope = scope;
        await _budgetDb.SaveChangesAsync();
    }

    public async Task RemoveAccountFromProjectAsync(int projectId, long accountId)
    {
        var existing = await _budgetDb.ProjectAccountAssignments
            .FirstOrDefaultAsync(paa => paa.ProjectId == projectId && paa.AccountId == accountId);
        if (existing == null) return;
        _budgetDb.ProjectAccountAssignments.Remove(existing);
        await _budgetDb.SaveChangesAsync();
    }

    /// <summary>
    /// Returns the project's real per-day balance delta for the calendar year, starting from the
    /// project-accounts' actual combined balance on Jan 1. Deltas reflect transactions on assigned
    /// accounts filtered by scope: Exclusive includes every transaction in the account; Shared
    /// includes only transactions whose category is in the project's tracked-categories set.
    ///
    /// Starting balance is computed by reversing ALL transactions on the assigned accounts from
    /// year start through "now" out of the live account balance — so the line is anchored to a
    /// real-world number regardless of scope. Days beyond today have zero delta; the caller can
    /// layer projection on top.
    /// </summary>
    public async Task<ProjectDailyDeltaResult> GetProjectActualDailyDeltaAsync(int projectId, int year)
    {
        var project = await _budgetDb.Projects
            .Where(p => p.Id == projectId)
            .Select(p => new { p.Id, p.SourceId })
            .FirstOrDefaultAsync();
        if (project == null) return new ProjectDailyDeltaResult { DailyDelta = Array.Empty<decimal>() };
        var sourceId = project.SourceId;

        var assignments = await _budgetDb.ProjectAccountAssignments
            .Where(paa => paa.ProjectId == projectId)
            .ToListAsync();

        int daysInYear = DateTime.IsLeapYear(year) ? 366 : 365;
        var result = new ProjectDailyDeltaResult
        {
            DailyDelta = new decimal[daysInYear],
            HasAccounts = assignments.Any()
        };
        if (!result.HasAccounts) return result;

        var accountIds = assignments.Select(a => a.AccountId).ToHashSet();
        var exclusiveIds = assignments.Where(a => a.Scope == AccountScope.Exclusive).Select(a => a.AccountId).ToHashSet();
        var sharedIds = assignments.Where(a => a.Scope == AccountScope.Shared).Select(a => a.AccountId).ToHashSet();

        var accounts = await _budgetDb.Accounts
            .Where(a => a.SourceId == sourceId && accountIds.Contains(a.Id))
            .ToListAsync();
        decimal currentBalance = accounts.Sum(a => a.CurrentBalance ?? 0m);

        // Resolve project's leaf category IDs once — only needed when at least one account is Shared.
        HashSet<long> projectLeafCategoryIds = new();
        if (sharedIds.Any())
        {
            var trackingItems = await GetProjectTrackingItemsAsync(projectId);
            var items = trackingItems
                .Select(t => t.CategoryGroupId.HasValue
                    ? (t.CategoryGroupId.Value, CategoryItemType.Group)
                    : (t.CategoryId!.Value, CategoryItemType.Category))
                .ToList();
            var allCategoriesWithCounts = await GetAllCategoriesWithChildCountsAsync(sourceId);
            var itemToLeaf = BuildItemToLeafCategoryIds(items, allCategoriesWithCounts);
            projectLeafCategoryIds = itemToLeaf.Values.SelectMany(s => s).ToHashSet();
        }

        var yearStartUtc = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var yearStartTs = ((DateTimeOffset)yearStartUtc).ToUnixTimeSeconds();
        var yearEndTs = ((DateTimeOffset)yearStartUtc.AddYears(1)).ToUnixTimeSeconds();
        var nowTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Anchor the line on Jan 1 by reversing all booked transactions through "now" out of the
        // live balance. Both this sum and the per-day delta walk below MUST use the same upper
        // bound — MoneyMoney stores future-dated entries (scheduled payments) in the same table,
        // and including them in one but not the other lets currentBalance fall out of sync with
        // sum(DailyDelta), making balanceAtProjStart drift away from the real account balance.
        var clampedEnd = Math.Max(yearStartTs, Math.Min(nowTs, yearEndTs));

        decimal ytdSumAllScopes = 0m;
        if (clampedEnd > yearStartTs)
        {
            ytdSumAllScopes = (decimal)await _budgetDb.Transactions
                .Where(t => t.SourceId == sourceId)
                .Where(t => t.AccountId.HasValue && accountIds.Contains(t.AccountId.Value))
                .Where(t => t.Timestamp >= yearStartTs && t.Timestamp < clampedEnd)
                .SumAsync(t => t.Amount);
        }
        // selectedYear in the future: clampedEnd == yearStartTs, ytdSumAllScopes stays 0, and
        // StartingBalance defaults to currentBalance — a known approximation flagged in the UI.
        result.StartingBalance = currentBalance - ytdSumAllScopes;

        var yearTxns = await _budgetDb.Transactions
            .Where(t => t.SourceId == sourceId)
            .Where(t => t.AccountId.HasValue && accountIds.Contains(t.AccountId.Value))
            .Where(t => t.Timestamp >= yearStartTs && t.Timestamp < clampedEnd)
            .Select(t => new { AccountId = t.AccountId!.Value, t.CategoryId, t.Amount, t.Timestamp })
            .ToListAsync();

        foreach (var t in yearTxns)
        {
            bool include = exclusiveIds.Contains(t.AccountId)
                || (sharedIds.Contains(t.AccountId)
                    && t.CategoryId.HasValue
                    && projectLeafCategoryIds.Contains(t.CategoryId.Value));
            if (!include) continue;

            var date = DateTimeOffset.FromUnixTimeSeconds(t.Timestamp).UtcDateTime;
            int dayIndex = date.DayOfYear - 1;
            if (dayIndex < 0 || dayIndex >= daysInYear) continue;
            result.DailyDelta[dayIndex] += (decimal)t.Amount;
        }

        var today = DateTime.UtcNow.Date;
        if (year < today.Year) result.LastActualDayOfYear = daysInYear;
        else if (year > today.Year) result.LastActualDayOfYear = 0;
        else result.LastActualDayOfYear = today.DayOfYear;

        return result;
    }

    /// <summary>
    /// Returns when each registered MoneyMoney source was last imported (LastUploadAt) plus the
    /// account-refresh snapshot for each source. The dashboard uses this to flag stale data;
    /// projection numbers built on stale data silently lie, so a visible indicator matters.
    /// </summary>
    public async Task<List<DataFreshness>> GetDataFreshnessAsync()
    {
        var sources = await _budgetDb.ImportSources
            .OrderBy(s => s.Name)
            .ToListAsync();

        var accountsBySource = (await _budgetDb.Accounts
            .Select(a => new { a.SourceId, a.Id, a.Name, a.RefreshTimestamp, a.Error })
            .ToListAsync())
            .GroupBy(a => a.SourceId)
            .ToDictionary(g => g.Key, g => g.Select(a => new AccountFreshness
            {
                AccountId = a.Id,
                Name = a.Name,
                RefreshTimestamp = a.RefreshTimestamp,
                Error = a.Error
            }).ToList());

        return sources.Select(s => new DataFreshness
        {
            SourceId = s.Id,
            SourceName = s.Name,
            DbUploadedUtc = s.LastUploadAt,
            Accounts = accountsBySource.TryGetValue(s.Id, out var list) ? list : new List<AccountFreshness>()
        }).ToList();
    }

    /// <summary>
    /// Counts MoneyMoney transactions that aren't assigned to a real category and returns the
    /// summed absolute amount. Surfaced on the dashboard so users see when budget rollups are
    /// silently undercounting because transactions slipped past categorization in MoneyMoney —
    /// the absolute sum is the decision signal (€5,000 missing rent vs €50 of coffee).
    ///
    /// MoneyMoney represents "no category" by pinning the transaction to a sentinel category
    /// with an empty name (typically rowid=1) rather than leaving category_key NULL, so both
    /// shapes count. Split-parent rows (Splitted=1) carry that same sentinel as a placeholder
    /// — their real categories live on the child rows — so they're excluded to match
    /// MoneyMoney's own "Uncategorized" view.
    /// </summary>
    public async Task<Dictionary<int, UncategorizedSummary>> GetUncategorizedTransactionSummaryAsync()
    {
        // Group per source so the dashboard can render a banner per source. Two queries (count
        // and sum) per source — fine even at a dozen sources.
        var sourceIds = await _budgetDb.ImportSources.Select(s => s.Id).ToListAsync();
        var result = new Dictionary<int, UncategorizedSummary>();
        foreach (var sourceId in sourceIds)
        {
            var query = _budgetDb.Transactions
                .Where(t => t.SourceId == sourceId)
                .Where(t => t.Splitted != 1)
                .Where(t => t.CategoryId == null
                    || !_budgetDb.Categories.Any(c => c.SourceId == sourceId && c.Id == t.CategoryId && !string.IsNullOrEmpty(c.Name)));

            var count = await query.CountAsync();
            var absSum = count == 0 ? 0m : (decimal)await query.SumAsync(t => Math.Abs(t.Amount));
            result[sourceId] = new UncategorizedSummary { SourceId = sourceId, Count = count, AbsoluteAmount = absSum };
        }
        return result;
    }

    /// <summary>
    /// Returns the current combined balance of each project's assigned accounts in a single
    /// pass — two DB calls regardless of project count. Projects with no assignments are
    /// omitted; the caller treats absence as "no accounts linked".
    /// </summary>
    public async Task<Dictionary<int, ProjectBalanceSummary>> GetAllProjectsBalancesAsync()
    {
        var assignments = await _budgetDb.ProjectAccountAssignments.ToListAsync();
        if (!assignments.Any()) return new Dictionary<int, ProjectBalanceSummary>();

        // AccountIds are namespaced globally unique across sources, so a single Contains() query
        // resolves accounts for every project regardless of source.
        var accountIds = assignments.Select(a => a.AccountId).Distinct().ToList();
        var accounts = await _budgetDb.Accounts
            .Where(a => accountIds.Contains(a.Id))
            .ToListAsync();
        var accountById = accounts.ToDictionary(a => a.Id);

        var result = new Dictionary<int, ProjectBalanceSummary>();
        foreach (var grp in assignments.GroupBy(a => a.ProjectId))
        {
            decimal balance = 0m;
            int linkedCount = 0;
            foreach (var assignment in grp)
            {
                if (!accountById.TryGetValue(assignment.AccountId, out var acc)) continue;
                balance += acc.CurrentBalance ?? 0m;
                linkedCount++;
            }
            result[grp.Key] = new ProjectBalanceSummary
            {
                Balance = balance,
                AccountCount = linkedCount
            };
        }
        return result;
    }

    /// <summary>
    /// Returns a normalized day-of-month spending distribution per item, learned from real
    /// transactions. Used by the balance chart's projection so projected months show real-looking
    /// patterns (payday lumps, mid-month groceries, end-of-month rent) instead of a flat smear.
    ///
    /// Fallback ladder per item: trailing 12 months (≥3 transactions) → all-time (≥3) → a single
    /// lump on day 1. Weights normalize to sum=1 over days 1..31; callers must snap any weight
    /// past the target month's last day onto the last day (Feb gets the day-29/30/31 weight).
    /// </summary>
    public async Task<Dictionary<(long Id, CategoryItemType ItemType), DayOfMonthDistribution>>
        GetCategoryDayOfMonthWeightsAsync(
            IReadOnlyCollection<(long Id, CategoryItemType ItemType)> items,
            int sourceId)
    {
        const int MinSampleSize = 3;

        var result = new Dictionary<(long, CategoryItemType), DayOfMonthDistribution>();
        if (!items.Any()) return result;

        var allCategoriesWithCounts = await GetAllCategoriesWithChildCountsAsync(sourceId);
        var itemToLeafIds = BuildItemToLeafCategoryIds(items, allCategoriesWithCounts);

        var allLeafIds = itemToLeafIds.Values.SelectMany(s => s).Distinct().ToList();
        if (!allLeafIds.Any())
        {
            foreach (var item in items) result[item] = DayOfMonthDistribution.Day1Fallback();
            return result;
        }

        var nowTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var trailingStartTs = ((DateTimeOffset)DateTime.UtcNow.AddYears(-1)).ToUnixTimeSeconds();

        // Single transaction scan across all relevant leaves; trailing-year vs all-time are then
        // computed in-memory per item without further round trips.
        var allTxns = await _budgetDb.Transactions
            .Where(t => t.SourceId == sourceId)
            .Where(t => t.CategoryId.HasValue && allLeafIds.Contains(t.CategoryId.Value))
            .Select(t => new { CategoryId = t.CategoryId!.Value, t.Amount, t.Timestamp })
            .ToListAsync();

        var byLeaf = allTxns
            .GroupBy(t => t.CategoryId)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var item in items)
        {
            var leafs = itemToLeafIds[item];
            var sample = new List<(decimal AbsAmount, int DayOfMonth)>();
            int trailingCount = 0;
            foreach (var leaf in leafs)
            {
                if (!byLeaf.TryGetValue(leaf, out var list)) continue;
                foreach (var t in list)
                {
                    if (t.Timestamp < trailingStartTs || t.Timestamp > nowTs) continue;
                    var day = DateTimeOffset.FromUnixTimeSeconds(t.Timestamp).UtcDateTime.Day;
                    sample.Add((Math.Abs((decimal)t.Amount), day));
                    trailingCount++;
                }
            }

            if (trailingCount >= MinSampleSize)
            {
                result[item] = DayOfMonthDistribution.FromSample(sample, trailingCount, DistributionPatternSource.TrailingYear);
                continue;
            }

            // Fall back to all-time sample.
            sample.Clear();
            int allTimeCount = 0;
            foreach (var leaf in leafs)
            {
                if (!byLeaf.TryGetValue(leaf, out var list)) continue;
                foreach (var t in list)
                {
                    var day = DateTimeOffset.FromUnixTimeSeconds(t.Timestamp).UtcDateTime.Day;
                    sample.Add((Math.Abs((decimal)t.Amount), day));
                    allTimeCount++;
                }
            }

            result[item] = allTimeCount >= MinSampleSize
                ? DayOfMonthDistribution.FromSample(sample, allTimeCount, DistributionPatternSource.AllTime)
                : DayOfMonthDistribution.Day1Fallback();
        }

        return result;
    }

    #endregion
}

public class ProjectBalanceSummary
{
    public decimal Balance { get; set; }
    public int AccountCount { get; set; }
}

public class UncategorizedSummary
{
    public int SourceId { get; set; }
    public int Count { get; set; }
    public decimal AbsoluteAmount { get; set; }
}

public class DataFreshness
{
    public int SourceId { get; set; }
    public string SourceName { get; set; } = string.Empty;
    /// <summary>When this source was last uploaded via the /api/moneymoney-db/{name} endpoint. Null when nothing has been imported yet.</summary>
    public DateTime? DbUploadedUtc { get; set; }
    public List<AccountFreshness> Accounts { get; set; } = new();

    /// <summary>True when any account has an unread error message — indicates a refresh failure
    /// that may be masking missing transactions.</summary>
    public IEnumerable<AccountFreshness> AccountsWithErrors =>
        Accounts.Where(a => !string.IsNullOrWhiteSpace(a.Error));
}

public class AccountFreshness
{
    public long AccountId { get; set; }
    public string? Name { get; set; }
    public long? RefreshTimestamp { get; set; }
    public string? Error { get; set; }

    public DateTime? LastRefreshUtc => RefreshTimestamp.HasValue && RefreshTimestamp.Value > 0
        ? DateTimeOffset.FromUnixTimeSeconds(RefreshTimestamp.Value).UtcDateTime
        : null;
}

public enum DistributionPatternSource
{
    TrailingYear,
    AllTime,
    Day1Fallback
}

/// <summary>
/// Per-budget view of stored vs. effective monthly amounts. A group row's effective amount is
/// the sum of all stored amounts beneath it (when any are non-zero), or its own stored amount
/// otherwise — that's the rule that lets a group act as a roll-up of its sub-cats without a
/// separate "leftover" concept.
/// </summary>
public class BudgetEffectiveAmounts
{
    /// <summary>Raw amounts as persisted, keyed by (Id, ItemType).</summary>
    public Dictionary<(long Id, CategoryItemType ItemType), decimal[]> Stored { get; init; } = new();

    /// <summary>What each row should display: group rows get replaced by descendant sum when
    /// applicable, all others equal their stored amount.</summary>
    public Dictionary<(long Id, CategoryItemType ItemType), decimal[]> Effective { get; init; } = new();

    /// <summary>Group keys whose effective amount is computed from descendants — the UI should
    /// render these group inputs read-only.</summary>
    public HashSet<(long Id, CategoryItemType ItemType)> AutoSummed { get; init; } = new();
}

/// <summary>
/// Normalized 31-element day-of-month spending histogram for one tracked item.
/// <see cref="Weights"/>[i] is the share of monthly volume on day (i+1), summing to ~1.
/// </summary>
public class DayOfMonthDistribution
{
    public decimal[] Weights { get; set; } = new decimal[31];
    public DistributionPatternSource Source { get; set; }
    public int SampleSize { get; set; }

    public static DayOfMonthDistribution Day1Fallback()
    {
        var w = new decimal[31];
        w[0] = 1m;
        return new DayOfMonthDistribution { Weights = w, Source = DistributionPatternSource.Day1Fallback };
    }

    public static DayOfMonthDistribution FromSample(
        IReadOnlyCollection<(decimal AbsAmount, int DayOfMonth)> sample,
        int sampleSize,
        DistributionPatternSource source)
    {
        var weights = new decimal[31];
        decimal total = 0m;
        foreach (var (amt, day) in sample)
        {
            if (day < 1 || day > 31) continue;
            weights[day - 1] += amt;
            total += amt;
        }

        if (total > 0m)
        {
            for (int i = 0; i < 31; i++) weights[i] /= total;
        }
        else
        {
            // Zero-amount sample (rare — refunds netted to zero). Fall back to count-based by
            // re-walking; if even that is empty, distribute uniformly.
            int countTotal = sample.Count(s => s.DayOfMonth >= 1 && s.DayOfMonth <= 31);
            if (countTotal > 0)
            {
                foreach (var (_, day) in sample)
                {
                    if (day < 1 || day > 31) continue;
                    weights[day - 1] += 1m / countTotal;
                }
            }
            else
            {
                for (int i = 0; i < 31; i++) weights[i] = 1m / 31m;
            }
        }

        return new DayOfMonthDistribution
        {
            Weights = weights,
            Source = source,
            SampleSize = sampleSize
        };
    }
}

/// <summary>
/// Output of <see cref="BudgetService.GetProjectActualDailyDeltaAsync"/>: the starting balance
/// (real-world account balance at year start) and a per-day delta array of scope-filtered
/// transaction sums. The caller layers projection deltas on top and cumulates to produce a
/// balance line.
/// </summary>
public class ProjectDailyDeltaResult
{
    public decimal StartingBalance { get; set; }
    /// <summary>Length = days in year. Index 0 = Jan 1.</summary>
    public decimal[] DailyDelta { get; set; } = Array.Empty<decimal>();
    /// <summary>1-indexed day of year up to which DailyDelta is populated from real transactions
    /// (subsequent days are zero pending projection). Zero when the year is entirely in the future.</summary>
    public int LastActualDayOfYear { get; set; }
    public bool HasAccounts { get; set; }
}

public class ProjectSpendingSummary
{
    public int ProjectId { get; set; }
    public int SourceId { get; set; }
    public string ProjectName { get; set; } = string.Empty;
    public int Year { get; set; }
    public int FromMonth { get; set; }
    public int ToMonth { get; set; }
    public bool IsSingleMonth => FromMonth == ToMonth;
    public List<CategorySpending> CategorySpending { get; set; } = new();
    public decimal TotalBudget { get; set; }
    public decimal TotalSpent { get; set; }
    public decimal TotalRemaining { get; set; }

    /// <summary>True when an active Budget exists for the year and was used to populate Budget/Remaining.</summary>
    public bool HasBudget { get; set; }
    /// <summary>Sum of income across tracked income items for the month.</summary>
    public decimal MonthIncome { get; set; }
    /// <summary>Sum of expenses across tracked expense items for the month.</summary>
    public decimal MonthExpenses { get; set; }
    public decimal MonthNet => MonthIncome - MonthExpenses;
}

public class CategorySpending
{
    public long CategoryId { get; set; }
    public CategoryItemType ItemType { get; set; }
    public string CategoryName { get; set; } = string.Empty;
    public decimal Budget { get; set; }
    /// <summary>
    /// Month's amount in the item's natural direction: income amount for income items,
    /// expense amount for expense items. Always positive.
    /// </summary>
    public decimal Spent { get; set; }
    public decimal Remaining { get; set; }
    public bool IsIncome { get; set; }
    public decimal PercentUsed => Budget > 0 ? (Spent / Budget * 100) : 0;
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

public class MonthlySpending
{
    public int Year { get; set; }
    public int Month { get; set; }
    public decimal Amount { get; set; }
    public string MonthName => new DateTime(Year, Month, 1).ToString("MMM yyyy");
}

public class CategorySpendingDetail
{
    public long CategoryGroupId { get; set; }
    public string CategoryGroupName { get; set; } = string.Empty;
    public decimal Amount { get; set; }
}
