using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>
/// Reads and writes the mapping between PortfolioPerformance depots and imported depot accounts
/// (<see cref="PpPortfolioLink"/>) — the link that lets per-depot FIFO lots attach to held positions.
/// Source-level configuration, independent of any project.
/// </summary>
public class DepotMappingService
{
    private readonly IDbContextFactory<BudgetDbContext> _dbFactory;

    public DepotMappingService(IDbContextFactory<BudgetDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<DepotMapping> GetAsync(int sourceId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        // Mapping / tax configuration is owner-only (the source's owner; refused for everyone else).
        db.Scope?.RequireSourceOwner(sourceId);

        var portfolios = await db.PpPortfolios.AsNoTracking()
            .Where(p => p.SourceId == sourceId).OrderBy(p => p.Name).ToListAsync(ct);
        var lots = await db.PpLots.AsNoTracking()
            .Where(l => l.SourceId == sourceId).ToListAsync(ct);
        var links = await db.PpPortfolioLinks.AsNoTracking()
            .Where(l => l.SourceId == sourceId).ToListAsync(ct);
        // imported depot accounts: portfolio flag set, or the depot account type (7).
        var accounts = await db.Accounts.AsNoTracking()
            .Where(a => a.SourceId == sourceId && (a.Portfolio == 1 || a.Type == 7))
            .OrderBy(a => a.Name).ToListAsync(ct);

        var lotStats = lots.GroupBy(l => l.PortfolioUuid, StringComparer.Ordinal)
            .ToDictionary(g => g.Key,
                g => (count: g.Count(), isins: g.Select(x => x.Isin).Distinct(StringComparer.Ordinal).Count()),
                StringComparer.Ordinal);
        var linkByUuid = links.ToDictionary(l => l.PpPortfolioUuid, l => l.AccountId, StringComparer.Ordinal);

        var rows = portfolios.Select(p =>
        {
            lotStats.TryGetValue(p.Uuid, out var stat);
            return new DepotMappingRow
            {
                PpUuid = p.Uuid,
                PpName = p.Name,
                LotCount = stat.count,
                IsinCount = stat.isins,
                LinkedAccountId = linkByUuid.TryGetValue(p.Uuid, out var a) ? a : null
            };
        }).ToList();

        return new DepotMapping
        {
            Depots = rows,
            Accounts = accounts.Select(a => new DepotAccountOption { Id = a.Id, Name = a.Name ?? $"Account {a.Id}" }).ToList()
        };
    }

    /// <summary>Maps a PP depot to a imported account, or clears the mapping when
    /// <paramref name="accountId"/> is null.</summary>
    public async Task SaveLinkAsync(int sourceId, string ppUuid, long? accountId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        // Mapping / tax configuration is owner-only (the source's owner; refused for everyone else).
        db.Scope?.RequireSourceOwner(sourceId);
        var existing = await db.PpPortfolioLinks
            .FirstOrDefaultAsync(l => l.SourceId == sourceId && l.PpPortfolioUuid == ppUuid, ct);

        if (accountId is null)
        {
            if (existing is not null) { db.PpPortfolioLinks.Remove(existing); await db.SaveChangesAsync(ct); }
            return;
        }

        if (existing is null)
            db.PpPortfolioLinks.Add(new PpPortfolioLink { SourceId = sourceId, PpPortfolioUuid = ppUuid, AccountId = accountId.Value });
        else
            existing.AccountId = accountId.Value;
        await db.SaveChangesAsync(ct);
    }
}
