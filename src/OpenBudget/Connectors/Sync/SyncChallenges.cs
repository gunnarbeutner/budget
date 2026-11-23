using OpenBudget.Connectors;

namespace OpenBudget.Connectors.Sync;

/// <summary>Which open sync step the sync dialog shows while several syncs run at once.</summary>
public static class SyncChallenges
{
    /// <summary>A question, or a web login whose page needs the user (not one running by itself).</summary>
    public static bool WantsUser(BankSyncStatus status) =>
        status.Challenge?.Kind != ChallengeKind.WebLogin || status.Phase == SyncPhase.WaitingForInput;

    /// <summary>
    /// The step shown stays while it is open and needs the user (or its browser is shown on request); otherwise the
    /// first step that needs the user, so a web login running by itself in another sync never takes the dialog from a
    /// question; otherwise the first open step. Null when no sync waits on anything.
    /// </summary>
    public static BankSyncStatus? Pick(IEnumerable<BankSyncStatus> statuses, Guid shownChallengeId, bool showBrowser)
    {
        var pending = statuses.Where(s => s.Challenge != null && s.Phase != SyncPhase.Finished).ToList();
        return pending.FirstOrDefault(s => s.ChallengeId == shownChallengeId && (WantsUser(s) || showBrowser))
            ?? pending.FirstOrDefault(WantsUser)
            ?? pending.FirstOrDefault();
    }
}
