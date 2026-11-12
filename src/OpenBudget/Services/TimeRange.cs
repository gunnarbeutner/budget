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

    /// <summary>
    /// Restrict to uncategorized transactions of a source. The source app represents "no category" by
    /// pinning the transaction to a sentinel category with an empty name rather than leaving
    /// category_key NULL, so both shapes count. Does not exclude split parents — callers do that.
    /// </summary>
    public static IQueryable<Transaction> WhereUncategorized(this IQueryable<Transaction> q, BudgetDbContext db, int sourceId)
    {
        return q.Where(t => t.CategoryId == null
            || !db.Categories.Any(c => c.SourceId == sourceId && c.Id == t.CategoryId && !string.IsNullOrEmpty(c.Name)));
    }

    /// <summary>
    /// Rows that count as income or spending: no split parents (their parts count instead) and no transfers between
    /// the household's own accounts. Balance-type queries use <c>Splitted != 1</c> alone, since transfers do move money.
    /// </summary>
    public static IQueryable<Transaction> CountedAsFlow(this IQueryable<Transaction> q) =>
        q.Where(t => t.Splitted != SplitState.Parent && !t.IsTransfer);
}
