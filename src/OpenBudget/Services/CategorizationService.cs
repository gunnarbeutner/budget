using Microsoft.EntityFrameworkCore;

namespace OpenBudget.Services;

public enum SuggestionReason
{
    /// <summary>The category most often used for transactions of the same payee.</summary>
    SamePayee,

    /// <summary>One of the categories used most recently.</summary>
    RecentlyUsed,
}

/// <summary>A category the picker offers on top; <see cref="Count"/> is the number of same-payee transactions (SamePayee only).</summary>
public sealed record CategorySuggestion(long CategoryId, string Path, SuggestionReason Reason, int Count);

/// <summary>Read side of categorizing: the picker's categories, suggestions per payee and "similar uncategorized" lookups.</summary>
public sealed class CategorizationService
{
    private readonly IDbContextFactory<BudgetDbContext> _dbFactory;

    public CategorizationService(IDbContextFactory<BudgetDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<CategoryPickerData> GetPickerDataAsync(int sourceId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var groups = await db.CategoryGroups.AsNoTracking().Where(g => g.SourceId == sourceId).ToListAsync(ct);
        var categories = await db.Categories.AsNoTracking().Where(c => c.SourceId == sourceId).ToListAsync(ct);
        return CategoryPickerLogic.Build(groups, categories, sourceId);
    }

    /// <summary>
    /// Suggestions for a payee: up to <paramref name="payeeTake"/> most frequent categories of that payee (by count, newest
    /// breaks ties), then the most recently used categories, each category once.
    /// </summary>
    public async Task<List<CategorySuggestion>> GetSuggestionsAsync(
        int sourceId, string? payee, CategoryPickerData picker, int payeeTake = 2, int recentTake = 4, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var result = new List<CategorySuggestion>();
        var usable = picker.Options.ToDictionary(o => o.Id);

        var name = payee?.Trim();
        if (!string.IsNullOrEmpty(name))
        {
            var byPayee = await db.Transactions.AsNoTracking()
                .Where(t => t.SourceId == sourceId && t.CategoryId != null && t.Name != null && EF.Functions.Collate(t.Name, "NOCASE") == name)
                .GroupBy(t => t.CategoryId!.Value)
                .Select(g => new { Id = g.Key, Count = g.Count(), Last = g.Max(t => t.Timestamp) })
                .OrderByDescending(g => g.Count).ThenByDescending(g => g.Last)
                .Take(payeeTake + 4)
                .ToListAsync(ct);
            foreach (var g in byPayee)
            {
                if (result.Count >= payeeTake) break;
                if (usable.TryGetValue(g.Id, out var option))
                    result.Add(new CategorySuggestion(option.Id, option.Path, SuggestionReason.SamePayee, g.Count));
            }
        }

        // "Recently used": by the moment a user last categorized by hand, else by the booking date.
        var recent = await db.Transactions.AsNoTracking()
            .Where(t => t.SourceId == sourceId && t.CategoryId != null)
            .GroupBy(t => t.CategoryId!.Value)
            .Select(g => new { Id = g.Key, Last = g.Max(t => t.CategorySetAt ?? t.Timestamp) })
            .OrderByDescending(g => g.Last)
            .Take(recentTake + 12)
            .ToListAsync(ct);
        var added = 0;
        foreach (var g in recent)
        {
            if (added >= recentTake) break;
            if (!usable.TryGetValue(g.Id, out var option) || result.Any(r => r.CategoryId == g.Id)) continue;
            result.Add(new CategorySuggestion(option.Id, option.Path, SuggestionReason.RecentlyUsed, 0));
            added++;
        }
        return result;
    }

    /// <summary>
    /// Uncategorized transactions of the same payee the user may edit, except the given ones: what "Also categorize N similar"
    /// acts on. Split parents are never offered.
    /// </summary>
    public async Task<List<long>> FindSimilarUncategorizedAsync(
        int sourceId, string? payee, IReadOnlyCollection<long> exclude, CancellationToken ct = default)
    {
        var name = payee?.Trim();
        if (string.IsNullOrEmpty(name)) return [];
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.Transactions.AsNoTracking()
            .Where(t => t.SourceId == sourceId && t.Splitted != 1 && t.Name != null && EF.Functions.Collate(t.Name, "NOCASE") == name)
            .WhereUncategorized(db, sourceId)
            .Select(t => new { t.Id, t.SourceId, t.AccountId })
            .ToListAsync(ct);
        rows = rows.Where(r => !exclude.Contains(r.Id)).ToList();

        var accountIds = rows.Where(r => r.AccountId.HasValue).Select(r => r.AccountId!.Value).Distinct().ToList();
        var providers = (await db.Accounts.AsNoTracking().Where(a => accountIds.Contains(a.Id)).Select(a => new { a.Id, a.Provider }).ToListAsync(ct))
            .ToDictionary(a => a.Id, a => a.Provider);
        return rows
            .Where(r => TransactionEditRules.Check(db.Scope, r.SourceId, r.AccountId,
                r.AccountId is { } id && providers.TryGetValue(id, out var p) ? p : null) == EditBlock.None)
            .Select(r => r.Id)
            .ToList();
    }
}
