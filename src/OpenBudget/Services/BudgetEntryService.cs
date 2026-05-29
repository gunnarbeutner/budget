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
    private readonly IDbContextFactory<BudgetDbContext> _dbFactory;
    private readonly CategoryHierarchyService _hierarchy;
    private readonly ChangeNotificationService _notifier;
    private readonly ChangeOrigin _origin;

    public BudgetEntryService(
        IDbContextFactory<BudgetDbContext> dbFactory,
        CategoryHierarchyService hierarchy,
        ChangeNotificationService notifier,
        ChangeOrigin origin)
    {
        _dbFactory = dbFactory;
        _hierarchy = hierarchy;
        _notifier = notifier;
        _origin = origin;
    }

    // Publish a change stamped with this circuit's origin so our own components skip the echo.
    private void Notify(DataChange change) => _notifier.Publish(change, _origin.Id);

    public async Task<List<Budget>> GetBudgetsForProjectAsync(int projectId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.Budgets
            .AsNoTracking()
            .Where(b => b.ProjectId == projectId)
            .Include(b => b.MonthlyEntries)
            .OrderByDescending(b => b.Year)
            .ThenByDescending(b => b.IsActive)
            .ThenByDescending(b => b.LastModifiedAt ?? b.CreatedAt)
            .ToListAsync();
    }

    public async Task<Budget?> GetBudgetAsync(int budgetId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.Budgets
            .AsNoTracking()
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
    // A budget is a yearly plan; clamp to a sane window so a typo can't persist year 0 / 99999.
    // Matches the editor's <input min/max>.
    internal const int MinBudgetYear = 2000;
    internal const int MaxBudgetYear = 2100;

    /// <summary>Trim + reject an empty or over-long budget name. Returns the normalized value.</summary>
    internal static string ValidateAndNormalizeName(string? name)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length == 0)
            throw new ArgumentException("Budget name must not be empty.", nameof(name));
        if (trimmed.Length > 200)
            throw new ArgumentException("Budget name must be 200 characters or fewer.", nameof(name));
        return trimmed;
    }

    internal static void ValidateYear(int year)
    {
        if (year < MinBudgetYear || year > MaxBudgetYear)
            throw new ArgumentException($"Budget year must be between {MinBudgetYear} and {MaxBudgetYear}.", nameof(year));
    }

    public async Task<Budget> CreateBudgetAsync(int projectId, int year, string? name = null, string? notes = null, bool makeActive = true)
    {
        ValidateYear(year);
        await using var db = await _dbFactory.CreateDbContextAsync();
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
            var siblings = await db.Budgets
                .Where(b => b.ProjectId == projectId && b.Year == year && b.IsActive)
                .ToListAsync();
            foreach (var sibling in siblings)
                sibling.IsActive = false;
        }

        db.Budgets.Add(budget);
        await db.SaveChangesAsync();
        Notify(new DataChange(DataArea.Budgets, ProjectId: budget.ProjectId));
        return budget;
    }

    /// <summary>Mark a budget as the active one for its (project, year), deactivating siblings.</summary>
    public async Task SetActiveBudgetAsync(int budgetId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var target = await db.Budgets.FindAsync(budgetId);
        if (target == null) return;

        var siblings = await db.Budgets
            .Where(b => b.ProjectId == target.ProjectId && b.Year == target.Year && b.Id != target.Id && b.IsActive)
            .ToListAsync();
        foreach (var sibling in siblings)
            sibling.IsActive = false;

        target.IsActive = true;
        await db.SaveChangesAsync();
        Notify(new DataChange(DataArea.Budgets, ProjectId: target.ProjectId));
    }

    /// <summary>
    /// Update a budget. If the caller flipped IsActive on, deactivate every other budget for
    /// the same (project, year) in the same transaction so the unique-active invariant holds.
    /// </summary>
    public async Task UpdateBudgetAsync(Budget budget)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        // budget arrives detached from another context with MonthlyEntries + Project included, so
        // db.Budgets.Update(budget) would attach that whole graph and mark every node Modified —
        // silently rewriting monthly entries (and the read-only-ish Project nav) on a metadata
        // save. Load the tracked row instead and copy only the editable scalar columns; entry
        // edits go through SaveBudgetEntriesAsync.
        var target = await db.Budgets.FindAsync(budget.Id);
        if (target == null)
            throw new ArgumentException("Budget not found", nameof(budget));

        var name = ValidateAndNormalizeName(budget.Name);
        ValidateYear(budget.Year);

        var now = DateTime.UtcNow;
        target.Name = name;
        target.Year = budget.Year;
        target.Notes = budget.Notes;
        target.IsActive = budget.IsActive;
        target.LastModifiedAt = now;
        budget.LastModifiedAt = now; // keep the caller's in-memory copy in sync for re-render

        if (target.IsActive)
        {
            var siblings = await db.Budgets
                .Where(b => b.ProjectId == target.ProjectId
                            && b.Year == target.Year
                            && b.Id != target.Id
                            && b.IsActive)
                .ToListAsync();
            foreach (var sibling in siblings)
                sibling.IsActive = false;
        }

        await db.SaveChangesAsync();
        Notify(new DataChange(DataArea.Budgets, ProjectId: target.ProjectId));
    }

    public async Task DeleteBudgetAsync(int budgetId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var budget = await db.Budgets.FindAsync(budgetId);
        if (budget != null)
        {
            db.Budgets.Remove(budget);
            await db.SaveChangesAsync();
            Notify(new DataChange(DataArea.Budgets, ProjectId: budget.ProjectId));
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
        await using var db = await _dbFactory.CreateDbContextAsync();
        var budget = await db.Budgets.FindAsync(budgetId);
        if (budget == null)
            throw new ArgumentException("Budget not found", nameof(budgetId));

        await ApplyEntriesAsync(db, budget, entries);

        budget.LastModifiedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        Notify(new DataChange(DataArea.Budgets, ProjectId: budget.ProjectId));
    }

    /// <summary>
    /// Save budget metadata <em>and</em> its full monthly-entry set in a single transaction,
    /// guarded by optimistic concurrency on <paramref name="expectedVersion"/> (the Version the
    /// editor loaded). If another session saved the budget in the meantime, the version no longer
    /// matches and EF throws <see cref="DbUpdateConcurrencyException"/> — the editor surfaces that
    /// as a conflict instead of silently overwriting the other edit. This is the editor's save
    /// path; <see cref="SaveBudgetEntriesAsync"/> stays for the wizard (new budgets, no contention).
    /// </summary>
    public async Task SaveBudgetAsync(
        Budget budget,
        IReadOnlyCollection<(long Id, CategoryItemType ItemType, decimal[] Months)> entries,
        int expectedVersion)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var target = await db.Budgets.FindAsync(budget.Id);
        if (target == null)
            throw new ArgumentException("Budget not found", nameof(budget));

        // Compare against the version the caller loaded, not the row's current value (the row was
        // just re-read, so its current value can't conflict with itself). EF puts the original
        // value in the UPDATE's WHERE clause.
        db.Entry(target).Property(b => b.Version).OriginalValue = expectedVersion;

        // Authoritative validation — the UI also checks, but the service must never persist an
        // empty name or a nonsense year regardless of caller.
        var name = ValidateAndNormalizeName(budget.Name);
        ValidateYear(budget.Year);

        var now = DateTime.UtcNow;
        target.Name = name;
        target.Year = budget.Year;
        target.Notes = budget.Notes;
        target.IsActive = budget.IsActive;
        target.LastModifiedAt = now;
        target.Version = expectedVersion + 1;

        if (target.IsActive)
        {
            var siblings = await db.Budgets
                .Where(b => b.ProjectId == target.ProjectId
                            && b.Year == target.Year
                            && b.Id != target.Id
                            && b.IsActive)
                .ToListAsync();
            foreach (var sibling in siblings)
                sibling.IsActive = false;
        }

        await ApplyEntriesAsync(db, target, entries);

        // One transaction: the budget UPDATE (version-guarded), sibling deactivation, and all
        // entry inserts/updates/deletes commit together or not at all.
        await db.SaveChangesAsync();

        // Keep the caller's in-memory copy current so a re-render (and any subsequent save) sees
        // the new version.
        budget.LastModifiedAt = now;
        budget.Version = target.Version;
        Notify(new DataChange(DataArea.Budgets, ProjectId: target.ProjectId));
    }

    /// <summary>
    /// Reconcile the MonthlyBudgetEntry rows for <paramref name="budget"/> against the supplied
    /// set on the given context: rows whose (CategoryId, ItemType) key is absent are removed, the
    /// rest upserted. Does not call SaveChanges — the caller commits so metadata + entries land in
    /// one transaction.
    /// </summary>
    private async Task ApplyEntriesAsync(
        BudgetDbContext db,
        Budget budget,
        IReadOnlyCollection<(long Id, CategoryItemType ItemType, decimal[] Months)> entries)
    {
        var project = await db.Projects.FindAsync(budget.ProjectId);
        var sourceId = project?.SourceId ?? SourceScope.LegacySourceId;
        var hierarchy = await _hierarchy.GetAllCategoriesWithChildCountsAsync(sourceId);
        var normalized = NormalizeBudgetEntries(entries, hierarchy);

        var existing = await db.MonthlyBudgetEntries
            .Where(e => e.BudgetId == budget.Id)
            .ToListAsync();

        var desiredKeys = normalized.Select(e => (e.Id, e.ItemType)).ToHashSet();
        foreach (var row in existing.Where(e => !desiredKeys.Contains((e.CategoryId, e.ItemType))))
        {
            db.MonthlyBudgetEntries.Remove(row);
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
                    BudgetId = budget.Id,
                    CategoryId = id,
                    ItemType = itemType
                };
                db.MonthlyBudgetEntries.Add(row);
            }
            for (int m = 1; m <= 12; m++)
                row.SetMonthAmount(m, months[m - 1]);
        }
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
        await using var db = await _dbFactory.CreateDbContextAsync();
        var rows = await db.BudgetContributionSplits
            .AsNoTracking()
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
        await using var db = await _dbFactory.CreateDbContextAsync();
        var existing = await db.BudgetContributionSplits
            .Where(s => s.BudgetId == budgetId)
            .ToListAsync();

        var desiredKeys = splits.Select(s => (s.Id, s.ItemType)).ToHashSet();
        foreach (var row in existing.Where(r => !desiredKeys.Contains((r.CategoryId, r.ItemType))))
        {
            db.BudgetContributionSplits.Remove(row);
        }

        foreach (var (id, itemType, percent) in splits)
        {
            var row = existing.FirstOrDefault(r => r.CategoryId == id && r.ItemType == itemType);
            if (row == null)
            {
                db.BudgetContributionSplits.Add(new BudgetContributionSplit
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

        await db.SaveChangesAsync();
        var projectId = await db.Budgets.Where(b => b.Id == budgetId).Select(b => (int?)b.ProjectId).FirstOrDefaultAsync();
        Notify(new DataChange(DataArea.Budgets, ProjectId: projectId));
    }

    /// <summary>
    /// Returns the 12-month budget amounts for every MonthlyBudgetEntry on a budget, keyed by
    /// (CategoryId, ItemType) — Group and Category IDs share a numeric space so the
    /// discriminator is required.
    /// </summary>
    public async Task<Dictionary<(long Id, CategoryItemType ItemType), decimal[]>> GetMonthlyBudgetAmountsAsync(int budgetId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var entries = await db.MonthlyBudgetEntries
            .AsNoTracking()
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
        await using var db = await _dbFactory.CreateDbContextAsync();
        var budget = await db.Budgets
            .AsNoTracking()
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
