namespace OpenBudget.Services;

/// <summary>
/// ID-namespacing helpers for multi-source MoneyMoney data. Every imported MoneyMoney row's
/// primary key is rewritten with <see cref="Namespace"/> so multiple installations can share
/// a single SQLite store without rowid collisions.
///
/// Source 1 is the legacy/default source and uses identity mapping — that means existing
/// budgets.db references (ProjectCategoryAssignment.CategoryId, MonthlyBudgetEntry.CategoryId,
/// etc.) stay valid without any backfill rewrite. New sources get IDs ≥ 2 and their rowids
/// are shifted into the high 24 bits.
/// </summary>
public static class SourceScope
{
    public const int LegacySourceId = 1;
    public const string LegacySourceName = "default";

    /// <summary>
    /// Compute the namespaced primary key for a (sourceId, rowid) pair.
    /// Layout: 24 high bits = sourceId, 40 low bits = rowid. SourceId 1 is identity.
    /// </summary>
    public static long Namespace(int sourceId, long rowid)
    {
        if (sourceId <= 0) throw new ArgumentOutOfRangeException(nameof(sourceId), "Source IDs are positive.");
        if (rowid < 0) throw new ArgumentOutOfRangeException(nameof(rowid), "Rowids are non-negative.");
        if (rowid >= (1L << 40)) throw new ArgumentOutOfRangeException(nameof(rowid), "Rowid exceeds 40-bit limit.");
        return sourceId == LegacySourceId ? rowid : ((long)sourceId << 40) | rowid;
    }

    /// <summary>
    /// Sentinel namespaced IDs (rowids 0 and 1) used by MoneyMoney for "no category" / hidden
    /// root group. These need to be hidden per source — the legacy bare-literal checks against
    /// 0/1 would let one source's sentinel leak into another's.
    /// </summary>
    public static IReadOnlyCollection<long> SentinelIds(int sourceId) => new[]
    {
        Namespace(sourceId, 0),
        Namespace(sourceId, 1)
    };

    public static long SentinelRoot(int sourceId) => Namespace(sourceId, 1);
    public static long SentinelZero(int sourceId) => Namespace(sourceId, 0);
}
