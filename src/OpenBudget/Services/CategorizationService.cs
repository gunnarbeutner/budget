using Microsoft.EntityFrameworkCore;
using OpenBudget.Assist;

namespace OpenBudget.Services;

/// <summary>Read side of categorizing: the picker's categories and "similar uncategorized" lookups.</summary>
public sealed class CategorizationService
{
    private readonly IDbContextFactory<BudgetDbContext> _dbFactory;

    public CategorizationService(IDbContextFactory<BudgetDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<CategoryPickerData> GetPickerDataAsync(string ownerId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var groups = await db.CategoryGroups.AsNoTracking().Where(g => g.OwnerUserId == ownerId).ToListAsync(ct);
        var categories = await db.Categories.AsNoTracking().Where(c => c.OwnerUserId == ownerId).ToListAsync(ct);
        return CategoryPickerLogic.Build(groups, categories, await RowIds.SentinelRootAsync(db, ownerId, ct));
    }

    /// <summary>
    /// Uncategorized transactions of the same payee as <paramref name="transactionId"/> that the user may edit, except the
    /// given ones: what "Also categorize N similar" acts on. The same payee is the assistant's: the same
    /// <see cref="CategoryMatching.PayeeKey"/> (legal forms and booking boilerplate ignored), and where both bookings carry a
    /// counterparty IBAN, the same IBAN. Split parents are never offered.
    /// </summary>
    public async Task<List<long>> FindSimilarUncategorizedAsync(
        string ownerId, long transactionId, IReadOnlyCollection<long> exclude, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var source = await db.Transactions.AsNoTracking().Where(t => t.Id == transactionId)
            .Select(t => new { t.Name, t.CounterpartyIban }).FirstOrDefaultAsync(ct);
        var key = CategoryMatching.PayeeKey(source?.Name);
        if (source == null || key.Length == 0) return [];
        var iban = CategoryMatching.IbanKey(source.CounterpartyIban);
        var candidates = await db.Transactions.AsNoTracking()
            .Where(t => t.OwnerUserId == ownerId && t.Splitted != 1 && t.Name != null)
            .WhereUncategorized(db, ownerId)
            .Select(t => new { t.Id, t.OwnerUserId, t.AccountId, t.Name, t.CounterpartyIban })
            .ToListAsync(ct);
        var rows = candidates
            .Where(r => !exclude.Contains(r.Id)
                && string.Equals(CategoryMatching.PayeeKey(r.Name), key, StringComparison.Ordinal)
                && (iban == null || CategoryMatching.IbanKey(r.CounterpartyIban) is not { } other || string.Equals(other, iban, StringComparison.Ordinal)))
            .ToList();

        var accountIds = rows.Where(r => r.AccountId.HasValue).Select(r => r.AccountId!.Value).Distinct().ToList();
        var providers = (await db.Accounts.AsNoTracking().Where(a => accountIds.Contains(a.Id)).Select(a => new { a.Id, a.Provider }).ToListAsync(ct))
            .ToDictionary(a => a.Id, a => a.Provider);
        return rows
            .Where(r => TransactionEditRules.Check(db.Scope, r.OwnerUserId, r.AccountId,
                r.AccountId is { } id && providers.TryGetValue(id, out var p) ? p : null) == EditBlock.None)
            .Select(r => r.Id)
            .ToList();
    }
}
