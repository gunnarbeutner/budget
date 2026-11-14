using Microsoft.EntityFrameworkCore;
using OpenBudget.Importers.MoneyMoney;
using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>What a takeover copied over, for the confirmation message.</summary>
public sealed record StructureTakeoverResult(int AccountGroups, int Accounts, int CategoryGroups, int Categories);

/// <summary>
/// Hands a user's structure to OpenBudget once their MoneyMoney import from the browser finished. The structure
/// (account groups and accounts' names/grouping/order, category groups and categories with names, colors, MoneyMoney's
/// rule text, order) already lives in OpenBudget's own rows - the import writes it there - so the takeover freezes it:
/// it gives every sibling an explicit sort position (MoneyMoney leaves some empty and relies on name order) and marks the
/// user via <see cref="UserDataState.StructureTakenOverAt"/>.
/// </summary>
public sealed class StructureTakeoverService
{
    private readonly IDbContextFactory<BudgetDbContext> _dbFactory;
    private readonly ChangeNotificationService _notifier;
    private readonly ChangeOrigin _origin;
    private readonly MoneyMoneyImportService? _importer;

    public StructureTakeoverService(
        IDbContextFactory<BudgetDbContext> dbFactory, ChangeNotificationService notifier, ChangeOrigin origin, MoneyMoneyImportService? importer = null)
    {
        _dbFactory = dbFactory;
        _notifier = notifier;
        _origin = origin;
        _importer = importer;
    }

    /// <summary>
    /// Takes the structure of the user's data over (null when it already was). Runs on the context the factory gives; the
    /// browser import passes the system factory.
    /// </summary>
    public async Task<StructureTakeoverResult?> TakeOverAsync(string ownerId, CancellationToken ct = default)
    {
        // An upload in flight must finish first, otherwise it could replace the structure we are freezing.
        var result = _importer == null
            ? await TakeOverCoreAsync(ownerId, ct)
            : await _importer.WithOwnerLockAsync(ownerId, () => TakeOverCoreAsync(ownerId, ct), ct);
        if (result != null)
        {
            _notifier.Publish(new DataChange(DataArea.ImportState, OwnerUserId: ownerId), _origin.Id);
            _notifier.Publish(new DataChange(DataArea.ImportedData, OwnerUserId: ownerId), _origin.Id);
        }
        return result;
    }

    private async Task<StructureTakeoverResult?> TakeOverCoreAsync(string ownerId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var source = await RowIds.StateAsync(db, ownerId, ct);
        if (source.StructureTakenOverAt != null) return null;

        var accountGroups = await db.AccountGroups.Where(g => g.OwnerUserId == ownerId).ToListAsync(ct);
        var accounts = await db.Accounts.Where(a => a.OwnerUserId == ownerId).ToListAsync(ct);
        var categoryGroups = await db.CategoryGroups.Where(g => g.OwnerUserId == ownerId).ToListAsync(ct);
        var categories = await db.Categories.Where(c => c.OwnerUserId == ownerId).ToListAsync(ct);

        // Account tree: groups and accounts are siblings under a group; accounts without a (known) group stay ungrouped.
        var knownAccountGroups = accountGroups.Select(g => g.Id).ToHashSet();
        foreach (var siblings in accountGroups.GroupBy(g => g.ParentGroupId is { } p && knownAccountGroups.Contains(p) ? p : (long?)null))
            Renumber(siblings.OrderBy(g => g.Sort ?? long.MaxValue).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(g => g.Id), (g, i) => g.Sort = i);
        foreach (var siblings in accounts.GroupBy(a => a.GroupId is { } p && knownAccountGroups.Contains(p) ? p : (long?)null))
            Renumber(siblings.OrderBy(a => a.Sort ?? long.MaxValue).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(a => a.Id), (a, i) => a.Sort = i);

        var knownCategoryGroups = categoryGroups.Select(g => g.Id).ToHashSet();
        foreach (var siblings in categoryGroups.GroupBy(g => g.ParentGroupId is { } p && knownCategoryGroups.Contains(p) ? p : (long?)null))
            Renumber(siblings.OrderBy(g => g.Sort ?? long.MaxValue).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(g => g.Id), (g, i) => g.Sort = i);
        foreach (var siblings in categories.GroupBy(c => c.GroupId is { } p && knownCategoryGroups.Contains(p) ? p : (long?)null))
            Renumber(siblings.OrderBy(c => c.Sort ?? long.MaxValue).ThenBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(c => c.Id), (c, i) => c.Sort = i);

        source.StructureTakenOverAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return new StructureTakeoverResult(accountGroups.Count, accounts.Count, categoryGroups.Count, categories.Count);
    }

    private static void Renumber<T>(IEnumerable<T> ordered, Action<T, long> assign)
    {
        long i = 0;
        foreach (var item in ordered) assign(item, i++);
    }
}
