namespace OpenBudget.Connectors.Sync;

/// <summary>The step a running sync is in; the UI names it from its resources.</summary>
public enum SyncStage
{
    LoggingIn,
    ListingAccounts,
    Refreshing,
    /// <summary>A connector-side operation; <see cref="SyncProgress.Detail"/> carries the connector's own text.</summary>
    Connector,
    MatchingOrders,
    MatchingReceipts,
    FetchingStatements,
    FetchingDocuments,
}

/// <summary>
/// What a running sync does now: the stage, optional counts (<paramref name="Done"/> of <paramref name="Total"/>) and an
/// optional detail text (an account name, a connector's progress line).
/// </summary>
public sealed record SyncProgress(SyncStage Stage, int? Done = null, int? Total = null, string? Detail = null);
