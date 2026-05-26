using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>
/// Paged, filtered transaction list for the per-project browser. Always excludes split parents
/// (Splitted == 1), to match the rollup semantics used everywhere else in the app — a parent's
/// real categories live on its children, so showing it as a row would double-count visually.
/// Split children (Splitted == 2) come through as normal rows and get flagged so the UI can
/// mark them.
///
/// The category filter takes an already-resolved leaf-id set rather than an (item, type) tuple
/// — leaf resolution happens via <see cref="CategoryHierarchyService.BuildItemToLeafCategoryIds"/>
/// at the page layer so this service stays purely a query.
/// </summary>
public class TransactionQueryService
{
    private readonly BudgetDbContext _db;

    public TransactionQueryService(BudgetDbContext db)
    {
        _db = db;
    }

    public async Task<TransactionPage> QueryAsync(TransactionQuery query, CancellationToken ct = default)
    {
        if (query.SourceId <= 0) throw new ArgumentException("SourceId is required.", nameof(query));
        if (query.Page < 0) throw new ArgumentOutOfRangeException(nameof(query), "Page must be >= 0.");
        if (query.PageSize <= 0) throw new ArgumentOutOfRangeException(nameof(query), "PageSize must be > 0.");

        var baseQuery = _db.Transactions
            .Where(t => t.SourceId == query.SourceId)
            .Where(t => t.Splitted != 1);

        if (query.Period is { } period)
        {
            var range = TimeRange.Between(
                new DateTime(period.Year, period.FromMonth, 1, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(period.Year, period.ToMonth, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1));
            baseQuery = baseQuery.WithinUtc(range);
        }

        if (query.CategoryLeafIds is { Count: > 0 } leafIds)
        {
            var leafList = leafIds.ToList();
            baseQuery = baseQuery.Where(t => t.CategoryId.HasValue && leafList.Contains(t.CategoryId.Value));
        }

        if (query.AccountIds is { Count: > 0 } accountIds)
        {
            var accountList = accountIds.ToList();
            baseQuery = baseQuery.Where(t => t.AccountId.HasValue && accountList.Contains(t.AccountId.Value));
        }

        if (!string.IsNullOrWhiteSpace(query.SearchText))
        {
            var needle = query.SearchText.Trim();
            baseQuery = baseQuery.Where(t =>
                (t.Name != null && EF.Functions.Like(t.Name, $"%{needle}%"))
                || (t.Comment != null && EF.Functions.Like(t.Comment, $"%{needle}%"))
                || (t.Purpose != null && EF.Functions.Like(t.Purpose, $"%{needle}%")));
        }

        if (query.MinAbsAmount is { } minAmt)
        {
            var minCents = (long)Math.Round(minAmt * 100m);
            baseQuery = baseQuery.Where(t => t.Amount >= minCents || -t.Amount >= minCents);
        }
        if (query.MaxAbsAmount is { } maxAmt)
        {
            var maxCents = (long)Math.Round(maxAmt * 100m);
            baseQuery = baseQuery.Where(t => (t.Amount >= 0 ? t.Amount : -t.Amount) <= maxCents);
        }

        var totalCount = await baseQuery.CountAsync(ct);
        var totalSumCents = totalCount == 0 ? 0L : await baseQuery.SumAsync(t => t.Amount, ct);

        var rowsRaw = await baseQuery
            .OrderByDescending(t => t.Timestamp)
            .ThenByDescending(t => t.Id)
            .Skip(query.Page * query.PageSize)
            .Take(query.PageSize)
            .Select(t => new
            {
                t.Id,
                t.Timestamp,
                t.Name,
                t.Comment,
                t.Purpose,
                t.CategoryId,
                t.AccountId,
                t.Amount,
                t.Currency,
                t.Splitted
            })
            .ToListAsync(ct);

        // Resolve category + account names in two batched queries rather than per-row .Include.
        var catIds = rowsRaw.Where(r => r.CategoryId.HasValue).Select(r => r.CategoryId!.Value).Distinct().ToList();
        var acctIds = rowsRaw.Where(r => r.AccountId.HasValue).Select(r => r.AccountId!.Value).Distinct().ToList();
        var catNames = catIds.Count == 0
            ? new Dictionary<long, string>()
            : await _db.Categories.Where(c => catIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name ?? "", ct);
        var acctNames = acctIds.Count == 0
            ? new Dictionary<long, string>()
            : await _db.Accounts.Where(a => acctIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Name ?? "", ct);

        var rows = rowsRaw.Select(r => new TransactionRow
        {
            Id = r.Id,
            Date = DateTimeOffset.FromUnixTimeSeconds(r.Timestamp).UtcDateTime,
            Name = r.Name,
            Comment = r.Comment,
            Purpose = r.Purpose,
            CategoryId = r.CategoryId,
            CategoryName = r.CategoryId.HasValue ? catNames.GetValueOrDefault(r.CategoryId.Value) : null,
            AccountId = r.AccountId,
            AccountName = r.AccountId.HasValue ? acctNames.GetValueOrDefault(r.AccountId.Value) : null,
            Amount = r.Amount / 100m,
            Currency = r.Currency,
            IsSplitChild = r.Splitted == 2
        }).ToList();

        return new TransactionPage
        {
            Rows = rows,
            TotalCount = totalCount,
            TotalSum = totalSumCents / 100m
        };
    }

    /// <summary>
    /// Earliest transaction timestamp for a source, as UTC. Used as the chart's left edge so
    /// the per-category history goes back as far as the data exists. Null when the source
    /// has no transactions yet.
    /// </summary>
    public async Task<DateTime?> GetSourceEarliestTransactionAsync(int sourceId, CancellationToken ct = default)
    {
        var minTs = await _db.Transactions
            .Where(t => t.SourceId == sourceId)
            .Where(t => t.Splitted != 1)
            .MinAsync(t => (long?)t.Timestamp, ct);
        return minTs.HasValue ? DateTimeOffset.FromUnixTimeSeconds(minTs.Value).UtcDateTime : null;
    }

    /// <summary>
    /// Bulk (timestamp, amount-in-euros) pairs across an arbitrary UTC range, scoped to the
    /// given source + leaf categories. Used for chart-style queries (trailing-12-months,
    /// trailing-8-quarters etc.) that don't fit into a single calendar-year window. Always
    /// excludes split parents.
    /// </summary>
    public async Task<List<(DateTime Date, decimal Amount)>> GetRawByDateRangeAsync(
        int sourceId,
        IReadOnlyCollection<long> leafIds,
        DateTime startUtc,
        DateTime endExclusiveUtc,
        CancellationToken ct = default)
    {
        if (leafIds.Count == 0) return new List<(DateTime, decimal)>();
        var leafList = leafIds.ToList();
        var range = TimeRange.Between(startUtc, endExclusiveUtc);
        var raw = await _db.Transactions
            .Where(t => t.SourceId == sourceId)
            .Where(t => t.Splitted != 1)
            .Where(t => t.CategoryId.HasValue && leafList.Contains(t.CategoryId.Value))
            .WithinUtc(range)
            .Select(t => new { t.Timestamp, t.Amount })
            .ToListAsync(ct);
        return raw
            .Select(r => (DateTimeOffset.FromUnixTimeSeconds(r.Timestamp).UtcDateTime, r.Amount / 100m))
            .ToList();
    }
}
