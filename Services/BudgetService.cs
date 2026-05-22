using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;

namespace OpenBudget.Services;

public class BudgetService
{
    private readonly BudgetDbContext _budgetDb;
    private readonly MoneyMoneyDbContext _moneyMoneyDb;

    public BudgetService(BudgetDbContext budgetDb, MoneyMoneyDbContext moneyMoneyDb)
    {
        _budgetDb = budgetDb;
        _moneyMoneyDb = moneyMoneyDb;
    }

    /// <summary>
    /// Get all budgets
    /// </summary>
    public async Task<List<Project>> GetProjectsAsync()
    {
        return await _budgetDb.Projects
            .Include(b => b.CategoryAssignments)
            .OrderByDescending(b => b.CreatedAt)
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
    /// Create a new budget
    /// </summary>
    public async Task<Project> CreateProjectAsync(string name, string? description = null)
    {
        var project = new Project
        {
            Name = name,
            Description = description,
            CreatedAt = DateTime.UtcNow
        };

        _budgetDb.Projects.Add(project);
        await _budgetDb.SaveChangesAsync();

        return project;
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
    /// Calculate average monthly spending for a category group and all its categories/sub-groups
    /// over the last N months
    /// </summary>
    public async Task<decimal> CalculateAverageMonthlySpendingAsync(long categoryGroupId, int months = 12)
    {
        // Get all category IDs for this group (including sub-groups)
        var categoryIds = await GetAllCategoryIdsForGroupAsync(categoryGroupId);

        if (!categoryIds.Any())
            return 0;

        // Calculate date range
        var endDate = DateTimeOffset.UtcNow;
        var startDate = endDate.AddMonths(-months);

        var startTimestamp = startDate.ToUnixTimeSeconds();
        var endTimestamp = endDate.ToUnixTimeSeconds();

        // Get all transactions for these categories in the time range
        var transactions = await _moneyMoneyDb.Transactions
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
    /// Get spending summary for a budget in a specific month/year
    /// </summary>
    public async Task<ProjectSpendingSummary> GetProjectSpendingSummaryAsync(int projectId, int year, int month)
    {
        var project = await GetProjectAsync(projectId);
        if (project == null)
            throw new ArgumentException("Project not found", nameof(projectId));

        // Budget amounts live on the Budget model now; without an active budget there's nothing
        // to compare against, so every assignment reports a budget of 0. Assignments are
        // historically Group references — none of the legacy code stored Category IDs here.
        var entries = project.CategoryAssignments
            .Select(a => (a.CategoryId, ItemType: CategoryItemType.Group, Budget: 0m))
            .ToList();

        var categorySpending = await BuildCategorySpendingAsync(year, month, entries);

        return new ProjectSpendingSummary
        {
            ProjectId = projectId,
            ProjectName = project.Name,
            Year = year,
            Month = month,
            CategorySpending = categorySpending,
            TotalBudget = categorySpending.Sum(c => c.Budget),
            TotalSpent = categorySpending.Sum(c => c.Spent),
            TotalRemaining = categorySpending.Sum(c => c.Remaining)
        };
    }

    /// <summary>
    /// Get spending summary for a budget in a specific month using MonthlyBudgetEntries
    /// </summary>
    public async Task<ProjectSpendingSummary> GetBudgetSpendingSummaryAsync(int budgetId, int month)
    {
        var budget = await GetBudgetAsync(budgetId);
        if (budget == null)
            throw new ArgumentException("Budget not found", nameof(budgetId));

        var entries = budget.MonthlyEntries
            .Select(e => (e.CategoryId, e.ItemType, Budget: e.GetMonthAmount(month)))
            .ToList();

        var categorySpending = await BuildCategorySpendingAsync(budget.Year, month, entries);

        return new ProjectSpendingSummary
        {
            ProjectId = budget.ProjectId,
            ProjectName = budget.Project?.Name ?? "Unknown",
            Year = budget.Year,
            Month = month,
            CategorySpending = categorySpending,
            TotalBudget = categorySpending.Sum(c => c.Budget),
            TotalSpent = categorySpending.Sum(c => c.Spent),
            TotalRemaining = categorySpending.Sum(c => c.Remaining)
        };
    }

    /// <summary>
    /// Builds the per-row spent/remaining values for a set of budget entries, with one batched
    /// transactions query for the month. Entries are disambiguated by (CategoryId, ItemType).
    /// </summary>
    private async Task<List<CategorySpending>> BuildCategorySpendingAsync(
        int year,
        int month,
        List<(long CategoryId, CategoryItemType ItemType, decimal Budget)> entries)
    {
        var startTimestamp = ((DateTimeOffset)new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc)).ToUnixTimeSeconds();
        var endTimestamp = ((DateTimeOffset)new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1)).ToUnixTimeSeconds();

        var allCategoriesWithCounts = await GetAllCategoriesWithChildCountsAsync();
        var groupNameLookup = allCategoriesWithCounts
            .Where(c => c.ItemType == CategoryItemType.Group)
            .ToDictionary(c => c.Id, c => c.CategoryGroup.Name ?? "Unknown");
        var leafCategoryNameLookup = (await _moneyMoneyDb.Categories.ToListAsync())
            .ToDictionary(c => c.Id, c => c.Name ?? "Unknown");

        // Build the leaf transaction-category ID set per entry, keyed by (id, itemType).
        var entryLeafIds = new Dictionary<(long Id, CategoryItemType ItemType), HashSet<long>>();
        var entryNames = new Dictionary<(long Id, CategoryItemType ItemType), string>();
        foreach (var (entryId, itemType, _) in entries)
        {
            var key = (entryId, itemType);
            if (entryLeafIds.ContainsKey(key)) continue;

            var leafIds = new HashSet<long>();
            string? name;
            if (itemType == CategoryItemType.Category)
            {
                if (!leafCategoryNameLookup.TryGetValue(entryId, out var catName)) continue;
                leafIds.Add(entryId);
                name = catName;
            }
            else
            {
                if (!groupNameLookup.TryGetValue(entryId, out var groupName)) continue;
                name = groupName;
                var descendants = CategoryGroupWithChildCount.CollectDescendants(
                    entryId, allCategoriesWithCounts, includeLeafCategories: true);
                foreach (var d in descendants)
                {
                    if (d.ItemType == CategoryItemType.Category)
                        leafIds.Add(d.Id);
                }
            }

            entryLeafIds[key] = leafIds;
            entryNames[key] = name ?? "Unknown";
        }

        // Single batched query for spending in the month
        var allLeafIds = entryLeafIds.Values.SelectMany(s => s).Distinct().ToList();
        var spendingByLeaf = new Dictionary<long, decimal>();
        if (allLeafIds.Any())
        {
            var raw = await _moneyMoneyDb.Transactions
                .Where(t => t.CategoryId.HasValue && allLeafIds.Contains(t.CategoryId.Value))
                .Where(t => t.Timestamp >= startTimestamp && t.Timestamp < endTimestamp)
                .Where(t => t.Amount < 0)
                .Select(t => new { CategoryId = t.CategoryId!.Value, t.Amount })
                .ToListAsync();
            foreach (var t in raw)
            {
                spendingByLeaf[t.CategoryId] = spendingByLeaf.GetValueOrDefault(t.CategoryId) + -(decimal)t.Amount;
            }
        }

        var result = new List<CategorySpending>();
        foreach (var (entryId, itemType, budgetAmount) in entries)
        {
            var key = (entryId, itemType);
            if (!entryLeafIds.TryGetValue(key, out var leafIds)) continue;
            var spent = leafIds.Sum(id => spendingByLeaf.GetValueOrDefault(id));
            result.Add(new CategorySpending
            {
                CategoryId = entryId,
                CategoryName = entryNames[key],
                Budget = budgetAmount,
                Spent = spent,
                Remaining = budgetAmount - spent
            });
        }

        return result;
    }

    /// <summary>
    /// Get all category IDs for a category group (including all sub-groups and their categories)
    /// </summary>
    private async Task<List<long>> GetAllCategoryIdsForGroupAsync(long categoryGroupId)
    {
        var categoryIds = new List<long>();

        // Get all category group IDs (including sub-groups)
        var groupIds = await GetAllCategoryGroupIdsRecursiveAsync(categoryGroupId);

        // Get all categories that belong to these groups
        foreach (var groupId in groupIds)
        {
            var categoriesInGroup = await _moneyMoneyDb.Categories
                .Where(c => c.GroupId == groupId)
                .Select(c => c.Id)
                .ToListAsync();

            categoryIds.AddRange(categoriesInGroup);
        }

        return categoryIds;
    }

    /// <summary>
    /// Recursively get all category group IDs including sub-groups
    /// </summary>
    private async Task<List<long>> GetAllCategoryGroupIdsRecursiveAsync(long categoryGroupId)
    {
        var groupIds = new List<long> { categoryGroupId };

        // Get sub-groups
        var subGroups = await _moneyMoneyDb.CategoryGroups
            .Where(cg => cg.ParentGroupId == categoryGroupId)
            .ToListAsync();

        foreach (var subGroup in subGroups)
        {
            var subGroupIds = await GetAllCategoryGroupIdsRecursiveAsync(subGroup.Id);
            groupIds.AddRange(subGroupIds);
        }

        return groupIds;
    }

    /// <summary>
    /// Get all categories (from categories table)
    /// </summary>
    public async Task<List<Category>> GetAllCategoriesAsync()
    {
        return await _moneyMoneyDb.Categories
            .OrderBy(c => c.Name)
            .ToListAsync();
    }

    /// <summary>
    /// Get all category groups and individual categories with their sub-group counts
    /// </summary>
    public async Task<List<CategoryGroupWithChildCount>> GetAllCategoriesWithChildCountsAsync()
    {
        var allCategoryGroups = await _moneyMoneyDb.CategoryGroups
            .OrderBy(cg => cg.Name)
            .ToListAsync();

        var allCategories = await _moneyMoneyDb.Categories
            .OrderBy(c => c.Name)
            .ToListAsync();

        var result = new List<CategoryGroupWithChildCount>();

        foreach (var categoryGroup in allCategoryGroups)
        {
            // Skip category group 1 if it has no name (it's the root Umbuchungen system category)
            if (categoryGroup.Id == 1 && string.IsNullOrEmpty(categoryGroup.Name))
                continue;

            // Count direct children (both sub-groups and individual categories)
            var subGroupCount = allCategoryGroups.Count(cg => cg.ParentGroupId == categoryGroup.Id);
            var categoryCount = allCategories.Count(c => c.GroupId == categoryGroup.Id);
            var childCount = subGroupCount + categoryCount;

            // A category group is a root category if:
            // 1. It has no parent (group_key is null or 0), OR
            // 2. It's a child of category group 1 (Umbuchungen root) which we're hiding
            var isRootCategory = categoryGroup.ParentGroupId == null ||
                                 categoryGroup.ParentGroupId == 0 ||
                                 categoryGroup.ParentGroupId == 1;

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
    /// Get all root category groups (category groups with no parent or parent is the root category group with id=1)
    /// </summary>
    public async Task<List<CategoryGroup>> GetRootCategoriesAsync()
    {
        return await _moneyMoneyDb.CategoryGroups
            .Where(cg => cg.ParentGroupId == null || cg.ParentGroupId == 0 || cg.ParentGroupId == 1)
            .Where(cg => cg.Id != 1) // Exclude the empty root category group
            .OrderBy(cg => cg.Name)
            .ToListAsync();
    }

    /// <summary>
    /// Get a category group by ID
    /// </summary>
    public async Task<CategoryGroup?> GetCategoryWithSubCategoriesAsync(long categoryGroupId)
    {
        return await _moneyMoneyDb.CategoryGroups
            .FirstOrDefaultAsync(cg => cg.Id == categoryGroupId);
    }

    /// <summary>
    /// Get monthly spending for an individual category over the last N months
    /// </summary>
    public async Task<List<MonthlySpending>> GetMonthlySpendingForCategoryAsync(long categoryId, int months = 12)
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

            var spent = await _moneyMoneyDb.Transactions
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
    /// Get monthly spending for a category group over the last N months
    /// </summary>
    public async Task<List<MonthlySpending>> GetMonthlySpendingAsync(long categoryGroupId, int months = 12)
    {
        var categoryIds = await GetAllCategoryIdsForGroupAsync(categoryGroupId);

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

            var spent = await _moneyMoneyDb.Transactions
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
    /// Get spending breakdown by category for a specific month
    /// </summary>
    public async Task<List<CategorySpendingDetail>> GetCategorySpendingForMonthAsync(
        int year,
        int month,
        int projectId = 0,
        HashSet<long>? selectedCategoryGroupIds = null,
        HashSet<long>? selectedCategoryIds = null)
    {
        var startDate = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
        var endDate = startDate.AddMonths(1);

        var startTimestamp = ((DateTimeOffset)startDate).ToUnixTimeSeconds();
        var endTimestamp = ((DateTimeOffset)endDate).ToUnixTimeSeconds();

        List<CategoryGroup> categoryGroupsToProcess;

        if (projectId > 0)
        {
            // Get only category groups assigned to this budget (including all descendants)
            var project = await GetProjectAsync(projectId);
            if (project == null)
                return new List<CategorySpendingDetail>();

            var budgetCategoryGroupIds = new HashSet<long>();
            foreach (var assignment in project.CategoryAssignments)
            {
                budgetCategoryGroupIds.Add(assignment.CategoryId);
                var subGroupIds = await GetAllCategoryGroupIdsRecursiveAsync(assignment.CategoryId);
                foreach (var subGroupId in subGroupIds)
                {
                    budgetCategoryGroupIds.Add(subGroupId);
                }
            }

            categoryGroupsToProcess = await _moneyMoneyDb.CategoryGroups
                .Where(cg => budgetCategoryGroupIds.Contains(cg.Id))
                .OrderBy(cg => cg.Name)
                .ToListAsync();
        }
        else
        {
            // Get ALL category groups (not just roots)
            categoryGroupsToProcess = await _moneyMoneyDb.CategoryGroups
                .Where(cg => cg.Id != 1)  // Exclude the hidden root category
                .OrderBy(cg => cg.Name)
                .ToListAsync();
        }

        var results = new List<CategorySpendingDetail>();

        // Process selected category groups
        if (selectedCategoryGroupIds != null && selectedCategoryGroupIds.Any())
        {
            var groupsToProcess = categoryGroupsToProcess
                .Where(cg => selectedCategoryGroupIds.Contains(cg.Id))
                .ToList();

            foreach (var categoryGroup in groupsToProcess)
            {
                var categoryIds = await GetAllCategoryIdsForGroupAsync(categoryGroup.Id);

                if (!categoryIds.Any())
                    continue;

                var spent = await _moneyMoneyDb.Transactions
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
                var category = await _moneyMoneyDb.Categories
                    .FirstOrDefaultAsync(c => c.Id == categoryId);

                if (category == null)
                    continue;

                var spent = await _moneyMoneyDb.Transactions
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
    /// Create a new budget
    /// </summary>
    public async Task<Budget> CreateBudgetAsync(int projectId, int year, string? notes = null)
    {
        var budget = new Budget
        {
            ProjectId = projectId,
            Year = year,
            Notes = notes,
            CreatedAt = DateTime.UtcNow
        };

        _budgetDb.Budgets.Add(budget);
        await _budgetDb.SaveChangesAsync();

        return budget;
    }

    /// <summary>
    /// Update a budget
    /// </summary>
    public async Task UpdateBudgetAsync(Budget budget)
    {
        budget.LastModifiedAt = DateTime.UtcNow;
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
            var monthlyPredictions = await PredictMonthlySpendingForCategoryAsync(assignment.CategoryId, targetYear);
            predictions[assignment.CategoryId] = monthlyPredictions;
        }

        return predictions;
    }

    /// <summary>
    /// Predict monthly spending for a specific category group using seasonal adjustment
    /// This looks at the same month last year and adjusts based on overall trend
    /// </summary>
    public async Task<decimal[]> PredictMonthlySpendingForCategoryAsync(long categoryGroupId, int targetYear)
    {
        var predictions = new decimal[12];

        // Get 2 years of historical data
        var monthlyData = await GetMonthlySpendingAsync(categoryGroupId, 24);

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

        var allCategories = await GetAllCategoriesWithChildCountsAsync();

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
    public async Task<(decimal[] Income, decimal[] Expenses)> GetMonthlyIncomeAndExpenseAsync(long categoryGroupId, int year, int? projectId = null)
    {
        // Check if this is a leaf category (has no children) or a category group
        var isLeafCategory = await _moneyMoneyDb.Categories.AnyAsync(c => c.Id == categoryGroupId);

        List<long> categoryIds;
        if (isLeafCategory)
        {
            // It's a leaf category, use it directly
            categoryIds = new List<long> { categoryGroupId };
        }
        else
        {
            // It's a category group, get all descendant categories
            categoryIds = await GetAllCategoryIdsForGroupAsync(categoryGroupId);
        }

        if (!categoryIds.Any())
            return (new decimal[12], new decimal[12]);

        HashSet<long> incomeCategoryIds;
        HashSet<long> expenseCategoryIds;

        if (projectId.HasValue)
        {
            // Use project-specific category type assignments with inheritance
            var allCategories = await GetAllCategoriesWithChildCountsAsync();
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
            var categories = await _moneyMoneyDb.Categories
                .Where(c => categoryIds.Contains(c.Id))
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
                var monthlyIncome = await _moneyMoneyDb.Transactions
                    .Where(t => t.CategoryId.HasValue && incomeCategoryIds.Contains(t.CategoryId.Value))
                    .Where(t => t.Timestamp >= startTimestamp && t.Timestamp < endTimestamp)
                    .SumAsync(t => (decimal)t.Amount);

                income[month - 1] = monthlyIncome;
            }

            // Get expenses from expense categories (sum all amounts, negate to make positive)
            if (expenseCategoryIds.Any())
            {
                var monthlyExpense = await _moneyMoneyDb.Transactions
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
            int? projectId)
    {
        var result = new Dictionary<(long, CategoryItemType), (decimal[], decimal[])>();
        foreach (var item in items)
            result[item] = (new decimal[12], new decimal[12]);

        if (!items.Any()) return result;

        var allCategoriesWithCounts = await GetAllCategoriesWithChildCountsAsync();
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
            var types = await _moneyMoneyDb.Categories
                .Where(c => allLeafIds.Contains(c.Id))
                .Select(c => new { c.Id, c.Type })
                .ToListAsync();
            leafIsIncome = types.ToDictionary(c => c.Id, c => c.Type == 2);
        }

        var yearStart = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var startTs = ((DateTimeOffset)yearStart).ToUnixTimeSeconds();
        var endTs = ((DateTimeOffset)yearStart.AddYears(1)).ToUnixTimeSeconds();

        var raw = await _moneyMoneyDb.Transactions
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
    /// Batched version of GetMonthlySpendingAsync / GetMonthlySpendingForCategoryAsync. Returns
    /// monthly outflow (negative transactions, negated to positive) for the last N calendar months.
    /// Items with no leaf categories get an empty list, matching the per-item method's behavior.
    /// </summary>
    public async Task<Dictionary<(long Id, CategoryItemType ItemType), List<MonthlySpending>>>
        GetMonthlySpendingBatchAsync(
            IReadOnlyCollection<(long Id, CategoryItemType ItemType)> items,
            int months)
    {
        var result = new Dictionary<(long, CategoryItemType), List<MonthlySpending>>();
        foreach (var item in items)
            result[item] = new List<MonthlySpending>();

        if (!items.Any() || months <= 0) return result;

        var now = DateTimeOffset.UtcNow;
        var startDate = new DateTimeOffset(new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc))
            .AddMonths(-(months - 1));

        var allCategoriesWithCounts = await GetAllCategoriesWithChildCountsAsync();
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

        var raw = await _moneyMoneyDb.Transactions
            .Where(t => t.CategoryId.HasValue && allLeafIds.Contains(t.CategoryId.Value))
            .Where(t => t.Timestamp >= startTs && t.Timestamp < endTs)
            .Where(t => t.Amount < 0)
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
            arr[monthIndex] += -(decimal)t.Amount;
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
    /// the already-loaded hierarchy to avoid additional DB calls.
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
}

public class ProjectSpendingSummary
{
    public int ProjectId { get; set; }
    public string ProjectName { get; set; } = string.Empty;
    public int Year { get; set; }
    public int Month { get; set; }
    public List<CategorySpending> CategorySpending { get; set; } = new();
    public decimal TotalBudget { get; set; }
    public decimal TotalSpent { get; set; }
    public decimal TotalRemaining { get; set; }
}

public class CategorySpending
{
    public long CategoryId { get; set; }
    public string CategoryName { get; set; } = string.Empty;
    public decimal Budget { get; set; }
    public decimal Spent { get; set; }
    public decimal Remaining { get; set; }
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
