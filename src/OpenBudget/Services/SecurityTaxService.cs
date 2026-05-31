using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>
/// Reads and writes per-security tax parameters (<see cref="SecurityTaxSetting"/>) — for now the
/// Teilfreistellung rate. Lists the securities that actually have reconstructed lots (the ones a
/// realised gain can be computed for), so the settings screen stays to the tax-relevant set rather
/// than the whole catalogue.
/// </summary>
public class SecurityTaxService
{
    /// <summary>Default when a security has no override: an equity fund's 30%.</summary>
    public const int DefaultTeilfreistellungBp = 3000;

    private readonly IDbContextFactory<BudgetDbContext> _dbFactory;

    public SecurityTaxService(IDbContextFactory<BudgetDbContext> dbFactory) => _dbFactory = dbFactory;

    public async Task<List<SecurityTaxRow>> GetSettingsAsync(int sourceId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var lotIsins = await db.PpLots.AsNoTracking()
            .Where(l => l.SourceId == sourceId && l.Isin != "").Select(l => l.Isin).Distinct().ToListAsync(ct);
        if (lotIsins.Count == 0) return new List<SecurityTaxRow>();

        var names = await db.PpSecurities.AsNoTracking()
            .Where(s => s.SourceId == sourceId && lotIsins.Contains(s.Isin))
            .ToDictionaryAsync(s => s.Isin, s => s.Name, ct);
        var overrides = await db.SecurityTaxSettings.AsNoTracking()
            .Where(s => s.SourceId == sourceId)
            .ToDictionaryAsync(s => s.Isin, s => s.TeilfreistellungBp, ct);

        return lotIsins
            .Select(i => new SecurityTaxRow
            {
                Isin = i,
                Name = names.GetValueOrDefault(i, i),
                TeilfreistellungBp = overrides.GetValueOrDefault(i, DefaultTeilfreistellungBp)
            })
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Teilfreistellung rates (0..1) by ISIN for securities the user has overridden — the
    /// caller falls back to <see cref="DefaultTeilfreistellungBp"/> for the rest.</summary>
    public async Task<Dictionary<string, decimal>> GetRatesAsync(int sourceId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.SecurityTaxSettings.AsNoTracking()
            .Where(s => s.SourceId == sourceId).ToListAsync(ct);
        return rows.ToDictionary(s => s.Isin, s => s.TeilfreistellungBp / 10000m, StringComparer.Ordinal);
    }

    public async Task SetTeilfreistellungAsync(int sourceId, string isin, int bp, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var existing = await db.SecurityTaxSettings
            .FirstOrDefaultAsync(s => s.SourceId == sourceId && s.Isin == isin, ct);
        if (existing is null)
            db.SecurityTaxSettings.Add(new SecurityTaxSetting { SourceId = sourceId, Isin = isin, TeilfreistellungBp = bp });
        else
            existing.TeilfreistellungBp = bp;
        await db.SaveChangesAsync(ct);
    }
}
