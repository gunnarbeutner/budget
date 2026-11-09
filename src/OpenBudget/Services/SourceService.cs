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
