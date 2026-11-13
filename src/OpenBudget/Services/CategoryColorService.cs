using Microsoft.EntityFrameworkCore;

namespace OpenBudget.Services;

/// <summary>
/// Changes a category's color. Only the source's owner may, and only once the structure was taken over from MoneyMoney:
/// before that every upload rewrites the categories (colors included) from MoneyMoney.
/// </summary>
public sealed class CategoryColorService
{
    private readonly IDbContextFactory<BudgetDbContext> _dbFactory;
    private readonly ChangeNotificationService _notifier;
    private readonly ChangeOrigin _origin;

    public CategoryColorService(IDbContextFactory<BudgetDbContext> dbFactory, ChangeNotificationService notifier, ChangeOrigin origin)
    {
        _dbFactory = dbFactory;
        _notifier = notifier;
        _origin = origin;
    }

    /// <summary>Whether the current user may recolor categories of this source.</summary>
    public async Task<bool> CanEditAsync(string ownerId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        if (db.Scope is { } scope && !scope.Owns(ownerId)) return false;
        return true;
    }

    /// <summary>Sets (or with null clears) the color; returns false when the category is unknown or not editable.</summary>
    public async Task<bool> SetColorAsync(long categoryId, string? color, CancellationToken ct = default)
    {
        if (color != null && !CategoryColors.IsValid(color)) throw new ArgumentException("Not a color.", nameof(color));
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var category = await db.Categories.FirstOrDefaultAsync(c => c.Id == categoryId, ct);
        if (category == null) return false;
        db.Scope?.RequireOwner(category.OwnerUserId);
        category.Color = color == null ? null : CategoryColors.ToCss(color);
        await db.SaveChangesAsync(ct);
        _notifier.Publish(new DataChange(DataArea.ImportedData, OwnerUserId: category.OwnerUserId), _origin.Id);
        return true;
    }

    /// <summary>Sets a category group's colour (null = none: the sidebar shows a folder). Owner only; false when not found.</summary>
    public async Task<bool> SetGroupColorAsync(long groupId, string? color, CancellationToken ct = default)
    {
        if (color != null && !CategoryColors.IsValid(color)) throw new ArgumentException("Not a color.", nameof(color));
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var group = await db.CategoryGroups.FirstOrDefaultAsync(g => g.Id == groupId, ct);
        if (group == null) return false;
        db.Scope?.RequireOwner(group.OwnerUserId);
        group.Color = color == null ? null : CategoryColors.ToCss(color);
        await db.SaveChangesAsync(ct);
        _notifier.Publish(new DataChange(DataArea.ImportedData, OwnerUserId: group.OwnerUserId), _origin.Id);
        return true;
    }
}
