using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>
/// Suggests a project's income/expense split and budget tracking from the owner's data, as a user would set them in
/// project settings › Categories: categories whose bookings sum to a positive amount are income (a group too when all
/// of its categories with bookings are); categories with a non-zero budget in the project are tracked, and so are
/// top-level groups with spending in the last 12 months that hold no tracked category. Only adds what is missing.
/// </summary>
public static class ProjectCategorySetup
{
    public static async Task ApplySuggestionsAsync(BudgetDbContext db, string ownerId, int projectId, DateTime now, CancellationToken ct = default)
    {
        var groups = await db.CategoryGroups.AsNoTracking().Where(g => g.OwnerUserId == ownerId).ToListAsync(ct);
        var categories = await db.Categories.AsNoTracking().Where(c => c.OwnerUserId == ownerId).ToListAsync(ct);
        var sentinels = (await RowIds.SentinelIdsAsync(db, ownerId, ct)).ToHashSet();
        var root = await RowIds.SentinelRootAsync(db, ownerId, ct);

        // Booked sums per category over the whole history and over the last 12 months (split parents excluded).
        var since = new DateTimeOffset(now.AddMonths(-12)).ToUnixTimeSeconds();
        var sums = await db.Transactions.AsNoTracking()
            .Where(t => t.OwnerUserId == ownerId && t.CategoryId != null && t.Splitted != 1)
            .GroupBy(t => t.CategoryId!.Value)
            .Select(g => new { Id = g.Key, Total = g.Sum(t => t.Amount), Recent = g.Where(t => t.Timestamp >= since).Sum(t => t.Amount) })
            .ToDictionaryAsync(x => x.Id, ct);

        var incomeItems = new List<(long Id, CategoryItemType Type)>();
        foreach (var c in categories.Where(c => !sentinels.Contains(c.Id)))
            if (sums.TryGetValue(c.Id, out var s) && s.Total > 0) incomeItems.Add((c.Id, CategoryItemType.Category));

        IEnumerable<Category> CategoriesUnder(long groupId)
        {
            foreach (var c in categories.Where(c => c.GroupId == groupId)) yield return c;
            foreach (var g in groups.Where(g => g.ParentGroupId == groupId))
                foreach (var c in CategoriesUnder(g.Id)) yield return c;
        }

        foreach (var g in groups.Where(g => !sentinels.Contains(g.Id)))
        {
            var booked = CategoriesUnder(g.Id).Where(c => sums.ContainsKey(c.Id)).ToList();
            if (booked.Count > 0 && booked.All(c => sums[c.Id].Total > 0)) incomeItems.Add((g.Id, CategoryItemType.Group));
        }

        // One type assignment per project and id: an imported group and category can share an id, so the first
        // suggestion for an id wins and ids that already have an assignment are left alone.
        var typed = (await db.CategoryTypeAssignments.Where(t => t.ProjectId == projectId).Select(t => t.CategoryId).ToListAsync(ct))
            .ToHashSet();
        foreach (var (id, type) in incomeItems)
        {
            if (!typed.Add(id)) continue;
            db.CategoryTypeAssignments.Add(new CategoryTypeAssignment { ProjectId = projectId, CategoryId = id, ItemType = type, IsIncome = true });
        }

        // Tracking: budgeted categories, then top-level expense groups with recent spending and no tracked category.
        var budgeted = await db.MonthlyBudgetEntries.AsNoTracking()
            .Where(e => e.Budget.ProjectId == projectId && e.ItemType == CategoryItemType.Category)
            .ToListAsync(ct);
        var trackedCategories = budgeted.Where(e => Enumerable.Range(1, 12).Any(m => e.GetMonthAmount(m) != 0)).Select(e => e.CategoryId).ToHashSet();
        var incomeGroups = incomeItems.Where(i => i.Type == CategoryItemType.Group).Select(i => i.Id).ToHashSet();
        var trackedGroups = groups
            .Where(g => !sentinels.Contains(g.Id) && (g.ParentGroupId == null || g.ParentGroupId == root) && !incomeGroups.Contains(g.Id))
            .Where(g => CategoriesUnder(g.Id).Any(c => sums.TryGetValue(c.Id, out var s) && s.Recent < 0))
            .Where(g => !CategoriesUnder(g.Id).Any(c => trackedCategories.Contains(c.Id)))
            .Select(g => g.Id).ToHashSet();

        var existing = await db.ProjectTrackingItems.Where(t => t.ProjectId == projectId).ToListAsync(ct);
        foreach (var id in trackedCategories.Where(id => existing.All(t => t.CategoryId != id)))
            db.ProjectTrackingItems.Add(new ProjectTrackingItem { ProjectId = projectId, CategoryId = id });
        foreach (var id in trackedGroups.Where(id => existing.All(t => t.CategoryGroupId != id)))
            db.ProjectTrackingItems.Add(new ProjectTrackingItem { ProjectId = projectId, CategoryGroupId = id });
        await db.SaveChangesAsync(ct);
    }
}
