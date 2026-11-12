using Microsoft.EntityFrameworkCore;
using OpenBudget.Auth;
using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>
/// The user's own sources: create, view/rotate the per-source upload token, delete. Everything here
/// is owner-only; other users never see a source's page, token or data except through a shared project.
/// </summary>
public class SourceService
{
    private readonly IDbContextFactory<BudgetDbContext> _dbFactory;
    private readonly ChangeNotificationService _notifier;
    private readonly ChangeOrigin _origin;

    public SourceService(IDbContextFactory<BudgetDbContext> dbFactory, ChangeNotificationService notifier, ChangeOrigin origin)
    {
        _dbFactory = dbFactory;
        _notifier = notifier;
        _origin = origin;
    }

    /// <summary>
    /// Sources owned by the current user, ordered by name. With <paramref name="ensureTokens"/> a
    /// source that has no upload token yet (claimed before per-source tokens existed) gets one.
    /// </summary>
    public async Task<List<ImportSource>> GetOwnedSourcesAsync(bool ensureTokens = false, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var query = db.ImportSources.AsQueryable();
        if (db.Scope is { } scope)
        {
            var owned = scope.OwnedSourceIds;
            query = query.Where(s => owned.Contains(s.Id));
        }

        var sources = await query.OrderBy(s => s.Name).ToListAsync(ct);
        var missing = ensureTokens ? sources.Where(s => string.IsNullOrEmpty(s.UploadToken)).ToList() : [];
        if (missing.Count > 0)
        {
            // Sources claimed before per-source tokens existed: mint theirs now so the owner can see it.
            foreach (var s in missing) s.UploadToken = TokenGenerator.NewUploadToken();
            await db.SaveChangesAsync(ct);
        }
        return sources;
    }

