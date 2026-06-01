namespace OpenBudget.Services;

/// <summary>
/// Per-circuit holder for the browser's timezone, so every page renders dates/times — and decides
/// "what is today / this month / this year" — in the user's zone rather than the server's.
///
/// The zone arrives as an IANA id in the <c>tz</c> cookie (set by wwwroot/js/timezone.js before
/// Blazor connects) and is seeded into this scoped service by the root <c>App</c> component, so
/// it's already correct by the time any page's OnInitialized runs. Falls back to UTC when the
/// cookie is missing (first-ever visit, before its one-time reload) or unparseable.
/// </summary>
public sealed class ClockService
{
    /// <summary>The browser's timezone, or UTC until seeded / when the id can't be resolved.</summary>
    public TimeZoneInfo Zone { get; private set; } = TimeZoneInfo.Utc;

    /// <summary>Seed from an IANA id (e.g. "Europe/Berlin"). No-op on null/blank/unknown ids.</summary>
    public void SetZone(string? ianaId)
    {
        if (string.IsNullOrWhiteSpace(ianaId)) return;
        // FindSystemTimeZoneById accepts IANA names on every platform under modern .NET (ICU).
        try { Zone = TimeZoneInfo.FindSystemTimeZoneById(ianaId); }
        catch (TimeZoneNotFoundException) { /* keep fallback */ }
        catch (InvalidTimeZoneException) { /* keep fallback */ }
    }

    /// <summary>Current instant in UTC. Use for durations/ages, which are timezone-independent.</summary>
    public DateTime UtcNow => DateTime.UtcNow;

    /// <summary>Current wall-clock time in the browser's zone.</summary>
    public DateTime NowLocal => TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, Zone);

    /// <summary>Today's date in the browser's zone.</summary>
    public DateTime Today => NowLocal.Date;

    /// <summary>Current (year, month) in the browser's zone.</summary>
    public (int Year, int Month) CurrentYearMonth => (NowLocal.Year, NowLocal.Month);

    /// <summary>
    /// Convert a UTC instant to the browser's zone for display. Forces UTC kind first so it works
    /// for both Kind=Utc values and the Kind=Unspecified values EF/SQLite and Transaction.Date
    /// produce — all of which represent UTC.
    /// </summary>
    public DateTime ToLocal(DateTime utc)
        => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone);
}
