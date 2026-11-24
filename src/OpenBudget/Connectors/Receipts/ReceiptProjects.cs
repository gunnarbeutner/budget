using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.Connectors.Receipts;

/// <summary>
/// Which project a receipt's items are categorized by, and that project's item categories for the receipt's source.
///
/// A booking's project: the owner's projects its account is assigned to. With several, the first (by the projects' order,
/// then id) whose categories contain the booking's current category, otherwise the first of them. A project's categories
/// are those under the category groups (or the categories) assigned to it. A booking on an account of none of the owner's
/// projects has no project: its items stay in its category.
/// </summary>
public static class ReceiptProjects
{
    /// <summary>The project of a booking on <paramref name="accountId"/> currently in <paramref name="bookingCategoryId"/> (see the class remarks), or null.</summary>
    public static async Task<int?> ResolveAsync(BudgetDbContext db, string ownerUserId, long? accountId, long? bookingCategoryId, CancellationToken ct)
    {
        if (accountId is not { } account) return null;
        var projects = await db.ProjectAccountAssignments.AsNoTracking()
            .Where(a => a.AccountId == account && a.Project.OwnerUserId == ownerUserId)
            .Select(a => new { a.ProjectId, a.Project.SortOrder })
            .Distinct().ToListAsync(ct);
        var ordered = projects.OrderBy(p => p.SortOrder).ThenBy(p => p.ProjectId).Select(p => p.ProjectId).Distinct().ToList();
        if (ordered.Count <= 1 || bookingCategoryId is not { } category) return ordered.Count == 0 ? null : ordered[0];
        var members = await CategoriesAsync(db, ownerUserId, ordered, ct);
        foreach (var id in ordered)
            if (members.TryGetValue(id, out var set) && set.Contains(category)) return id;
        return ordered[0];
    }

    /// <summary>For each of the given projects (of the owner): the ids of the categories that belong to it.</summary>
    public static async Task<Dictionary<int, HashSet<long>>> CategoriesAsync(BudgetDbContext db, string ownerUserId, IReadOnlyCollection<int> projectIds, CancellationToken ct)
    {
        var ids = projectIds.ToList();
        var assignments = await db.ProjectCategoryAssignments.AsNoTracking().Where(a => ids.Contains(a.ProjectId))
            .Select(a => new { a.ProjectId, a.CategoryId }).ToListAsync(ct);
        var groups = await db.CategoryGroups.AsNoTracking().Where(g => g.OwnerUserId == ownerUserId)
            .Select(g => new { g.Id, g.ParentGroupId }).ToDictionaryAsync(g => g.Id, g => g.ParentGroupId, ct);
        var categories = await db.Categories.AsNoTracking().Where(c => c.OwnerUserId == ownerUserId)
            .Select(c => new { c.Id, c.GroupId }).ToListAsync(ct);
        var result = ids.ToDictionary(id => id, _ => new HashSet<long>());
        foreach (var c in categories)
        {
            // the category itself and every group above it
            var chain = new HashSet<long> { c.Id };
            var g = c.GroupId;
            while (g is { } gid && chain.Add(gid)) g = groups.GetValueOrDefault(gid);
            foreach (var a in assignments.Where(a => chain.Contains(a.CategoryId))) result[a.ProjectId].Add(c.Id);
        }
        return result;
    }

    /// <summary>
    /// A project's item categories for a receipt source: the project's list, narrowed to the source's choice when the
    /// owner made one; only categories that still exist, with their paths.
    /// </summary>
    public static async Task<ReceiptSourceRules> RulesAsync(BudgetDbContext db, string ownerUserId, int? projectId, string source, CancellationToken ct)
    {
        if (projectId is not { } project) return ReceiptSourceRules.None;
        var rows = await db.ProjectReceiptCategories.AsNoTracking().Where(c => c.OwnerUserId == ownerUserId && c.ProjectId == project)
            .OrderBy(c => c.Id).ToListAsync(ct);
        if (rows.Count == 0) return ReceiptSourceRules.None;
        var narrowing = await db.ProjectReceiptSources.AsNoTracking()
            .Where(s => s.OwnerUserId == ownerUserId && s.ProjectId == project && s.Source == source).FirstOrDefaultAsync(ct);
        if (narrowing != null)
        {
            var only = ParseIds(narrowing.CategoryIds);
            rows = rows.Where(r => only.Contains(r.CategoryId)).ToList();
            if (rows.Count == 0) return ReceiptSourceRules.None;
        }
        var paths = await ReceiptStore.PathsAsync(db, ownerUserId, ct);
        return new ReceiptSourceRules(rows.Where(r => paths.ContainsKey(r.CategoryId))
            .Select(r => new AllowedCategory(r.CategoryId, paths[r.CategoryId], r.Description, r.Keywords)).ToList());
    }

