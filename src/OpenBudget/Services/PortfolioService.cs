using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>
/// Reads imported portfolio holdings for a depot account and shapes them for the Browse holdings
/// card. Only live positions are stored (the import drops the source's deleted=1 rows), so this is
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
            .Where(h => h.OwnerUserId == project.OwnerUserId && h.AccountId == accountId)
            .ToListAsync(ct);
        if (holdings.Count == 0) return null;

        return await BuildAsync(db, project.OwnerUserId ?? string.Empty, holdings, accountId, merge: false, ct);
    }

    /// <summary>
    /// The first project (in the user's order) on <paramref name="ownerId"/> that has a depot account assigned — the
    /// Portfolio view the owner's depot / instrument / tax pages send the user to. Null when there is none.
    /// </summary>
    public async Task<int?> FindPortfolioProjectAsync(string ownerId, CancellationToken ct = default)
    {
        var projects = (await _projects.GetProjectsAsync()).Where(p => p.OwnerUserId == ownerId).ToList();
        if (projects.Count == 0) return null;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var ids = projects.Select(p => p.Id).ToList();
        var withDepot = await db.ProjectAccountAssignments
            .Where(a => ids.Contains(a.ProjectId))
            .Join(db.Accounts.Where(a => a.Type == 7 || a.Portfolio == 1), pa => pa.AccountId, a => a.Id, (pa, _) => pa.ProjectId)
            .Distinct()
            .ToListAsync(ct);
        return projects.FirstOrDefault(p => withDepot.Contains(p.Id))?.Id;
    }

    /// <summary>
    /// Combined holdings across every portfolio (depot) account assigned to a project (the Portfolio
    /// view). Positions sharing an ISIN (or, for ISIN-less crypto, a symbol/name) merge into one row. Null when the project has no assigned depots holding anything.
    /// </summary>
    public async Task<AccountHoldings?> GetProjectHoldingsAsync(int projectId, CancellationToken ct = default)
    {
        var project = await _projects.GetProjectAsync(projectId);
        if (project == null) return null;

        var depotIds = await GetProjectDepotIdsAsync(projectId, ct);
        if (depotIds.Count == 0) return null;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var holdings = await db.Holdings
            .AsNoTracking()
            .Where(h => h.OwnerUserId == project.OwnerUserId && depotIds.Contains(h.AccountId))
            .ToListAsync(ct);
        if (holdings.Count == 0) return null;

        return await BuildAsync(db, project.OwnerUserId ?? string.Empty, holdings, accountId: 0, merge: true, ct);
    }

    /// <summary>The depot (portfolio) accounts assigned to a project.</summary>
    public async Task<List<long>> GetProjectDepotIdsAsync(int projectId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var assignedIds = await db.ProjectAccountAssignments
            .Where(a => a.ProjectId == projectId)
            .Select(a => a.AccountId)
            .ToListAsync(ct);
        if (assignedIds.Count == 0) return [];
        return await db.Accounts
            .Where(a => assignedIds.Contains(a.Id) && (a.Type == 7 || a.Portfolio == 1))
            .Select(a => a.Id)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Per-security top-level classification weights for one taxonomy, keyed by taxonomy key
    /// (<see cref="HoldingRow.TaxonomyKey"/>: "isin:…" or "uuid:…"). Each inner
    /// dictionary maps a top-level class (e.g. a region) to the security's fraction in it; the
    /// remainder up to 1 is unclassified. Securities not present have no taxonomy coverage.
    /// </summary>
    public async Task<Dictionary<string, Dictionary<string, decimal>>> GetSecurityTaxonomyWeightsAsync(
        int projectId, string taxonomyName, IReadOnlyCollection<string> taxonomyKeys, CancellationToken ct = default)
    {
        var result = new Dictionary<string, Dictionary<string, decimal>>(StringComparer.Ordinal);
        if (taxonomyKeys.Count == 0) return result;
        var project = await _projects.GetProjectAsync(projectId);
        if (project == null) return result;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await LoadTaxonomyRowsAsync(db, project.OwnerUserId ?? string.Empty, taxonomyKeys, taxonomyName, ct);

        foreach (var g in rows.GroupBy(TaxonomyKeyOf, StringComparer.Ordinal))
        {
            var byClass = new Dictionary<string, decimal>(StringComparer.Ordinal);
            foreach (var t in g)
            {
                var top = TopLevel(t);
                byClass.TryGetValue(top, out var v);
                byClass[top] = v + t.WeightBp / 10000m;
            }
            result[g.Key] = byClass;
        }
        return result;
    }

    /// <summary>
    /// Catalogued PortfolioPerformance securities of the project's owner that aren't already in
    /// <paramref name="excludeIsins"/> (i.e. not currently held), ordered by name — the candidates a
    /// user can add to a rebalance to plan buying into a position they don't yet own.
    /// </summary>
    public async Task<List<CatalogSecurity>> GetAddableSecuritiesAsync(
        int projectId, IReadOnlyCollection<string> excludeIsins, CancellationToken ct = default)
    {
        var project = await _projects.GetProjectAsync(projectId);
        if (project == null) return new List<CatalogSecurity>();

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var exclude = new HashSet<string>(excludeIsins, StringComparer.Ordinal);
        var rows = await db.PpSecurities
            .AsNoTracking()
            .Where(s => s.OwnerUserId == project.OwnerUserId && s.Isin != "")   // adding non-held is ISIN-keyed
            .OrderBy(s => s.Name)
            .ToListAsync(ct);

        return rows
            .Where(s => !exclude.Contains(s.Isin))
            .Select(s => new CatalogSecurity { Isin = s.Isin, Name = s.Name, Symbol = s.TickerSymbol, Wkn = s.Wkn, Price = s.Price, Currency = s.Currency })
            .ToList();
    }

    /// <summary>
    /// The reconstructed FIFO tax lots for a project, grouped by ISIN — scoped to the project's depot
    /// accounts via the PortfolioPerformance depot links. Empty when the project has no depots, no
    /// linked PP depots, or no lots. Drives the realised-gain estimate on the rebalance page.
    /// </summary>
    public async Task<Dictionary<string, List<OpenLot>>> GetProjectLotsAsync(int projectId, CancellationToken ct = default)
    {
        var empty = new Dictionary<string, List<OpenLot>>(StringComparer.Ordinal);
        var project = await _projects.GetProjectAsync(projectId);
        if (project == null) return empty;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var assignedIds = await db.ProjectAccountAssignments
            .Where(a => a.ProjectId == projectId).Select(a => a.AccountId).ToListAsync(ct);
        if (assignedIds.Count == 0) return empty;

        var depotIds = await db.Accounts
            .Where(a => assignedIds.Contains(a.Id) && (a.Type == 7 || a.Portfolio == 1))
            .Select(a => a.Id).ToListAsync(ct);
        if (depotIds.Count == 0) return empty;

        // The PP depots linked to those imported depots.
        var ppUuids = await db.PpPortfolioLinks
            .Where(l => l.OwnerUserId == project.OwnerUserId && depotIds.Contains(l.AccountId))
            .Select(l => l.PpPortfolioUuid).ToListAsync(ct);
        if (ppUuids.Count == 0) return empty;

        var lots = await db.PpLots.AsNoTracking()
            .Where(l => l.OwnerUserId == project.OwnerUserId && l.Isin != "" && ppUuids.Contains(l.PortfolioUuid))
            .ToListAsync(ct);

        return lots
            .GroupBy(l => l.Isin, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => g.Select(x => new OpenLot
                {
                    PortfolioUuid = x.PortfolioUuid,
                    Isin = x.Isin,
                    Acquired = x.Acquired,
                    Shares = x.Shares,
                    Cost = x.Cost,
                    Currency = x.Currency
                }).ToList(),
                StringComparer.Ordinal);
    }

    /// <summary>
    /// Tax profiles for a project keyed by rebalance-position identity ("isin:…" for securities, the
    /// mapped holding key for ISIN-less instruments): the FIFO lots (scoped to the project's linked
    /// depots) plus the resolved regime from the per-instrument tax settings. The rebalance read-out
    /// looks these up by position key to estimate realised gains. ISIN-less lots with no instrument
    /// mapping are dropped (nothing to attach them to).
    /// </summary>
    public async Task<Dictionary<string, TaxProfile>> GetProjectTaxProfilesAsync(int projectId, CancellationToken ct = default)
    {
        var empty = new Dictionary<string, TaxProfile>(StringComparer.Ordinal);
        var project = await _projects.GetProjectAsync(projectId);
        if (project == null) return empty;
        string ownerId = project.OwnerUserId ?? string.Empty;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var assignedIds = await db.ProjectAccountAssignments
            .Where(a => a.ProjectId == projectId).Select(a => a.AccountId).ToListAsync(ct);
        if (assignedIds.Count == 0) return empty;
        var depotIds = await db.Accounts
            .Where(a => assignedIds.Contains(a.Id) && (a.Type == 7 || a.Portfolio == 1))
            .Select(a => a.Id).ToListAsync(ct);
        if (depotIds.Count == 0) return empty;
        var ppUuids = await db.PpPortfolioLinks
            .Where(l => l.OwnerUserId == ownerId && depotIds.Contains(l.AccountId))
            .Select(l => l.PpPortfolioUuid).ToListAsync(ct);
        if (ppUuids.Count == 0) return empty;

        var lots = await db.PpLots.AsNoTracking()
            .Where(l => l.OwnerUserId == ownerId && ppUuids.Contains(l.PortfolioUuid)).ToListAsync(ct);
        if (lots.Count == 0) return empty;

        var instrumentLinks = await db.PpSecurityLinks.AsNoTracking()
            .Where(l => l.OwnerUserId == ownerId)
            .ToDictionaryAsync(l => l.PpSecurityUuid, l => l.MmHoldingKey, ct);
        var settings = await db.SecurityTaxSettings.AsNoTracking()
            .Where(s => s.OwnerUserId == ownerId).ToDictionaryAsync(s => s.SecurityUuid, ct);

        // Group lots under the rebalance position they belong to.
        var byKey = new Dictionary<string, List<OpenLot>>(StringComparer.Ordinal);
        var keyUuid = new Dictionary<string, string>(StringComparer.Ordinal);
        var keyIsin = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var l in lots)
        {
            string key;
            if (!string.IsNullOrEmpty(l.Isin)) key = "isin:" + l.Isin;
            else if (instrumentLinks.TryGetValue(l.SecurityUuid, out var mmKey)) key = mmKey;
            else continue;   // ISIN-less but unmapped
            if (!byKey.TryGetValue(key, out var list))
            {
                byKey[key] = list = new List<OpenLot>();
                keyUuid[key] = l.SecurityUuid;
                keyIsin[key] = l.Isin;
            }
            list.Add(new OpenLot
            {
                PortfolioUuid = l.PortfolioUuid,
                SecurityUuid = l.SecurityUuid,
                Isin = l.Isin,
                Acquired = l.Acquired,
                Shares = l.Shares,
                Cost = l.Cost,
                Currency = l.Currency
            });
        }

        var result = new Dictionary<string, TaxProfile>(StringComparer.Ordinal);
        foreach (var (key, lotList) in byKey)
        {
            InstrumentTaxKind kind;
            decimal rate;
            DateOnly? acquiredOverride = null;
            if (settings.TryGetValue(keyUuid[key], out var ov))
            {
                kind = (InstrumentTaxKind)ov.Kind;
                rate = ov.TeilfreistellungBp / 10000m;
                acquiredOverride = ov.AcquiredOverride;
            }
            else
            {
                kind = SecurityTaxService.DefaultKind(keyIsin[key]);
                rate = SecurityTaxService.DefaultTeilfreistellungBp / 10000m;
            }
            // An inherited/gifted asset's §23 clock runs from the predecessor's acquisition, so
            // re-date its lots to the override before realising.
            var lotsForProfile = acquiredOverride is { } acq
                ? lotList.Select(l => new OpenLot { PortfolioUuid = l.PortfolioUuid, SecurityUuid = l.SecurityUuid, Isin = l.Isin, Acquired = acq, Shares = l.Shares, Cost = l.Cost, Currency = l.Currency }).ToList()
                : lotList;
            var (assetKind, years) = TaxMath.Resolve(kind);
            result[key] = new TaxProfile
            {
                Lots = lotsForProfile,
                AssetKind = assetKind,
                SpekulationsfristYears = years,
                TeilfreistellungRate = rate
            };
        }
        return result;
    }

    private static async Task<AccountHoldings> BuildAsync(
        BudgetDbContext db, string ownerId, List<Holding> holdings, long accountId, bool merge, CancellationToken ct)
    {
        // Total drives both the per-row share and the allocation fractions. A depot can briefly
        // total zero (all dust / a fresh empty position), so guard the divisions.
        decimal total = holdings.Sum(h => (decimal)(h.Amount ?? 0));
        var currency = holdings.FirstOrDefault(h => !string.IsNullOrEmpty(h.AmountCurrency))?.AmountCurrency ?? "EUR";

        var linkedUuids = await LinkedSecurityUuidsAsync(db, ownerId, ct);
        string? TaxKey(Holding h) => TaxonomyKeyOf(h, linkedUuids);
        var rows = merge ? BuildMergedRows(holdings, total, TaxKey) : BuildRows(holdings, total, TaxKey);

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

        var taxonomies = await BuildLookThroughAsync(db, ownerId, holdings, total, TaxKey, ct);

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

    /// <summary>
    /// Key a holding's taxonomy rows are found by: "isin:" + ISIN when it has one, else "uuid:" +
    /// the PortfolioPerformance security uuid — its own (holdings written by the PortfolioPerformance
    /// sync) or the one linked to it via <see cref="PpSecurityLink"/>. Null when neither exists.
    /// </summary>
    internal static string? TaxonomyKeyOf(Holding h, IReadOnlyDictionary<string, string> uuidByHoldingKey)
    {
        if (!string.IsNullOrEmpty(h.Isin)) return "isin:" + h.Isin;
        if (!string.IsNullOrEmpty(h.PpSecurityUuid)) return "uuid:" + h.PpSecurityUuid;
        return uuidByHoldingKey.TryGetValue(KeyOf(h), out var uuid) ? "uuid:" + uuid : null;
    }

    /// <summary>Key of a taxonomy row, matching <see cref="TaxonomyKeyOf(Holding, IReadOnlyDictionary{string, string})"/>.</summary>
    internal static string TaxonomyKeyOf(SecurityTaxonomy t) =>
        !string.IsNullOrEmpty(t.Isin) ? "isin:" + t.Isin : "uuid:" + t.SecurityUuid;

    // Holding key → PP security uuid from the owner's manual instrument links.
    private static async Task<Dictionary<string, string>> LinkedSecurityUuidsAsync(BudgetDbContext db, string ownerId, CancellationToken ct)
    {
        var links = await db.PpSecurityLinks.AsNoTracking()
            .Where(l => l.OwnerUserId == ownerId)
            .Select(l => new { l.MmHoldingKey, l.PpSecurityUuid })
            .ToListAsync(ct);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var l in links) result[l.MmHoldingKey] = l.PpSecurityUuid;
        return result;
    }

    // Taxonomy rows of an owner for the given taxonomy keys, optionally limited to one taxonomy.
    private static async Task<List<SecurityTaxonomy>> LoadTaxonomyRowsAsync(
        BudgetDbContext db, string ownerId, IEnumerable<string> taxonomyKeys, string? taxonomyName, CancellationToken ct)
    {
        var keys = taxonomyKeys.ToHashSet(StringComparer.Ordinal);
        var isins = keys.Where(k => k.StartsWith("isin:", StringComparison.Ordinal)).Select(k => k[5..]).ToList();
        var uuids = keys.Where(k => k.StartsWith("uuid:", StringComparison.Ordinal)).Select(k => k[5..]).ToList();
        if (isins.Count == 0 && uuids.Count == 0) return new List<SecurityTaxonomy>();
        var query = db.SecurityTaxonomies.AsNoTracking()
            .Where(t => t.OwnerUserId == ownerId && (isins.Contains(t.Isin) || (t.Isin == "" && uuids.Contains(t.SecurityUuid))));
        if (taxonomyName != null) query = query.Where(t => t.TaxonomyName == taxonomyName);
        var rows = await query.ToListAsync(ct);
        return rows.Where(t => keys.Contains(TaxonomyKeyOf(t))).ToList();
    }

    private static List<HoldingRow> BuildRows(List<Holding> holdings, decimal total, Func<Holding, string?> taxKey) =>
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
                TaxonomyKey = taxKey(h),
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

    private static List<HoldingRow> BuildMergedRows(List<Holding> holdings, decimal total, Func<Holding, string?> taxKey)
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
                TaxonomyKey = members.Select(taxKey).FirstOrDefault(k => k != null),
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
    /// classifications by weights matched on the holding's taxonomy key (ISIN, else PP security uuid). Holdings with no (or partial) coverage contribute
    /// their remaining value to MoneyMoney's asset type (asset-class taxonomy, see
    /// <see cref="IsAssetClassTaxonomy"/>) or to "Unclassified" (others), so each taxonomy's slices sum to the
    /// account total. Returns only taxonomies that classify at least some value.
    /// </summary>
    private static async Task<List<TaxonomyLookThrough>> BuildLookThroughAsync(
        BudgetDbContext db, string ownerId, List<Holding> holdings, decimal total, Func<Holding, string?> taxKey, CancellationToken ct)
    {
        // Value per taxonomy key within this account (same ETF held twice would sum).
        var valueByKey = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var h in holdings)
        {
            if (taxKey(h) is not { } key) continue;
            valueByKey.TryGetValue(key, out var v);
            valueByKey[key] = v + (decimal)(h.Amount ?? 0);
        }
        if (valueByKey.Count == 0) return new List<TaxonomyLookThrough>();

        var taxRows = await LoadTaxonomyRowsAsync(db, ownerId, valueByKey.Keys, taxonomyName: null, ct);
        if (taxRows.Count == 0) return new List<TaxonomyLookThrough>();

        var result = new List<TaxonomyLookThrough>();
        foreach (var taxGroup in taxRows.GroupBy(t => t.TaxonomyName, StringComparer.Ordinal))
        {
            bool assetClasses = IsAssetClassTaxonomy(taxGroup.Key);
            // Labels merge case-insensitively so MoneyMoney's "Equity" joins PP's "equity" slice;
            // the first spelling seen (PP's, since those are added first) wins.
            var byLabel = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            void Add(string label, decimal value)
            {
                byLabel.TryGetValue(label, out var bucket);
                byLabel[label] = bucket + value;
            }

            var assignedByKey = new Dictionary<string, decimal>(StringComparer.Ordinal);
            foreach (var row in taxGroup)
            {
                var rowKey = TaxonomyKeyOf(row);
                if (!valueByKey.TryGetValue(rowKey, out var keyValue)) continue;
                decimal weight = row.WeightBp / 10000m;
                Add(TopLevel(row), keyValue * weight);
                assignedByKey.TryGetValue(rowKey, out var a);
                assignedByKey[rowKey] = a + weight;
            }

            // Whatever PP doesn't place — the unassigned part of classified securities and every
            // holding PP doesn't classify or can't be matched to — goes, for the
            // asset-class taxonomy, to MoneyMoney's own type for that position, and for any other
            // taxonomy to "Unclassified". Either way the slices sum to the account total.
            foreach (var h in holdings)
            {
                decimal amount = (decimal)(h.Amount ?? 0);
                decimal assigned = taxKey(h) is { } hk && assignedByKey.TryGetValue(hk, out var w)
                    ? Math.Min(1m, w) : 0m;
                decimal rest = amount * (1m - assigned);
                if (rest == 0m) continue;
                Add(assetClasses ? h.AssetClassLabel : UnclassifiedLabel, rest);
            }

            var slices = byLabel
                .Where(kv => Math.Abs(kv.Value) > 0.005m)
                .Select(kv => new AssetClassSlice
                {
                    Label = kv.Key,
                    Value = kv.Value,
                    Fraction = total != 0 ? kv.Value / total : 0m
                })
                .OrderBy(s => string.Equals(s.Label, UnclassifiedLabel, StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(s => s.Value)
                .ToList();

            result.Add(new TaxonomyLookThrough { TaxonomyName = taxGroup.Key, Slices = slices });
        }

        return result.OrderBy(t => t.TaxonomyName, StringComparer.Ordinal).ToList();
    }

    public const string UnclassifiedLabel = "Unclassified";

    /// <summary>
    /// The PortfolioPerformance taxonomy that describes asset classes — PP's default taxonomy,
    /// named "Asset Classes" (English) or "Anlageklassen" (German); matched by name, ignoring case
    /// and surrounding whitespace. For it, positions PP doesn't classify fall back to MoneyMoney's
    /// own asset type instead of "Unclassified".
    /// </summary>
    public static bool IsAssetClassTaxonomy(string? name) =>
        name?.Trim().ToUpperInvariant() is "ASSET CLASSES" or "ASSET CLASS" or "ANLAGEKLASSEN" or "ANLAGEKLASSE"
            or "ASSETKLASSEN" or "ASSET ALLOCATION";

    /// <summary>Top-level classification name: the first segment of the path, or the leaf name when flat.</summary>
    private static string TopLevel(SecurityTaxonomy row)
    {
        if (string.IsNullOrEmpty(row.ClassificationPath)) return row.ClassificationName;
        int slash = row.ClassificationPath.IndexOf(" / ", StringComparison.Ordinal);
        return slash < 0 ? row.ClassificationPath : row.ClassificationPath[..slash];
    }
}
