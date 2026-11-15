using Microsoft.Extensions.Localization;
using OpenBudget.Connectors.Sync;

namespace OpenBudget.Shared;

/// <summary>Names a <see cref="SyncProgress"/> for the UI from the <see cref="BankSyncHost"/> resources.</summary>
public static class SyncProgressText
{
    /// <summary>
    /// The stage's name with its counts ("Fetching documents: 3 of 10") or detail ("Refreshing: Giro"). A connector
    /// operation shows the connector's own detail text.
    /// </summary>
    public static string Format(SyncProgress progress, IStringLocalizer<BankSyncHost> l)
    {
        if (progress.Stage == SyncStage.Connector)
            return progress.Detail ?? l["Running_Started"].Value;
        var stage = l["Progress_" + progress.Stage].Value;
        if (progress.Done is { } done)
            return progress.Total is { } total ? l["Progress_CountOf", stage, done, total].Value : l["Progress_Count", stage, done].Value;
        return progress.Detail is { Length: > 0 } detail ? l["Progress_Detail", stage, detail].Value : stage;
    }

    /// <summary>Whether the progress is worth showing next to the sync chip in the header: a stage with counts.</summary>
    public static bool ShowsInChip(SyncProgress? progress) => progress is { Done: not null };
}
