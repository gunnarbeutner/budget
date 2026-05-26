using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>
/// Budget + MonthlyBudgetEntry + BudgetContributionSplit CRUD, plus the effective-amount
/// rollup (which lives here because it operates on stored entries). Carved out of
/// BudgetService — no transaction sums or projections here.
/// </summary>
public class BudgetEntryService
{
    private readonly BudgetDbContext _db;
    private readonly CategoryHierarchyService _hierarchy;

    public BudgetEntryService(BudgetDbContext db, CategoryHierarchyService hierarchy)
    {
        _db = db;
        _hierarchy = hierarchy;
    }

    public async Task<List<Budget>> GetBudgetsForProjectAsync(int projectId)
    {
        return await _db.Budgets
            .Where(b => b.ProjectId == projectId)
            .Include(b => b.MonthlyEntries)
            .OrderByDescending(b => b.Year)
            .ThenByDescending(b => b.IsActive)
            .ThenByDescending(b => b.LastModifiedAt ?? b.CreatedAt)
            .ToListAsync();
    }

    public async Task<Budget?> GetBudgetAsync(int budgetId)
    {
        return await _db.Budgets
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
            var siblings = await _db.Budgets
                .Where(b => b.ProjectId == projectId && b.Year == year && b.IsActive)
                .ToListAsync();
            foreach (var sibling in siblings)
                sibling.IsActive = false;
        }

        _db.Budgets.Add(budget);
        await _db.SaveChangesAsync();
        return budget;
    }

    /// <summary>Mark a budget as the active one for its (project, year), deactivating siblings.</summary>
    public async Task SetActiveBudgetAsync(int budgetId)
    {
        var target = await _db.Budgets.FindAsync(budgetId);
        if (target == null) return;

        var siblings = await _db.Budgets
            .Where(b => b.ProjectId == target.ProjectId && b.Year == target.Year && b.Id != target.Id && b.IsActive)
            .ToListAsync();
        foreach (var sibling in siblings)
            sibling.IsActive = false;

        target.IsActive = true;
        await _db.SaveChangesAsync();
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
            var siblings = await _db.Budgets
                .Where(b => b.ProjectId == budget.ProjectId
                            && b.Year == budget.Year
                            && b.Id != budget.Id
                            && b.IsActive)
                .ToListAsync();
            foreach (var sibling in siblings)
                sibling.IsActive = false;
        }

        _db.Budgets.Update(budget);
        await _db.SaveChangesAsync();
    }

    public async Task DeleteBudgetAsync(int budgetId)
    {
        var budget = await _db.Budgets.FindAsync(budgetId);
        if (budget != null)
        {
            _db.Budgets.Remove(budget);
            await _db.SaveChangesAsync();
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
        var budget = await _db.Budgets.FindAsync(budgetId);
        if (budget == null)
            throw new ArgumentException("Budget not found", nameof(budgetId));

        var project = await _db.Projects.FindAsync(budget.ProjectId);
        var sourceId = project?.SourceId ?? SourceScope.LegacySourceId;
        var hierarchy = await _hierarchy.GetAllCategoriesWithChildCountsAsync(sourceId);
        var normalized = NormalizeBudgetEntries(entries, hierarchy);

        var existing = await _db.MonthlyBudgetEntries
            .Where(e => e.BudgetId == budgetId)
            .ToListAsync();

        var desiredKeys = normalized.Select(e => (e.Id, e.ItemType)).ToHashSet();
        foreach (var row in existing.Where(e => !desiredKeys.Contains((e.CategoryId, e.ItemType))))
        {
            _db.MonthlyBudgetEntries.Remove(row);
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
                _db.MonthlyBudgetEntries.Add(row);
            }
            for (int m = 1; m <= 12; m++)
                row.SetMonthAmount(m, months[m - 1]);
        }

        budget.LastModifiedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Enforce the one-level-per-subtree invariant in the entries being saved: any Group entry
    /// whose descendants in the same set have non-zero amounts gets its own months zeroed. The
    /// UI should prevent this state, but this guards against drift between the UI's editing
    /// state and what's submitted.
    /// </summary>
    internal static List<(long Id, CategoryItemType ItemType, decimal[] Months)> NormalizeBudgetEntries(
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
    /// Returns this budget's stored Income Contribution splits as a dictionary keyed by
    /// (CategoryId, ItemType). Empty dictionary if no splits are saved.
    /// </summary>
    public async Task<Dictionary<(long Id, CategoryItemType ItemType), decimal>> GetBudgetContributionSplitsAsync(int budgetId)
    {
        var rows = await _db.BudgetContributionSplits
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
        var existing = await _db.BudgetContributionSplits
            .Where(s => s.BudgetId == budgetId)
            .ToListAsync();

        var desiredKeys = splits.Select(s => (s.Id, s.ItemType)).ToHashSet();
        foreach (var row in existing.Where(r => !desiredKeys.Contains((r.CategoryId, r.ItemType))))
        {
            _db.BudgetContributionSplits.Remove(row);
        }

        foreach (var (id, itemType, percent) in splits)
        {
            var row = existing.FirstOrDefault(r => r.CategoryId == id && r.ItemType == itemType);
            if (row == null)
            {
                _db.BudgetContributionSplits.Add(new BudgetContributionSplit
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

        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Returns the 12-month budget amounts for every MonthlyBudgetEntry on a budget, keyed by
    /// (CategoryId, ItemType) — Group and Category IDs share a numeric space so the
    /// discriminator is required.
    /// </summary>
    public async Task<Dictionary<(long Id, CategoryItemType ItemType), decimal[]>> GetMonthlyBudgetAmountsAsync(int budgetId)
    {
        var entries = await _db.MonthlyBudgetEntries
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
        var budget = await _db.Budgets
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
}
