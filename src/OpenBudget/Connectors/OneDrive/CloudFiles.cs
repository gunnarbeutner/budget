namespace OpenBudget.Connectors.OneDrive;

/// <summary>A version of a linked cloud file as the provider describes it.</summary>
public sealed record CloudFileMetadata(string Name, string? ETag, string? CTag, DateTime? LastModifiedUtc, long? Size)
{
    /// <summary>The tag that changes exactly when the content changes: the content tag, else the entity tag.</summary>
    public string? ContentTag => CTag ?? ETag;
}

/// <summary>The result of reading a linked file: its metadata, and its bytes unless it was unchanged.</summary>
public sealed record CloudFileContent(CloudFileMetadata Metadata, byte[]? Content)
{
    public bool Changed => Content != null;
}

/// <summary>
/// Server-side read access to a user's linked cloud files (see <see cref="Models.CloudFileLink"/>), independent of
/// whether the user's own computer is on. Every call names the acting user and only reaches that user's links.
/// </summary>
public interface ICloudFileReader
{
    /// <summary>The current metadata of the linked file (no download).</summary>
    Task<CloudFileMetadata> GetMetadataAsync(string userId, int linkId, CancellationToken ct);

    /// <summary>
    /// Downloads the linked file unless its content tag equals <paramref name="knownContentTag"/>; then
    /// <see cref="CloudFileContent.Content"/> is null.
    /// </summary>
    Task<CloudFileContent> ReadAsync(string userId, int linkId, string? knownContentTag, CancellationToken ct);
}

/// <summary>A linked file as handed to a <see cref="ICloudFileConsumer"/>.</summary>
public sealed record CloudFileLinkInfo(int Id, string OwnerUserId, string DisplayPath);

/// <summary>
/// Receives each new version of a linked cloud file found by <see cref="CloudFileSyncCoordinator"/>. All registered
/// consumers see every version, in registration order; a consumer that throws leaves the version unprocessed, so
/// the next check downloads it again. <see cref="PortfolioPerformanceCloudFileConsumer"/> is the one registered by
/// default; a PortfolioPerformance source that reads files through a file-source seam registers its own consumer
/// (or reads through <see cref="ICloudFileReader"/> directly).
/// </summary>
public interface ICloudFileConsumer
{
    Task ConsumeAsync(CloudFileLinkInfo link, CloudFileContent content, CancellationToken ct);
}
