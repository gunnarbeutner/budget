using Microsoft.EntityFrameworkCore;
using OpenBudget.Services;

namespace OpenBudget.Auth;

/// <summary>Result of assigning legacy (unowned) data to a user.</summary>
public sealed record ClaimResult(IReadOnlyList<string> Sources, int Projects);

/// <summary>
/// Hands pre-existing data to a user: a source whose Name equals the username and that has no owner
/// yet is assigned to them, together with that source's projects that have no owner yet. Anything
/// that already has an owner is never touched, so re-running (or creating a second user) can't steal
/// data. Unowned sources/projects are invisible to everyone until claimed.
/// </summary>
public static class UserClaims
{
    public static async Task<ClaimResult> ClaimAsync(
        IDbContextFactory<BudgetDbContext> system, string userId, string userName, CancellationToken ct = default)
    {
        await using var db = await system.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var sources = await db.ImportSources
            .Where(s => s.OwnerUserId == null && s.Name == userName)
            .ToListAsync(ct);
        foreach (var s in sources)
        {
            s.OwnerUserId = userId;
            s.UploadToken ??= TokenGenerator.NewUploadToken();
        }

        var sourceIds = sources.Select(s => s.Id).ToList();
        var projects = sourceIds.Count == 0
            ? []
            : await db.Projects.Where(p => p.OwnerUserId == null && sourceIds.Contains(p.SourceId)).ToListAsync(ct);
        foreach (var p in projects) p.OwnerUserId = userId;

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return new ClaimResult(sources.Select(s => s.Name).ToList(), projects.Count);
    }
}
