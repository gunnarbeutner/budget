using OpenBudget.Services;

namespace OpenBudget.Connectors.OneDrive;

/// <summary>Where linked files are staged before import (the upload staging directory).</summary>
public sealed record CloudFileStaging(string Directory);

/// <summary>
/// Imports each new version of a linked ".portfolio" file as data of the link's owner through the same path as the
/// browser upload (<see cref="PortfolioPerformanceUpload"/>), so taxonomies, lots and portfolios update.
/// </summary>
public sealed class PortfolioPerformanceCloudFileConsumer : ICloudFileConsumer
{
    private readonly PortfolioPerformanceImportService _importer;
    private readonly CloudFileStaging _staging;

    public PortfolioPerformanceCloudFileConsumer(PortfolioPerformanceImportService importer, CloudFileStaging staging)
    {
        _importer = importer;
        _staging = staging;
    }

    public async Task ConsumeAsync(CloudFileLinkInfo link, CloudFileContent content, CancellationToken ct)
    {
        if (content.Content is not { } bytes) return;
        using var stream = new MemoryStream(bytes, writable: false);
        await PortfolioPerformanceUpload.ImportAsync(_importer, link.OwnerUserId, stream, _staging.Directory, ct);
    }
}
