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
    public async Task<bool> CanEditAsync(int sourceId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        if (db.Scope is { } scope && !scope.OwnsSource(sourceId)) return false;
        return await db.ImportSources.AnyAsync(s => s.Id == sourceId, ct);
    }

    /// <summary>Sets (or with null clears) the color; returns false when the category is unknown or not editable.</summary>
    public async Task<bool> SetColorAsync(long categoryId, string? color, CancellationToken ct = default)
    {
        if (color != null && !CategoryColors.IsValid(color)) throw new ArgumentException("Not a color.", nameof(color));
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var category = await db.Categories.FirstOrDefaultAsync(c => c.Id == categoryId, ct);
        if (category == null) return false;
        db.Scope?.RequireSourceOwner(category.SourceId);
        if (!await db.ImportSources.AnyAsync(s => s.Id == category.SourceId, ct)) return false;
        category.Color = color == null ? null : CategoryColors.ToCss(color);
        await db.SaveChangesAsync(ct);
        _notifier.Publish(new DataChange(DataArea.ImportedData, SourceId: category.SourceId), _origin.Id);
        return true;
    }
}
