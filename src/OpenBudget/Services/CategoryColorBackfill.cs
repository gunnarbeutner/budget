using Microsoft.EntityFrameworkCore;

namespace OpenBudget.Services;

/// <summary>
/// Gives every category group and category without a colour one, so each shows a coloured ribbon: a category the
/// palette colour it was shown in before (<see cref="CategoryColors.FallbackColor"/>), a group the palette colours in
/// turn. MoneyMoney's sentinel rows are left alone. Runs at startup and after an import; idempotent.
/// </summary>
public static class CategoryColorBackfill
{
    public static async Task<int> FillAsync(BudgetDbContext db, string? ownerId = null, CancellationToken ct = default)
    {
        var sentinels = new HashSet<long>();
        foreach (var space in await db.UserDataStates.AsNoTracking().Select(s => s.IdSpace).ToListAsync(ct))
            sentinels.UnionWith(RowIds.SentinelIds(space));

        var categories = await db.Categories.Where(c => c.Color == null && (ownerId == null || c.OwnerUserId == ownerId)).ToListAsync(ct);
        foreach (var c in categories.Where(c => !sentinels.Contains(c.Id)))
            c.Color = CategoryColors.FallbackColor(c.Id);

        var groups = await db.CategoryGroups.Where(g => g.Color == null && (ownerId == null || g.OwnerUserId == ownerId)).OrderBy(g => g.Id).ToListAsync(ct);
        var filled = 0;
        foreach (var g in groups.Where(g => !sentinels.Contains(g.Id)))
            g.Color = CategoryColors.Palette[filled++ % CategoryColors.Palette.Count].Key;

        await db.SaveChangesAsync(ct);
        return categories.Count(c => c.Color != null) + filled;
    }
}
