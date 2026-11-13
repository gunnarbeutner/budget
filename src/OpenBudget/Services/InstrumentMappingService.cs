using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>
/// Reads and writes the mapping between ISIN-less PortfolioPerformance instruments (crypto, real
/// estate) and imported holdings (<see cref="PpSecurityLink"/>) — the manual join that lets their
/// reconstructed FIFO lots attach to a held position. ISIN-bearing securities join automatically and
/// aren't listed here. Per-user configuration.
/// </summary>
public class InstrumentMappingService
{
    private readonly IDbContextFactory<BudgetDbContext> _dbFactory;

    public InstrumentMappingService(IDbContextFactory<BudgetDbContext> dbFactory) => _dbFactory = dbFactory;

    /// <summary>Identity key for an ISIN-less holding — symbol first (crypto), else name (real estate).
    /// Mirrors the holdings views' key so a mapping target lines up with the rebalance rows.</summary>
    private static string HoldingKey(Holding h) =>
        !string.IsNullOrEmpty(h.Symbol) ? "sym:" + h.Symbol : "name:" + (h.Name ?? "");

    public async Task<InstrumentMapping> GetAsync(string ownerId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        // Mapping / tax configuration is owner-only (the source's owner; refused for everyone else).
        db.Scope?.RequireOwner(ownerId);

        // ISIN-less instruments that actually have reconstructed lots — the ones worth mapping.
        var lotsByUuid = (await db.PpLots.AsNoTracking()
                .Where(l => l.OwnerUserId == ownerId && l.Isin == "")
                .Select(l => l.SecurityUuid)
                .ToListAsync(ct))
            .GroupBy(u => u, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var names = await db.PpSecurities.AsNoTracking()
            .Where(s => s.OwnerUserId == ownerId && s.Isin == "")
            .ToDictionaryAsync(s => s.Uuid, s => s.Name, ct);

        var links = await db.PpSecurityLinks.AsNoTracking()
            .Where(l => l.OwnerUserId == ownerId).ToListAsync(ct);
        var linkByUuid = links.ToDictionary(l => l.PpSecurityUuid, l => l.MmHoldingKey, StringComparer.Ordinal);

        var instruments = lotsByUuid.Keys
            .Select(uuid => new InstrumentMappingRow
            {
                PpUuid = uuid,
                PpName = names.GetValueOrDefault(uuid, uuid),
                LotCount = lotsByUuid[uuid],
                LinkedHoldingKey = linkByUuid.TryGetValue(uuid, out var k) ? k : null
            })
            .OrderBy(r => r.PpName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // ISIN-less imported holdings, deduplicated by identity key (same crypto across depots).
        var holdings = await db.Holdings.AsNoTracking()
            .Where(h => h.OwnerUserId == ownerId && (h.Isin == null || h.Isin == ""))
            .ToListAsync(ct);
        var options = holdings
            .GroupBy(HoldingKey, StringComparer.Ordinal)
            .Select(g => new HoldingOption { Key = g.Key, Name = g.Select(h => h.Name).FirstOrDefault(n => !string.IsNullOrEmpty(n)) ?? g.Key })
            .OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new InstrumentMapping { Instruments = instruments, Holdings = options };
    }

    /// <summary>Maps a PP instrument to a imported holding, or clears it when
    /// <paramref name="holdingKey"/> is null/empty.</summary>
    public async Task SaveLinkAsync(string ownerId, string ppUuid, string? holdingKey, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        // Mapping / tax configuration is owner-only (the source's owner; refused for everyone else).
        db.Scope?.RequireOwner(ownerId);
        var existing = await db.PpSecurityLinks
            .FirstOrDefaultAsync(l => l.OwnerUserId == ownerId && l.PpSecurityUuid == ppUuid, ct);

        if (string.IsNullOrEmpty(holdingKey))
        {
            if (existing is not null) { db.PpSecurityLinks.Remove(existing); await db.SaveChangesAsync(ct); }
            return;
        }

        if (existing is null)
            db.PpSecurityLinks.Add(new PpSecurityLink { OwnerUserId = ownerId, PpSecurityUuid = ppUuid, MmHoldingKey = holdingKey });
        else
            existing.MmHoldingKey = holdingKey;
        await db.SaveChangesAsync(ct);
    }
}
