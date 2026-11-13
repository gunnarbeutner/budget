using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.Importers.MoneyMoney;

/// <summary>
/// Turns MoneyMoney's category rules (the <c>categories.rules</c> text, a MoneyMoney search expression, which is the
/// syntax OpenBudget's rules use) into ordinary <see cref="CategoryRule"/>s: one rule per category with a non-empty
/// rule, in every project of the owner, appended in category order (group order, then category order). Idempotent:
/// a rule already imported for (project, category) is updated while the user has not edited it
/// (<see cref="CategoryRule.Expression"/> still equals <see cref="CategoryRule.ImportedExpression"/>) and left alone
/// otherwise; a category whose MoneyMoney rule became empty loses its untouched imported rule. Runs after every upload
/// and once at startup (backfill for data imported before rules were imported).
/// </summary>
public static class MoneyMoneyRuleImport
{
    public const string Origin = "moneymoney";

    /// <summary>Returns how many rules were created, updated or removed.</summary>
    public static async Task<int> SyncAsync(BudgetDbContext db, string ownerId, CancellationToken ct)
    {
        var projects = await db.Projects.Where(p => p.OwnerUserId == ownerId).Select(p => p.Id).ToListAsync(ct);
        if (projects.Count == 0) return 0;
        var groups = await db.CategoryGroups.AsNoTracking().Where(g => g.OwnerUserId == ownerId).ToDictionaryAsync(g => g.Id, ct);
        var categories = (await db.Categories.AsNoTracking().Where(c => c.OwnerUserId == ownerId).ToListAsync(ct))
            .OrderBy(c => GroupPath(c.GroupId, groups), Comparer<List<long>>.Create(ComparePaths))
            .ThenBy(c => c.Sort ?? long.MaxValue).ThenBy(c => c.Id).ToList();
        var wanted = categories
            .Where(c => !string.IsNullOrWhiteSpace(c.Rules) && !string.IsNullOrWhiteSpace(c.Name))
            .Select(c => (c.Id, Expression: c.Rules!.Trim()))
            .ToList();

        var changes = 0;
        foreach (var projectId in projects)
        {
            var rules = await db.CategoryRules.Where(r => r.ProjectId == projectId).ToListAsync(ct);
            var imported = rules.Where(r => r.ImportOrigin == Origin).GroupBy(r => r.CategoryId).ToDictionary(g => g.Key, g => g.First());
            var next = rules.Count == 0 ? 0 : rules.Max(r => r.SortOrder) + 1;
            var wantedIds = new HashSet<long>();
            foreach (var (categoryId, expression) in wanted)
            {
                wantedIds.Add(categoryId);
                if (imported.TryGetValue(categoryId, out var rule))
                {
                    if (rule.Dismissed || rule.Expression != rule.ImportedExpression || rule.Expression == expression) continue;
                    rule.Expression = expression;
                    rule.ImportedExpression = expression;
                    changes++;
                    continue;
                }
                db.CategoryRules.Add(new CategoryRule
                {
                    ProjectId = projectId,
                    SortOrder = next++,
                    CategoryId = categoryId,
                    Enabled = true,
                    Expression = expression,
                    ImportOrigin = Origin,
                    ImportedExpression = expression,
                });
                changes++;
            }
            foreach (var (categoryId, rule) in imported)
            {
                if (wantedIds.Contains(categoryId) || rule.Dismissed || rule.Expression != rule.ImportedExpression) continue;
                db.CategoryRules.Remove(rule);
                changes++;
            }
        }
        if (changes > 0) await db.SaveChangesAsync(ct);
        return changes;
    }

    /// <summary>Startup backfill: every owner whose MoneyMoney categories have rules.</summary>
    public static async Task<int> BackfillAsync(BudgetDbContext db, CancellationToken ct)
    {
        var sources = await db.Categories.Where(c => c.Rules != null && c.Rules != "").Select(c => c.OwnerUserId).Distinct().ToListAsync(ct);
        var total = 0;
        foreach (var s in sources) total += await SyncAsync(db, s, ct);
        return total;
    }

    private static List<long> GroupPath(long? groupId, Dictionary<long, CategoryGroup> groups)
    {
        var path = new List<long>();
        var seen = new HashSet<long>();
        while (groupId is { } id && groups.TryGetValue(id, out var g) && seen.Add(id))
        {
            path.Insert(0, g.Sort ?? long.MaxValue);
            groupId = g.ParentGroupId;
        }
        return path;
    }

    private static int ComparePaths(List<long>? a, List<long>? b)
    {
        a ??= [];
        b ??= [];
        for (var i = 0; i < Math.Min(a.Count, b.Count); i++)
        {
            var c = a[i].CompareTo(b[i]);
            if (c != 0) return c;
        }
        return a.Count.CompareTo(b.Count);
    }
}
