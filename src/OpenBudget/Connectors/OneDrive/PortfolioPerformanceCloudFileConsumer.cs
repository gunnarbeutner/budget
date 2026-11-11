using Microsoft.EntityFrameworkCore;
using OpenBudget.Services;

namespace OpenBudget.Connectors.OneDrive;

/// <summary>Where linked files are staged before import (the upload staging directory).</summary>
public sealed record CloudFileStaging(string Directory);

/// <summary>
/// Imports each new version of a linked ".portfolio" file into its target source through the same path as
/// POST /api/portfolioperformance/{source} (<see cref="PortfolioPerformanceUpload"/>), so taxonomies, lots and
/// portfolios update. Files without a target source are skipped. The target must still belong to the link's owner.
/// </summary>
public sealed class PortfolioPerformanceCloudFileConsumer : ICloudFileConsumer
{
    private readonly SystemDbContextFactory _system;
    private readonly PortfolioPerformanceImportService _importer;
    private readonly CloudFileStaging _staging;

    public PortfolioPerformanceCloudFileConsumer(SystemDbContextFactory system, PortfolioPerformanceImportService importer, CloudFileStaging staging)
    {
        _system = system;
        _importer = importer;
        _staging = staging;
    }

    public async Task ConsumeAsync(CloudFileLinkInfo link, CloudFileContent content, CancellationToken ct)
    {
        if (link.TargetSourceName is not { } sourceName || content.Content is not { } bytes) return;
        await using (var db = await _system.CreateDbContextAsync(ct))
        {
            if (!await db.ImportSources.AnyAsync(s => s.Name == sourceName && s.OwnerUserId == link.OwnerUserId, ct))
                throw new KeyNotFoundException($"The source '{sourceName}' no longer exists or is not yours.");
        }
        using var stream = new MemoryStream(bytes, writable: false);
        await PortfolioPerformanceUpload.ImportAsync(_importer, sourceName, stream, _staging.Directory, ct);
    }
}
