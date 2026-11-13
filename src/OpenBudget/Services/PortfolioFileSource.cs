using Microsoft.EntityFrameworkCore;
namespace OpenBudget.Services;

/// <summary>
/// Where the PortfolioPerformance file of a user comes from. The portfolio accounts of a user are recomputed from
/// whatever <see cref="OpenLatestAsync"/> returns, so the computation does not care whether the file was uploaded or
/// fetched from elsewhere.
/// </summary>
public interface IPortfolioFileSource
{
    /// <summary>Opens the newest ".portfolio" file known for the user, or null when there is none.</summary>
    Task<Stream?> OpenLatestAsync(string ownerId, CancellationToken ct = default);
}

/// <summary>
/// The last imported PortfolioPerformance file of a user: each import replaces the user's copy
/// under "&lt;data&gt;/portfolioperformance/&lt;user id&gt;.portfolio", which later recomputations (e.g. after a
/// settings change) read again.
/// </summary>
public sealed class UploadedPortfolioFileSource : IPortfolioFileSource
{
    private readonly string _directory;

    public UploadedPortfolioFileSource(string directory) => _directory = directory;

    private string PathFor(string ownerId) => Path.Combine(_directory, $"{ownerId}.portfolio");

    /// <summary>Keeps <paramref name="uploadedFile"/> as the user's current file.</summary>
    public async Task StoreAsync(string ownerId, string uploadedFile, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_directory);
        var target = PathFor(ownerId);
        var temp = target + ".tmp";
        await using (var from = File.OpenRead(uploadedFile))
        await using (var to = File.Create(temp))
        {
            await from.CopyToAsync(to, ct);
        }
        File.Move(temp, target, overwrite: true);
    }

    /// <summary>
    /// Renames files stored under a former per-data-set number ("&lt;n&gt;.portfolio") to their owner's name, as listed in
    /// <see cref="Models.UserDataState.LegacyFileKeys"/>, and clears that list. Run at startup; does nothing when all moved.
    /// </summary>
    public async Task RelocateLegacyFilesAsync(BudgetDbContext db, CancellationToken ct = default)
    {
        foreach (var state in await db.UserDataStates.IgnoreQueryFilters().Where(s => s.LegacyFileKeys != null).ToListAsync(ct))
        {
            var target = PathFor(state.UserId);
            foreach (var key in state.LegacyFileKeys!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var legacy = PathFor(key);
                if (!File.Exists(legacy)) continue;
                if (File.Exists(target) && File.GetLastWriteTimeUtc(target) >= File.GetLastWriteTimeUtc(legacy)) File.Delete(legacy);
                else File.Move(legacy, target, overwrite: true);
            }
            state.LegacyFileKeys = null;
        }
        await db.SaveChangesAsync(ct);
    }

    public Task<Stream?> OpenLatestAsync(string ownerId, CancellationToken ct = default)
    {
        var path = PathFor(ownerId);
        return Task.FromResult<Stream?>(File.Exists(path) ? File.OpenRead(path) : null);
    }
}
