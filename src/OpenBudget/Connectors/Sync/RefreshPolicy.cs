using OpenBudget.Models;

namespace OpenBudget.Connectors.Sync;

/// <summary>Pure scheduling rules: when is a bank login (or an account's statement download) due?</summary>
public static class RefreshPolicy
{
    public static TimeSpan? Period(RefreshInterval interval) => interval switch
    {
        RefreshInterval.EveryTwoHours => TimeSpan.FromHours(2),
        RefreshInterval.FourTimesDaily => TimeSpan.FromHours(6),
        RefreshInterval.Daily => TimeSpan.FromDays(1),
        RefreshInterval.Weekly => TimeSpan.FromDays(7),
        RefreshInterval.ManualOnly or RefreshInterval.Never => null,
        _ => null,
    };

    /// <summary>Whether a page load should start a sync: only for automatic intervals whose last attempt is old enough.</summary>
    public static bool IsDue(RefreshInterval interval, DateTime? lastSyncAtUtc, DateTime nowUtc) =>
        Period(interval) is { } period && (lastSyncAtUtc is not { } last || nowUtc - last >= period);

    /// <summary>"Sync all" includes everything except logins set to Never.</summary>
    public static bool IncludedInSyncAll(RefreshInterval interval) => interval != RefreshInterval.Never;

    /// <summary>
    /// Whether an account's statements are due. Monthly: a try is due at the start of a month and then
    /// daily until a statement for the current month has been downloaded; weekly/daily: that long after
    /// the last attempt; manual: never automatically.
    /// </summary>
    public static bool StatementsDue(BankLoginAccount account, DateTime nowUtc)
    {
        if (!account.StatementsEnabled || account.Closed || account.LinkedAccountId == null) return false;
        var lastAttempt = account.LastStatementsAttemptAt;
        switch (account.StatementsInterval)
        {
            case StatementsInterval.Monthly:
                if (account.LastStatementAt is { } got && got.Year == nowUtc.Year && got.Month == nowUtc.Month) return false;
                return lastAttempt is not { } a
                    || a.Year != nowUtc.Year || a.Month != nowUtc.Month
                    || nowUtc - a >= TimeSpan.FromDays(1);
            case StatementsInterval.Weekly:
                return lastAttempt is not { } w || nowUtc - w >= TimeSpan.FromDays(7);
            case StatementsInterval.Daily:
                return lastAttempt is not { } d || nowUtc - d >= TimeSpan.FromDays(1);
            case StatementsInterval.ManualOnly:
            default:
                return false;
        }
    }
}
