using System.Runtime.InteropServices;
using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>
/// Half-open Unix-epoch second interval [<see cref="StartTs"/>, <see cref="EndTsExclusive"/>).
/// All comparisons against <see cref="Transaction.Timestamp"/> use this convention so a
/// boundary timestamp belongs to exactly one range.
/// </summary>
[StructLayout(LayoutKind.Auto)]
public readonly record struct TimeRange(long StartTs, long EndTsExclusive)
{
    public static TimeRange Month(int year, int month)
    {
        var start = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
        return new TimeRange(
            ((DateTimeOffset)start).ToUnixTimeSeconds(),
            ((DateTimeOffset)start.AddMonths(1)).ToUnixTimeSeconds());
    }

    public static TimeRange Year(int year)
    {
        var start = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return new TimeRange(
            ((DateTimeOffset)start).ToUnixTimeSeconds(),
            ((DateTimeOffset)start.AddYears(1)).ToUnixTimeSeconds());
    }

    /// <summary>UTC interval from <paramref name="startUtc"/> (inclusive) to <paramref name="endExclusiveUtc"/> (exclusive).</summary>
    public static TimeRange Between(DateTime startUtc, DateTime endExclusiveUtc)
    {
        return new TimeRange(
            ((DateTimeOffset)startUtc).ToUnixTimeSeconds(),
            ((DateTimeOffset)endExclusiveUtc).ToUnixTimeSeconds());
    }

    /// <summary>Already-DateTimeOffset variant — useful when callers have UTC offsets in hand.</summary>
    public static TimeRange Between(DateTimeOffset startUtc, DateTimeOffset endExclusiveUtc)
    {
        return new TimeRange(startUtc.ToUnixTimeSeconds(), endExclusiveUtc.ToUnixTimeSeconds());
    }
}

public static class TransactionQueryExtensions
{
    /// <summary>
    /// Restrict a transaction query to a half-open [start, end) Unix-epoch window. Locals are
    /// used inside the predicate so EF Core lifts them to SQL parameters cleanly.
    /// </summary>
    public static IQueryable<Transaction> WithinUtc(this IQueryable<Transaction> q, TimeRange range)
    {
        var start = range.StartTs;
        var end = range.EndTsExclusive;
        return q.Where(t => t.Timestamp >= start && t.Timestamp < end);
    }
}