    /// <summary>
    /// For each owner, project and receipt source: when most of the source's categorized bookings (at least three) are
    /// in one category that is not among the source's item categories, it becomes one. Items used to fall through to the
    /// booking's category, so lists were set up without it (the supermarket's food category, typically); without it the
    /// AI has nowhere to put those items. Other sources of the project keep exactly the categories they had. Products
    /// the AI placed nowhere are asked about again. Returns the categories added.
    /// </summary>
    public static async Task<int> AllowUsualBookingCategoriesAsync(BudgetDbContext db, CancellationToken ct)
    {
        var linked = await (from r in db.ReceiptInvoices
                            join t in db.Transactions on r.TransactionId equals t.Id
                            where t.CategoryId != null
                            select new { r.Shop, t.OwnerUserId, t.AccountId, CategoryId = t.CategoryId!.Value }).ToListAsync(ct);
        var counted = new Dictionary<(string Owner, int Project, string Source), Dictionary<long, int>>();
        foreach (var g in linked.GroupBy(x => (x.OwnerUserId, x.AccountId, x.CategoryId, Source: ReceiptSources.Of(x.Shop))))
        {
            if (await ResolveAsync(db, g.Key.OwnerUserId, g.Key.AccountId, g.Key.CategoryId, ct) is not { } project) continue;
            var key = (g.Key.OwnerUserId, project, g.Key.Source);
            if (!counted.TryGetValue(key, out var byCategory)) counted[key] = byCategory = [];
            byCategory[g.Key.CategoryId] = byCategory.GetValueOrDefault(g.Key.CategoryId) + g.Count();
        }

        var added = 0;
        var owners = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ((owner, project, source), byCategory) in counted)
        {
            var (usual, n) = byCategory.MaxBy(kv => kv.Value);
            if (n < 3 || n * 2 <= byCategory.Values.Sum()) continue;
            if ((await RulesAsync(db, owner, project, source, ct)).Allowed.Any(c => c.CategoryId == usual)) continue;
            if (!await db.Categories.AnyAsync(c => c.Id == usual && c.OwnerUserId == owner, ct)) continue;

            var listed = await db.ProjectReceiptCategories.Where(c => c.OwnerUserId == owner && c.ProjectId == project).Select(c => c.CategoryId).ToListAsync(ct);
            var narrowings = await db.ProjectReceiptSources.Where(s => s.OwnerUserId == owner && s.ProjectId == project).ToListAsync(ct);
            if (!listed.Contains(usual))
            {
                // Sources that take the whole list keep what they had.
                if (listed.Count > 0)
                    foreach (var other in ReceiptSources.All.Where(o => o != source && narrowings.All(n => n.Source != o)))
                        db.ProjectReceiptSources.Add(new ProjectReceiptSource { OwnerUserId = owner, ProjectId = project, Source = other, CategoryIds = FormatIds(listed) });
                db.ProjectReceiptCategories.Add(new ProjectReceiptCategory { OwnerUserId = owner, ProjectId = project, CategoryId = usual });
            }
            if (narrowings.FirstOrDefault(n => n.Source == source) is { } own)
                own.CategoryIds = FormatIds(ParseIds(own.CategoryIds).Append(usual));
            await db.SaveChangesAsync(ct);
            owners.Add(owner);
            added++;
        }
        foreach (var owner in owners) await ReceiptStore.ReaskAsync(db, owner, ct);
        return added;
    }

    /// <summary>The ids of a narrowing ("12,34").</summary>
    public static HashSet<long> ParseIds(string? text) =>
        (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => long.TryParse(x, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : (long?)null)
            .Where(v => v != null).Select(v => v!.Value).ToHashSet();

    /// <summary>A narrowing's text for these ids.</summary>
    public static string FormatIds(IEnumerable<long> ids) =>
        string.Join(',', ids.Distinct().Order().Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
}
