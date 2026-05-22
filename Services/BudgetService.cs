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
    /// Assign a category to a budget with a monthly limit
    /// </summary>
    public async Task AssignCategoryToProjectAsync(int projectId, long categoryId, decimal monthlyLimit)
    {
        // Check if assignment already exists
        var existing = await _budgetDb.ProjectCategoryAssignments
            .FirstOrDefaultAsync(a => a.ProjectId == projectId && a.CategoryId == categoryId);

        if (existing != null)
        {
            existing.MonthlyLimit = monthlyLimit;
        }
        else
        {
            var assignment = new ProjectCategoryAssignment
            {
                ProjectId = projectId,
                CategoryId = categoryId,
                MonthlyLimit = monthlyLimit
            };
            _budgetDb.ProjectCategoryAssignments.Add(assignment);
        }

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

        var startDate = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
        var endDate = startDate.AddMonths(1);

        var startTimestamp = ((DateTimeOffset)startDate).ToUnixTimeSeconds();
        var endTimestamp = ((DateTimeOffset)endDate).ToUnixTimeSeconds();

        var categorySpending = new List<CategorySpending>();

        foreach (var assignment in project.CategoryAssignments)
        {
            var categoryGroup = await _moneyMoneyDb.CategoryGroups
                .FirstOrDefaultAsync(cg => cg.Id == assignment.CategoryId);

            if (categoryGroup == null)
                continue;

            var categoryIds = await GetAllCategoryIdsForGroupAsync(assignment.CategoryId);

            // Get transactions (amounts are negative for expenses, so negate to get positive value)
            var spent = await _moneyMoneyDb.Transactions
                .Where(t => t.CategoryId.HasValue && categoryIds.Contains(t.CategoryId.Value))
                .Where(t => t.Timestamp >= startTimestamp && t.Timestamp < endTimestamp)
                .Where(t => t.Amount < 0)
                .SumAsync(t => -(decimal)t.Amount);

            categorySpending.Add(new CategorySpending
            {
                CategoryId = assignment.CategoryId,
                CategoryName = categoryGroup.Name ?? "Unknown",
                Budget = assignment.MonthlyLimit,
                Spent = spent,
                Remaining = assignment.MonthlyLimit - spent
            });
        }

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
    /// Get all category groups with their sub-group counts
    /// </summary>
    public async Task<List<CategoryGroupWithChildCount>> GetAllCategoriesWithChildCountsAsync()
    {
        var allCategoryGroups = await _moneyMoneyDb.CategoryGroups
            .OrderBy(cg => cg.Name)
            .ToListAsync();

        var result = new List<CategoryGroupWithChildCount>();

        foreach (var categoryGroup in allCategoryGroups)
        {
            // Skip category group 1 if it has no name (it's the root Umbuchungen system category)
            if (categoryGroup.Id == 1 && string.IsNullOrEmpty(categoryGroup.Name))
                continue;

            // Count direct children
            var childCount = allCategoryGroups.Count(cg => cg.ParentGroupId == categoryGroup.Id);

            // A category group is a root category if:
            // 1. It has no parent (group_key is null or 0), OR
            // 2. It's a child of category group 1 (Umbuchungen root) which we're hiding
            var isRootCategory = categoryGroup.ParentGroupId == null ||
                                 categoryGroup.ParentGroupId == 0 ||
                                 categoryGroup.ParentGroupId == 1;

            result.Add(new CategoryGroupWithChildCount
            {
                CategoryGroup = categoryGroup,
                DirectChildCount = childCount,
                IsRootCategory = isRootCategory,
                ParentCategoryGroupId = categoryGroup.ParentGroupId
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
        var endDate = DateTimeOffset.UtcNow;
        var startDate = endDate.AddMonths(-months);

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

        var endDate = DateTimeOffset.UtcNow;
        var startDate = endDate.AddMonths(-months);

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
            .FirstOrDefaultAsync(e => e.BudgetId == entry.BudgetId && e.CategoryId == entry.CategoryId);

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

        // Create monthly budget entries
        foreach (var (categoryId, monthlyAmounts) in predictions)
        {
            var entry = new MonthlyBudgetEntry
            {
                BudgetId = budget.Id,
                CategoryId = categoryId,
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

public class CategoryGroupWithChildCount
{
    public CategoryGroup CategoryGroup { get; set; } = null!;
    public int DirectChildCount { get; set; }
    public bool IsRootCategory { get; set; }
    public long? ParentCategoryGroupId { get; set; }
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
