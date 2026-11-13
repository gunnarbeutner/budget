using Microsoft.EntityFrameworkCore;
using OpenBudget.Importers.MoneyMoney;
using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>What a takeover copied over, for the confirmation message.</summary>
public sealed record StructureTakeoverResult(int AccountGroups, int Accounts, int CategoryGroups, int Categories);

/// <summary>
/// Hands a source's structure to OpenBudget once its MoneyMoney import from the browser finished. The structure
/// (account groups and accounts' names/grouping/order, category groups and categories with names, colors, MoneyMoney's
/// rule text, order) already lives in OpenBudget's own rows - the import writes it there - so the takeover freezes it:
/// it gives every sibling an explicit sort position (MoneyMoney leaves some empty and relies on name order) and marks the
/// source via <see cref="ImportSource.StructureTakenOverAt"/>. The MoneyMoney import checks that mark and from then on only
/// refreshes transactions and balances of MoneyMoney-owned accounts; ids stay stable, so projects, budgets and tracking
/// keep working.
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
    /// Takes the structure of the source over (null when it already was). Runs on the context the factory gives; the
    /// browser import passes the system factory.
    /// </summary>
    public async Task<StructureTakeoverResult?> TakeOverAsync(int sourceId, CancellationToken ct = default)
    {
        string name;
        await using (var probe = await _dbFactory.CreateDbContextAsync(ct))
        {
            name = await probe.ImportSources.Where(s => s.Id == sourceId).Select(s => s.Name).FirstOrDefaultAsync(ct)
                ?? throw new AccessDeniedException("Source not found.");
        }

        // An upload in flight must finish first, otherwise it could replace the structure we are freezing.
        var result = _importer == null
            ? await TakeOverCoreAsync(sourceId, ct)
            : await _importer.WithSourceLockAsync(name, () => TakeOverCoreAsync(sourceId, ct), ct);
        if (result != null)
        {
            _notifier.Publish(new DataChange(DataArea.Sources, SourceId: sourceId), _origin.Id);
            _notifier.Publish(new DataChange(DataArea.ImportedData, SourceId: sourceId), _origin.Id);
        }
        return result;
    }

    private async Task<StructureTakeoverResult?> TakeOverCoreAsync(int sourceId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var source = await db.ImportSources.FirstOrDefaultAsync(s => s.Id == sourceId, ct)
            ?? throw new AccessDeniedException("Source not found.");
        if (source.StructureTakenOverAt != null) return null;

        var accountGroups = await db.AccountGroups.Where(g => g.SourceId == sourceId).ToListAsync(ct);
        var accounts = await db.Accounts.Where(a => a.SourceId == sourceId).ToListAsync(ct);
        var categoryGroups = await db.CategoryGroups.Where(g => g.SourceId == sourceId).ToListAsync(ct);
        var categories = await db.Categories.Where(c => c.SourceId == sourceId).ToListAsync(ct);

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