    public async Task<ImportSource> CreateSourceAsync(string name, CancellationToken ct = default)
    {
        name = name.Trim();
        if (!SourceData.IsValidSlug(name))
            throw new ArgumentException("Use lowercase letters, digits and hyphens; start with a letter or digit.", nameof(name));

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        if (db.Scope is { IsAuthenticated: false })
            throw new AccessDeniedException("Not signed in.");
        // Names are global (they key the upload URL), so a taken name may belong to someone else
        // and is not distinguishable from "exists" here — the unique index decides.
        var source = new ImportSource
        {
            Name = name,
            OwnerUserId = db.Scope?.UserId,
            UploadToken = TokenGenerator.NewUploadToken(),
        };
        db.ImportSources.Add(source);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            throw new InvalidOperationException($"The source name '{name}' is not available.");
        }
        _notifier.Publish(new DataChange(DataArea.Sources, SourceId: source.Id), _origin.Id);
        return source;
    }

    /// <summary>
    /// The current user's source to import into: with <paramref name="preferMoneyMoney"/> the one that already holds
    /// MoneyMoney data, else the oldest one they own; a new source owned by them when they have none. Its name is
    /// internal (never shown), so a new one gets a generated slug.
    /// </summary>
    public async Task<ImportSource> EnsureOwnSourceAsync(bool preferMoneyMoney = false, CancellationToken ct = default)
    {
        var owned = await GetOwnedSourcesAsync(ct: ct);
        var pick = (preferMoneyMoney ? owned.OrderBy(s => s.Id).FirstOrDefault(s => s.LastUploadAt != null) : null)
            ?? owned.OrderBy(s => s.Id).FirstOrDefault();
        if (pick != null) return pick;
        for (var attempt = 1; attempt < 4; attempt++)
        {
            try
            {
                return await CreateSourceAsync(NewSourceName(), ct);
            }
            catch (InvalidOperationException)
            {
                // a generated name that is taken: try another one
            }
        }
        return await CreateSourceAsync(NewSourceName(), ct);
    }

    private static string NewSourceName() => "data-" + Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4));

    /// <summary>
    /// What users read for each of <paramref name="sourceIds"/> instead of its internal name: the project that shows
    /// the source's portfolio (else its first project), else the owner's name. Sources that would share a label get
    /// the owner's name added.
    /// </summary>
    public async Task<Dictionary<int, string>> GetLabelsAsync(IReadOnlyCollection<int> sourceIds, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var ids = sourceIds.Distinct().ToList();
        var projects = await db.Projects.AsNoTracking().Where(p => ids.Contains(p.SourceId))
            .Select(p => new { p.Id, p.Name, p.SourceId }).ToListAsync(ct);
        var projectIds = projects.Select(p => p.Id).ToList();
        var withDepot = (await db.ProjectAccountAssignments.AsNoTracking()
            .Where(a => projectIds.Contains(a.ProjectId))
            .Join(db.Accounts.Where(a => a.Type == 7 || a.Portfolio == 1), pa => pa.AccountId, a => a.Id, (pa, _) => pa.ProjectId)
            .Distinct().ToListAsync(ct)).ToHashSet();
        var owners = await db.ImportSources.AsNoTracking().Where(s => ids.Contains(s.Id))
            .Select(s => new { s.Id, s.OwnerUserId }).ToDictionaryAsync(s => s.Id, s => s.OwnerUserId, ct);
        var ownerIds = owners.Values.OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        var ownerNames = await db.Users.AsNoTracking().Where(u => ownerIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => AppUser.ShownName(u.DisplayName, u.UserName), StringComparer.Ordinal, ct);

        string OwnerName(int id) => owners.GetValueOrDefault(id) is { } o && ownerNames.TryGetValue(o, out var n) ? n : "";
        var labels = new Dictionary<int, string>();
        foreach (var id in ids)
        {
            var project = projects.Where(p => p.SourceId == id)
                .OrderByDescending(p => withDepot.Contains(p.Id)).ThenBy(p => p.Id).FirstOrDefault();
            labels[id] = project?.Name ?? OwnerName(id);
        }
        foreach (var group in labels.GroupBy(l => l.Value, StringComparer.Ordinal).Where(g => g.Count() > 1).ToList())
        {
            foreach (var (id, label) in group.ToList())
            {
                var owner = OwnerName(id);
                labels[id] = owner.Length > 0 && owner != label ? $"{label} · {owner}" : label;
            }
        }
        return labels;
    }

    /// <summary>Replaces the source's upload token; the old one stops working immediately.</summary>
    public async Task<string> RotateTokenAsync(int sourceId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        db.Scope?.RequireSourceOwner(sourceId);
        var source = await db.ImportSources.FirstOrDefaultAsync(s => s.Id == sourceId, ct)
            ?? throw new AccessDeniedException("Source not found.");
        source.UploadToken = TokenGenerator.NewUploadToken();
        await db.SaveChangesAsync(ct);
        _notifier.Publish(new DataChange(DataArea.Sources, SourceId: sourceId), _origin.Id);
        return source.UploadToken;
    }

    /// <summary>Deletes an owned source and all its imported data; refused while projects still use it.</summary>
    public async Task<bool> DeleteSourceAsync(int sourceId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        db.Scope?.RequireSourceOwner(sourceId);
        var source = await db.ImportSources.FirstOrDefaultAsync(s => s.Id == sourceId, ct);
        if (source == null || await db.Projects.AnyAsync(p => p.SourceId == sourceId, ct)) return false;

        // Wipe imported data first (own FK-disabled transaction, see SourceData), then the source row.
        await SourceData.DeleteSourceDataAsync(db, sourceId, ct);
        db.ImportSources.Remove(source);
        await db.SaveChangesAsync(ct);
        _notifier.Publish(new DataChange(DataArea.Sources, SourceId: sourceId), _origin.Id);
        _notifier.Publish(new DataChange(DataArea.ImportedData, SourceId: sourceId), _origin.Id);
        return true;
    }
}
