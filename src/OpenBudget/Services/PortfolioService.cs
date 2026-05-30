using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>
/// Reads imported portfolio holdings for a depot account and shapes them for the Browse holdings
/// card. Only live positions are stored (the import drops MoneyMoney's deleted=1 rows), so this is
/// a straight read-and-decorate: sort by value, compute each position's share, and roll up an
/// asset-class allocation.
/// </summary>
public class PortfolioService
{
    private readonly IDbContextFactory<BudgetDbContext> _dbFactory;
    private readonly ProjectService _projects;

    public PortfolioService(IDbContextFactory<BudgetDbContext> dbFactory, ProjectService projects)
    {
        _dbFactory = dbFactory;
        _projects = projects;
    }

    /// <summary>
    /// Holdings for one account within a project's source, or null when the project doesn't exist
    /// or the account holds no live positions. <paramref name="accountId"/> is the namespaced id.
    /// </summary>
    public async Task<AccountHoldings?> GetAccountHoldingsAsync(int projectId, long accountId, CancellationToken ct = default)
    {
        var project = await _projects.GetProjectAsync(projectId);
        if (project == null) return null;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var holdings = await db.Holdings
            .AsNoTracking()
            .Where(h => h.SourceId == project.SourceId && h.AccountId == accountId)
            .ToListAsync(ct);
        if (holdings.Count == 0) return null;

        // Total drives both the per-row share and the allocation fractions. A depot can briefly
        // total zero (all dust / a fresh empty position), so guard the divisions.
        decimal total = holdings.Sum(h => (decimal)(h.Amount ?? 0));
        var currency = holdings.FirstOrDefault(h => !string.IsNullOrEmpty(h.AmountCurrency))?.AmountCurrency ?? "EUR";

        var rows = holdings
            .OrderByDescending(h => h.Amount ?? 0)
            .Select(h =>
            {
                decimal value = (decimal)(h.Amount ?? 0);
                return new HoldingRow
                {
                    Name = h.Name ?? h.Isin ?? h.Symbol ?? "—",
                    Isin = h.Isin,
                    Wkn = h.Wkn,
                    Symbol = h.Symbol,
                    AssetClassLabel = h.AssetClassLabel,
                    Quantity = h.Quantity,
                    Price = h.Price.HasValue ? (decimal)h.Price.Value : null,
                    PriceCurrency = h.PriceCurrency,
                    Value = value,
                    ShareOfAccount = total != 0 ? value / total : 0m,
                    CostBasis = h.CostBasis.HasValue ? (decimal)h.CostBasis.Value : null,
                    Gain = h.UnrealizedGain.HasValue ? (decimal)h.UnrealizedGain.Value : null,
                    GainFraction = h.UnrealizedGainFraction
                };
            })
            .ToList();

        var allocation = holdings
            .GroupBy(h => h.AssetClassLabel, StringComparer.Ordinal)
            .Select(g =>
            {
                decimal value = g.Sum(h => (decimal)(h.Amount ?? 0));
                return new AssetClassSlice
                {
                    Label = g.Key,
                    Value = value,
                    Fraction = total != 0 ? value / total : 0m
                };
            })
            .OrderByDescending(s => s.Value)
            .ToList();

        return new AccountHoldings
        {
            AccountId = accountId,
            Currency = currency,
            TotalValue = total,
            Rows = rows,
            Allocation = allocation
        };
    }
}
