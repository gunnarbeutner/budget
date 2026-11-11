namespace OpenBudget.Services;

/// <summary>
/// Where the PortfolioPerformance file of a source comes from. The portfolio accounts of a source are recomputed from
/// whatever <see cref="OpenLatestAsync"/> returns, so the computation does not care whether the file was uploaded or
/// fetched from elsewhere.
/// </summary>
public interface IPortfolioFileSource
{
    /// <summary>Opens the newest ".portfolio" file known for the source, or null when there is none.</summary>
    Task<Stream?> OpenLatestAsync(int sourceId, CancellationToken ct = default);
}

/// <summary>
/// The file uploaded through <c>POST /api/portfolioperformance/{source}</c>: each upload replaces the source's copy
/// under "&lt;data&gt;/portfolioperformance/&lt;sourceId&gt;.portfolio", which later recomputations (e.g. after a
/// settings change) read again.
/// </summary>
public sealed class UploadedPortfolioFileSource : IPortfolioFileSource
{
    private readonly string _directory;

    public UploadedPortfolioFileSource(string directory) => _directory = directory;

    private string PathFor(int sourceId) => Path.Combine(_directory, $"{sourceId}.portfolio");

    /// <summary>Keeps <paramref name="uploadedFile"/> as the source's current file.</summary>
    public async Task StoreAsync(int sourceId, string uploadedFile, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_directory);
        var target = PathFor(sourceId);
        var temp = target + ".tmp";
        await using (var from = File.OpenRead(uploadedFile))
        await using (var to = File.Create(temp))
        {
            await from.CopyToAsync(to, ct);
        }
        File.Move(temp, target, overwrite: true);
    }

    public Task<Stream?> OpenLatestAsync(int sourceId, CancellationToken ct = default)
    {
        var path = PathFor(sourceId);
        return Task.FromResult<Stream?>(File.Exists(path) ? File.OpenRead(path) : null);
    }
}
