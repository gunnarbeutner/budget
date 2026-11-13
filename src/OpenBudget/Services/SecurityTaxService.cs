using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>
/// Reads and writes per-instrument tax parameters (<see cref="SecurityTaxSetting"/>): the tax kind
/// and, for securities, the Teilfreistellung rate. Lists every instrument that has reconstructed lots
/// — ISIN securities and the ISIN-less ones (crypto, real estate) alike — keyed by PortfolioPerformance
/// security uuid.
/// </summary>
public class SecurityTaxService
{
    /// <summary>Default when a security has no override: an equity fund's 30%.</summary>
    public const int DefaultTeilfreistellungBp = 3000;

    private readonly IDbContextFactory<BudgetDbContext> _dbFactory;

    public SecurityTaxService(IDbContextFactory<BudgetDbContext> dbFactory) => _dbFactory = dbFactory;

    /// <summary>The default kind for an unconfigured instrument: ISIN-bearing ⇒ a security, ISIN-less
    /// ⇒ crypto §23 (the common case; the user reclassifies real estate to the 10-year kind).</summary>
    public static InstrumentTaxKind DefaultKind(string isin) =>
        string.IsNullOrEmpty(isin) ? InstrumentTaxKind.MovableSection23 : InstrumentTaxKind.Security;

    public async Task<List<SecurityTaxRow>> GetSettingsAsync(string ownerId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        // Mapping / tax configuration is owner-only (the source's owner; refused for everyone else).
        db.Scope?.RequireOwner(ownerId);
        var lotUuids = await db.PpLots.AsNoTracking()
            .Where(l => l.OwnerUserId == ownerId).Select(l => l.SecurityUuid).Distinct().ToListAsync(ct);
        if (lotUuids.Count == 0) return new List<SecurityTaxRow>();

        var secs = await db.PpSecurities.AsNoTracking()
            .Where(s => s.OwnerUserId == ownerId && lotUuids.Contains(s.Uuid))
            .ToDictionaryAsync(s => s.Uuid, ct);
        var overrides = await db.SecurityTaxSettings.AsNoTracking()
            .Where(s => s.OwnerUserId == ownerId)
            .ToDictionaryAsync(s => s.SecurityUuid, ct);

        return lotUuids
            .Select(u =>
            {
                secs.TryGetValue(u, out var sec);
                var isin = sec?.Isin ?? "";
                var has = overrides.TryGetValue(u, out var ov);
                return new SecurityTaxRow
                {
                    Uuid = u,
                    Isin = isin,
                    Name = sec?.Name ?? u,
                    Kind = has ? ov!.Kind : (int)DefaultKind(isin),
                    TeilfreistellungBp = has ? ov!.TeilfreistellungBp : DefaultTeilfreistellungBp,
                    AcquiredOverride = has ? ov!.AcquiredOverride : null
                };
            })
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task SetAsync(string ownerId, string uuid, int kind, int teilfreistellungBp, DateOnly? acquiredOverride, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        // Mapping / tax configuration is owner-only (the source's owner; refused for everyone else).
        db.Scope?.RequireOwner(ownerId);
        var existing = await db.SecurityTaxSettings
            .FirstOrDefaultAsync(s => s.OwnerUserId == ownerId && s.SecurityUuid == uuid, ct);
        if (existing is null)
            db.SecurityTaxSettings.Add(new SecurityTaxSetting { OwnerUserId = ownerId, SecurityUuid = uuid, Kind = kind, TeilfreistellungBp = teilfreistellungBp, AcquiredOverride = acquiredOverride });
        else
        {
            existing.Kind = kind;
            existing.TeilfreistellungBp = teilfreistellungBp;
            existing.AcquiredOverride = acquiredOverride;
        }
        await db.SaveChangesAsync(ct);
    }
}
