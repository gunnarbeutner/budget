using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;

namespace OpenBudget.Services;

public class BudgetService
{
    private readonly BudgetDbContext _budgetDb;
    private readonly CategoryHierarchyService _hierarchy;

    public BudgetService(BudgetDbContext budgetDb, CategoryHierarchyService hierarchy)
    {
        _budgetDb = budgetDb;
        _hierarchy = hierarchy;
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

        // Resolve tracked items from the project's live tracking config.
        var trackedKeys = project.TrackingItems.ToTrackedKeys().ToList();

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
        var effective = ComputeEffectiveAmounts(storedAmounts, allCategoriesWithCounts);

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

        // Budget total: skip auto-summed groups so a tracked group whose budget is rolled up
        // from sub-cats isn't double-counted alongside its descendants. TotalSpent uses the
        // leaf-union aggregate to avoid double-counting across a tracked group + tracked sub-cat.
        var expenseRows = categorySpending.Where(c => !c.IsIncome).ToList();
        var totalBudget = expenseRows
            .Where(c => !effective.AutoSummed.Contains((c.CategoryId, c.ItemType)))
            .Sum(c => c.Budget);

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
        var hits = await _budgetDb.Transactions
            .Where(t => t.SourceId == sourceId)
            .Where(t => t.CategoryId.HasValue && leafList.Contains(t.CategoryId.Value))
            .WithinUtc(range)
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

        var allCategoriesWithCounts = await _hierarchy.GetAllCategoriesWithChildCountsAsync(sourceId);
        var itemToLeafIds = CategoryHierarchyService.BuildItemToLeafCategoryIds(items, allCategoriesWithCounts);
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

        var range = TimeRange.Between(
            new DateTime(year, fromMonth, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(year, toMonth, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1));

        var raw = await _budgetDb.Transactions
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
            var range = TimeRange.Between(monthStart, monthStart.AddMonths(1));

            var spentCents = isIncome
                ? await _budgetDb.Transactions
                    .Where(t => t.SourceId == sourceId)
                    .Where(t => t.CategoryId == categoryId)
                    .WithinUtc(range)
                    .Where(t => t.Amount > 0)
                    .SumAsync(t => t.Amount)
                : await _budgetDb.Transactions
                    .Where(t => t.SourceId == sourceId)
                    .Where(t => t.CategoryId == categoryId)
                    .WithinUtc(range)
                    .Where(t => t.Amount < 0)
                    .SumAsync(t => -t.Amount);

            results.Add(new MonthlySpending
            {
                Year = monthStart.Year,
                Month = monthStart.Month,
                Amount = spentCents / 100m
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
        var categoryIds = await _hierarchy.GetAllCategoryIdsForGroupAsync(categoryGroupId, sourceId);

        if (!categoryIds.Any())
            return new List<MonthlySpending>();

        // Get calendar month boundaries - last N months including current month
        var now = DateTimeOffset.UtcNow;
        var startDate = new DateTimeOffset(new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc)).AddMonths(-(months - 1));

        var results = new List<MonthlySpending>();

        for (int i = 0; i < months; i++)
        {
            var monthStart = startDate.AddMonths(i);
            var range = TimeRange.Between(monthStart, monthStart.AddMonths(1));

            var spentCents = isIncome
                ? await _budgetDb.Transactions
                    .Where(t => t.SourceId == sourceId)
                    .Where(t => t.CategoryId.HasValue && categoryIds.Contains(t.CategoryId.Value))
                    .WithinUtc(range)
                    .Where(t => t.Amount > 0)
                    .SumAsync(t => t.Amount)
                : await _budgetDb.Transactions
                    .Where(t => t.SourceId == sourceId)
                    .Where(t => t.CategoryId.HasValue && categoryIds.Contains(t.CategoryId.Value))
                    .WithinUtc(range)
                    .Where(t => t.Amount < 0)
                    .SumAsync(t => -t.Amount);

            results.Add(new MonthlySpending
            {
                Year = monthStart.Year,
                Month = monthStart.Month,
                Amount = spentCents / 100m
            });
        }

        return results;
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
        var hierarchy = await _hierarchy.GetAllCategoriesWithChildCountsAsync(sourceId);
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

    #endregion

    #region Budget Prediction

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
        var hierarchy = await _hierarchy.GetAllCategoriesWithChildCountsAsync(sourceId);
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

        var allCategoriesWithCounts = await _hierarchy.GetAllCategoriesWithChildCountsAsync(sourceId);
        var itemToLeafIds = CategoryHierarchyService.BuildItemToLeafCategoryIds(items, allCategoriesWithCounts);

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

        var raw = await _budgetDb.Transactions
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

        var allCategoriesWithCounts = await _hierarchy.GetAllCategoriesWithChildCountsAsync(sourceId);
        var itemToLeafIds = CategoryHierarchyService.BuildItemToLeafCategoryIds(items, allCategoriesWithCounts);

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

        var raw = await _budgetDb.Transactions
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

        var trackedKeys = project.TrackingItems.ToTrackedKeys().ToList();
        if (!trackedKeys.Any()) return new List<int>();

        var allCategoriesWithCounts = await _hierarchy.GetAllCategoriesWithChildCountsAsync(project.SourceId);
        var itemToLeafIds = CategoryHierarchyService.BuildItemToLeafCategoryIds(trackedKeys, allCategoriesWithCounts);
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

        var allCategoriesWithCounts = await _hierarchy.GetAllCategoriesWithChildCountsAsync(sourceId);
        var itemToLeafIds = CategoryHierarchyService.BuildItemToLeafCategoryIds(items, allCategoriesWithCounts);

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

        var rawQuery = _budgetDb.Transactions
            .Where(t => t.SourceId == sourceId)
            .Where(t => t.CategoryId.HasValue && allLeafIds.Contains(t.CategoryId.Value))
            .WithinUtc(TimeRange.Between(startDate, startDate.AddMonths(months)));
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
            arr[monthIndex] += (isIncome ? t.Amount : -t.Amount) / 100m;
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
            var items = trackingItems.ToTrackedKeys().ToList();
            var allCategoriesWithCounts = await _hierarchy.GetAllCategoriesWithChildCountsAsync(sourceId);
            var itemToLeaf = CategoryHierarchyService.BuildItemToLeafCategoryIds(items, allCategoriesWithCounts);
            projectLeafCategoryIds = itemToLeaf.Values.SelectMany(s => s).ToHashSet();
        }

        var yearRange = TimeRange.Year(year);
        var nowTs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // Anchor the line on Jan 1 by reversing all booked transactions through "now" out of the
        // live balance. Both this sum and the per-day delta walk below MUST use the same upper
        // bound — MoneyMoney stores future-dated entries (scheduled payments) in the same table,
        // and including them in one but not the other lets currentBalance fall out of sync with
        // sum(DailyDelta), making balanceAtProjStart drift away from the real account balance.
        var clampedEnd = Math.Max(yearRange.StartTs, Math.Min(nowTs, yearRange.EndTsExclusive));
        var ytdRange = new TimeRange(yearRange.StartTs, clampedEnd);

        decimal ytdSumAllScopes = 0m;
        if (clampedEnd > yearRange.StartTs)
        {
            ytdSumAllScopes = (await _budgetDb.Transactions
                .Where(t => t.SourceId == sourceId)
                .Where(t => t.AccountId.HasValue && accountIds.Contains(t.AccountId.Value))
                .WithinUtc(ytdRange)
                .SumAsync(t => t.Amount)) / 100m;
        }
        // selectedYear in the future: clampedEnd == yearRange.StartTs, ytdSumAllScopes stays 0,
        // and StartingBalance defaults to currentBalance — a known approximation flagged in the UI.
        result.StartingBalance = currentBalance - ytdSumAllScopes;

        var yearTxns = await _budgetDb.Transactions
            .Where(t => t.SourceId == sourceId)
            .Where(t => t.AccountId.HasValue && accountIds.Contains(t.AccountId.Value))
            .WithinUtc(ytdRange)
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
            result.DailyDelta[dayIndex] += t.Amount / 100m;
        }

        var today = DateTime.UtcNow.Date;
        if (year < today.Year) result.LastActualDayOfYear = daysInYear;
        else if (year > today.Year) result.LastActualDayOfYear = 0;
        else result.LastActualDayOfYear = today.DayOfYear;

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

        var allCategoriesWithCounts = await _hierarchy.GetAllCategoriesWithChildCountsAsync(sourceId);
        var itemToLeafIds = CategoryHierarchyService.BuildItemToLeafCategoryIds(items, allCategoriesWithCounts);

        var allLeafIds = itemToLeafIds.Values.SelectMany(s => s).Distinct().ToList();
        if (!allLeafIds.Any())
        {
            foreach (var item in items) result[item] = DayOfMonthDistribution.Day1Fallback();
            return result;
        }

        // Trailing-year window for the primary sample. Half-open per TimeRange convention —
        // a transaction at exactly the upper-bound second is treated as "future" and skipped.
        var nowUtc = DateTime.UtcNow;
        var trailingRange = TimeRange.Between(nowUtc.AddYears(-1), nowUtc);

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
                    if (t.Timestamp < trailingRange.StartTs || t.Timestamp >= trailingRange.EndTsExclusive) continue;
                    var day = DateTimeOffset.FromUnixTimeSeconds(t.Timestamp).UtcDateTime.Day;
                    sample.Add((Math.Abs(t.Amount) / 100m, day));
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
                    sample.Add((Math.Abs(t.Amount) / 100m, day));
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

