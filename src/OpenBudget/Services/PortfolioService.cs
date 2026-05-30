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

        return await BuildAsync(db, project.SourceId, holdings, accountId, merge: false, ct);
    }

    /// <summary>
    /// Combined holdings across several of a project's portfolio accounts — a unified view when a
    /// project holds more than one depot. Positions sharing an ISIN (or, for ISIN-less crypto, a
    /// symbol/name) are merged across accounts into one row. Null when the accounts hold nothing.
    /// </summary>
    public async Task<AccountHoldings?> GetCombinedHoldingsAsync(
        int projectId, IReadOnlyCollection<long> accountIds, CancellationToken ct = default)
    {
        if (accountIds.Count == 0) return null;
        var project = await _projects.GetProjectAsync(projectId);
        if (project == null) return null;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var holdings = await db.Holdings
            .AsNoTracking()
            .Where(h => h.SourceId == project.SourceId && accountIds.Contains(h.AccountId))
            .ToListAsync(ct);
        if (holdings.Count == 0) return null;

        return await BuildAsync(db, project.SourceId, holdings, accountId: 0, merge: true, ct);
    }

    /// <summary>
    /// Combined holdings across every portfolio (depot) account assigned to a project — resolves
    /// the depots itself, so callers without the Browse tree can use it (e.g. the rebalance page).
    /// Null when the project has no assigned depots holding anything.
    /// </summary>
    public async Task<AccountHoldings?> GetProjectHoldingsAsync(int projectId, CancellationToken ct = default)
    {
        var project = await _projects.GetProjectAsync(projectId);
        if (project == null) return null;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var assignedIds = await db.ProjectAccountAssignments
            .Where(a => a.ProjectId == projectId)
            .Select(a => a.AccountId)
            .ToListAsync(ct);
        if (assignedIds.Count == 0) return null;

        var depotIds = await db.Accounts
            .Where(a => assignedIds.Contains(a.Id) && (a.Type == 7 || a.Portfolio == 1))
            .Select(a => a.Id)
            .ToListAsync(ct);
        if (depotIds.Count == 0) return null;

        var holdings = await db.Holdings
            .AsNoTracking()
            .Where(h => h.SourceId == project.SourceId && depotIds.Contains(h.AccountId))
            .ToListAsync(ct);
        if (holdings.Count == 0) return null;

        return await BuildAsync(db, project.SourceId, holdings, accountId: 0, merge: true, ct);
    }

    private static async Task<AccountHoldings> BuildAsync(
        BudgetDbContext db, int sourceId, List<Holding> holdings, long accountId, bool merge, CancellationToken ct)
    {
        // Total drives both the per-row share and the allocation fractions. A depot can briefly
        // total zero (all dust / a fresh empty position), so guard the divisions.
        decimal total = holdings.Sum(h => (decimal)(h.Amount ?? 0));
        var currency = holdings.FirstOrDefault(h => !string.IsNullOrEmpty(h.AmountCurrency))?.AmountCurrency ?? "EUR";

        var rows = merge ? BuildMergedRows(holdings, total) : BuildRows(holdings, total);

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

        var taxonomies = await BuildLookThroughAsync(db, sourceId, holdings, total, ct);

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

    // Stable identity for a holding row: ISIN, else symbol, else name (prefixed so an ISIN value
    // can't collide with a same-text symbol). Also the merge key for combined views.
    private static string KeyOf(Holding h) =>
        !string.IsNullOrEmpty(h.Isin) ? "isin:" + h.Isin
        : !string.IsNullOrEmpty(h.Symbol) ? "sym:" + h.Symbol
        : "name:" + (h.Name ?? "");

    private static List<HoldingRow> BuildRows(List<Holding> holdings, decimal total) =>
        holdings
            .OrderByDescending(h => h.Amount ?? 0)
            .Select(h => new HoldingRow
            {
                Key = KeyOf(h),
                Name = h.Name ?? h.Isin ?? h.Symbol ?? "—",
                Isin = h.Isin,
                Wkn = h.Wkn,
                Symbol = h.Symbol,
                AssetClassLabel = h.AssetClassLabel,
                Quantity = h.Quantity,
                Price = h.Price.HasValue ? (decimal)h.Price.Value : null,
                PriceCurrency = h.PriceCurrency,
                Value = (decimal)(h.Amount ?? 0),
                ShareOfAccount = total != 0 ? (decimal)(h.Amount ?? 0) / total : 0m,
                CostBasis = h.CostBasis.HasValue ? (decimal)h.CostBasis.Value : null,
                Gain = h.UnrealizedGain.HasValue ? (decimal)h.UnrealizedGain.Value : null,
                GainFraction = h.UnrealizedGainFraction
            })
            .ToList();

    private static List<HoldingRow> BuildMergedRows(List<Holding> holdings, decimal total)
    {
        // Merge by KeyOf so the same security held in two depots becomes one row (and ISIN-less
        // crypto still merges by symbol/name).
        var rows = new List<HoldingRow>();
        foreach (var g in holdings.GroupBy(KeyOf, StringComparer.Ordinal))
        {
            var members = g.ToList();
            decimal value = members.Sum(h => (decimal)(h.Amount ?? 0));
            double quantity = members.Sum(h => h.Quantity);
            // Cost basis only when every member has one, else a partial gain would mislead.
            decimal? costBasis = members.All(h => h.CostBasis.HasValue)
                ? members.Sum(h => (decimal)h.CostBasis!.Value)
                : null;
            decimal? gain = costBasis.HasValue ? value - costBasis.Value : null;
            var first = members[0];
            rows.Add(new HoldingRow
            {
                Key = g.Key,
                Name = members.Select(h => h.Name).FirstOrDefault(n => !string.IsNullOrEmpty(n))
                    ?? first.Isin ?? first.Symbol ?? "—",
                Isin = first.Isin,
                Wkn = first.Wkn,
                Symbol = first.Symbol,
                AssetClassLabel = first.AssetClassLabel,
                Quantity = quantity,
                Price = first.Price.HasValue ? (decimal)first.Price.Value : null,
                PriceCurrency = first.PriceCurrency,
                Value = value,
                ShareOfAccount = total != 0 ? value / total : 0m,
                CostBasis = costBasis,
                Gain = gain,
                GainFraction = costBasis is > 0 && gain.HasValue ? (double)(gain.Value / costBasis.Value) : null
            });
        }
        return rows.OrderByDescending(r => r.Value).ToList();
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
            foreach (var row in taxGroup)
            {
                if (!valueByIsin.TryGetValue(row.Isin, out var isinValue)) continue;
                decimal weight = row.WeightBp / 10000m;
                string topLevel = TopLevel(row);
                byTopLevel.TryGetValue(topLevel, out var bucket);
                byTopLevel[topLevel] = bucket + isinValue * weight;
            }

            // Everything the taxonomy doesn't place is Unclassified: the unassigned slice of
            // classified securities AND every holding with no matching row at all — most importantly
            // ISIN-less crypto. Computing it as total minus the classified value keeps the slices
            // summing to the account total, instead of silently dropping the unmatched value (which
            // made a crypto-heavy combined view sum to far less than 100%).
            decimal classified = byTopLevel.Values.Sum();
            decimal unclassified = Math.Max(0m, total - classified);

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
