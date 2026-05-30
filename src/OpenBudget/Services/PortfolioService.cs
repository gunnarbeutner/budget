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

        var taxonomies = await BuildLookThroughAsync(db, project.SourceId, holdings, total, ct);

        return new AccountHoldings
        {
            AccountId = accountId,
            Currency = currency,
            TotalValue = total,
            Rows = rows,
            Allocation = allocation,
            Taxonomies = taxonomies
        };
    }

    /// <summary>
    /// Distributes each holding's value across a PortfolioPerformance taxonomy's top-level
    /// classifications by ISIN-matched weights. Holdings with no (or partial) coverage contribute
    /// their remaining value to an "Unclassified" slice, so each taxonomy's slices sum to the
    /// account total. Returns only taxonomies that classify at least some value.
    /// </summary>
    private static async Task<List<TaxonomyLookThrough>> BuildLookThroughAsync(
        BudgetDbContext db, int sourceId, List<Holding> holdings, decimal total, CancellationToken ct)
    {
        // Value per ISIN within this account (same ETF held twice would sum).
        var valueByIsin = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var h in holdings)
        {
            if (string.IsNullOrEmpty(h.Isin)) continue;
            valueByIsin.TryGetValue(h.Isin, out var v);
            valueByIsin[h.Isin] = v + (decimal)(h.Amount ?? 0);
        }
        if (valueByIsin.Count == 0) return new List<TaxonomyLookThrough>();

        var isins = valueByIsin.Keys.ToList();
        var taxRows = await db.SecurityTaxonomies
            .AsNoTracking()
            .Where(t => t.SourceId == sourceId && isins.Contains(t.Isin))
            .ToListAsync(ct);
        if (taxRows.Count == 0) return new List<TaxonomyLookThrough>();

        var result = new List<TaxonomyLookThrough>();
        foreach (var taxGroup in taxRows.GroupBy(t => t.TaxonomyName, StringComparer.Ordinal))
        {
            var byTopLevel = new Dictionary<string, decimal>(StringComparer.Ordinal);
            // Track how much of each ISIN's value this taxonomy assigns, to compute the remainder.
            var assignedFractionByIsin = new Dictionary<string, decimal>(StringComparer.Ordinal);

            foreach (var row in taxGroup)
            {
                if (!valueByIsin.TryGetValue(row.Isin, out var isinValue)) continue;
                decimal weight = row.WeightBp / 10000m;
                string topLevel = TopLevel(row);
                byTopLevel.TryGetValue(topLevel, out var bucket);
                byTopLevel[topLevel] = bucket + isinValue * weight;
                assignedFractionByIsin.TryGetValue(row.Isin, out var af);
                assignedFractionByIsin[row.Isin] = af + weight;
            }

            // Unassigned remainder: each ISIN's value not covered by this taxonomy, plus the value
            // of holdings with no row in this taxonomy at all.
            decimal unclassified = 0m;
            foreach (var (isin, value) in valueByIsin)
            {
                assignedFractionByIsin.TryGetValue(isin, out var af);
                if (af < 1m) unclassified += value * (1m - af);
            }

            var slices = byTopLevel
                .Select(kv => new AssetClassSlice
                {
                    Label = kv.Key,
                    Value = kv.Value,
                    Fraction = total != 0 ? kv.Value / total : 0m
                })
                .OrderByDescending(s => s.Value)
                .ToList();
            if (unclassified > 0.005m)
            {
                slices.Add(new AssetClassSlice
                {
                    Label = "Unclassified",
                    Value = unclassified,
                    Fraction = total != 0 ? unclassified / total : 0m
                });
            }

            result.Add(new TaxonomyLookThrough { TaxonomyName = taxGroup.Key, Slices = slices });
        }

        return result.OrderBy(t => t.TaxonomyName, StringComparer.Ordinal).ToList();
    }

    /// <summary>Top-level classification name: the first segment of the path, or the leaf name when flat.</summary>
    private static string TopLevel(SecurityTaxonomy row)
    {
        if (string.IsNullOrEmpty(row.ClassificationPath)) return row.ClassificationName;
        int slash = row.ClassificationPath.IndexOf(" / ", StringComparison.Ordinal);
        return slash < 0 ? row.ClassificationPath : row.ClassificationPath[..slash];
    }
}
