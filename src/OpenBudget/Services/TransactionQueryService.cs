using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>
/// Paged, filtered transaction list for the per-project browser. Always excludes split parents
/// (Splitted == 1), to match the rollup semantics used everywhere else in the app — a parent's
/// real categories live on its children, so showing it as a row would double-count visually.
/// Split children (Splitted == 2) come through as normal rows and get flagged so the UI can
/// mark them. Parents whose children don't cover their amount were rewritten at import time into
/// uncategorized remainder rows (Splitted == 3) and therefore show up (and count) like normal rows.
///
/// The category filter takes an already-resolved leaf-id set rather than an (item, type) tuple
/// — leaf resolution happens via <see cref="CategoryHierarchyService.BuildItemToLeafCategoryIds"/>
/// at the page layer so this service stays purely a query.
/// </summary>
public class TransactionQueryService
{
    private readonly IDbContextFactory<BudgetDbContext> _dbFactory;

    public TransactionQueryService(IDbContextFactory<BudgetDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<TransactionPage> QueryAsync(TransactionQuery query, CancellationToken ct = default)
    {
        if (query.SourceId <= 0) throw new ArgumentException("SourceId is required.", nameof(query));
        if (query.Skip < 0) throw new ArgumentOutOfRangeException(nameof(query), "Skip must be >= 0.");
        if (query.Take <= 0) throw new ArgumentOutOfRangeException(nameof(query), "Take must be > 0.");

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var baseQuery = BuildFilteredQuery(db, query);

        var totalCount = await baseQuery.CountAsync(ct);
        var totalSumCents = totalCount == 0 ? 0L : await baseQuery.SumAsync(t => t.Amount, ct);

        var rowsRaw = await baseQuery
            .OrderByDescending(t => t.Timestamp)
            .ThenByDescending(t => t.Id)
            .Skip(query.Skip)
            .Take(query.Take)
            .Select(t => new
            {
                t.Id,
                t.SourceId,
                t.Timestamp,
                t.Name,
                t.Comment,
                t.Purpose,
                t.CategoryId,
                t.AccountId,
                t.Amount,
                t.Currency,
                t.Splitted,
                t.Booked,
                t.Note,
                t.Checked,
                t.IsNew,
                t.CategorySetBy,
                t.RuleId,
                t.ValueTimestamp,
                t.CounterpartyIban,
                t.CounterpartyBic,
                t.BookingText,
                t.BankReference
            })
            .ToListAsync(ct);

        // Resolve category + account names in two batched queries rather than per-row .Include.
        var catIds = rowsRaw.Where(r => r.CategoryId.HasValue).Select(r => r.CategoryId!.Value).Distinct().ToList();
        var acctIds = rowsRaw.Where(r => r.AccountId.HasValue).Select(r => r.AccountId!.Value).Distinct().ToList();
        var cats = catIds.Count == 0
            ? new Dictionary<long, (string Name, string? Color)>()
            : (await db.Categories.Where(c => catIds.Contains(c.Id)).Select(c => new { c.Id, c.Name, c.Color }).ToListAsync(ct))
                .ToDictionary(c => c.Id, c => (Name: c.Name ?? "", c.Color));
        var accts = acctIds.Count == 0
            ? new Dictionary<long, (string Name, string Provider)>()
            : (await db.Accounts.Where(a => acctIds.Contains(a.Id)).Select(a => new { a.Id, a.Name, a.Provider }).ToListAsync(ct))
                .ToDictionary(a => a.Id, a => (Name: a.Name ?? "", a.Provider));

        var rows = rowsRaw.Select(r => new TransactionRow
        {
            Id = r.Id,
            Date = DateTimeOffset.FromUnixTimeSeconds(r.Timestamp).UtcDateTime,
            Name = r.Name,
            Comment = r.Comment,
            Purpose = r.Purpose,
            CategoryId = r.CategoryId,
            CategoryName = r.CategoryId.HasValue && cats.TryGetValue(r.CategoryId.Value, out var cat) ? cat.Name : null,
            CategoryColor = r.CategoryId.HasValue && cats.TryGetValue(r.CategoryId.Value, out var catColor) ? catColor.Color : null,
            AccountId = r.AccountId,
            AccountName = r.AccountId.HasValue && accts.TryGetValue(r.AccountId.Value, out var acct) ? acct.Name : null,
            AccountProvider = r.AccountId.HasValue && accts.TryGetValue(r.AccountId.Value, out var prov) ? prov.Provider : null,
            EditBlock = TransactionEditRules.Check(db.Scope, r.SourceId, r.AccountId,
                r.AccountId.HasValue && accts.TryGetValue(r.AccountId.Value, out var p2) ? p2.Provider : null),
            SourceId = r.SourceId,
            Note = r.Note,
            IsChecked = r.Checked,
            IsNew = r.IsNew,
            CategorySetBy = r.CategorySetBy,
            RuleId = r.RuleId,
            ValueDate = r.ValueTimestamp is { } vt ? DateTimeOffset.FromUnixTimeSeconds(vt).UtcDateTime : null,
            CounterpartyIban = r.CounterpartyIban,
            CounterpartyBic = r.CounterpartyBic,
            BookingText = r.BookingText,
            BankReference = r.BankReference,
            Amount = r.Amount / 100m,
            Currency = r.Currency,
            IsSplitChild = r.Splitted == SplitState.Child,
            IsSplitRemainder = r.Splitted == SplitState.Remainder,
            IsPending = !r.Booked
        }).ToList();

        return new TransactionPage
        {
            Rows = rows,
            TotalCount = totalCount,
            TotalSum = totalSumCents / 100m
        };
    }

    /// <summary>
    /// Count and signed-sum totals for the filter set, without fetching any rows. The infinite-
    /// scroll list calls Virtualize once per visible window, so footer totals (count + sum)
    /// live on a separate call that refreshes when filters change.
    /// </summary>
    public async Task<(int Count, decimal Sum)> QueryTotalsAsync(TransactionQuery query, CancellationToken ct = default)
    {
        if (query.SourceId <= 0) throw new ArgumentException("SourceId is required.", nameof(query));
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var q = BuildFilteredQuery(db, query);
        var count = await q.CountAsync(ct);
        var sumCents = count == 0 ? 0L : await q.SumAsync(t => t.Amount, ct);
        return (count, sumCents / 100m);
    }

    /// <summary>
    /// Count of transactions strictly newer than <paramref name="endExclusiveUtc"/> for the
    /// given filter set. Browse uses this to jump the tx list to the position of the latest
    /// transaction at-or-before a chart bar's period.
    /// </summary>
    public async Task<int> CountNewerThanAsync(TransactionQuery query, DateTime endExclusiveUtc, CancellationToken ct = default)
    {
        if (query.SourceId <= 0) throw new ArgumentException("SourceId is required.", nameof(query));
        var unixSeconds = new DateTimeOffset(DateTime.SpecifyKind(endExclusiveUtc, DateTimeKind.Utc)).ToUnixTimeSeconds();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await BuildFilteredQuery(db, query)
            .Where(t => t.Timestamp >= unixSeconds)
            .CountAsync(ct);
    }

    internal static IQueryable<Transaction> BuildFilteredQuery(BudgetDbContext db, TransactionQuery query)
    {
        var baseQuery = db.Transactions
            .Where(t => t.SourceId == query.SourceId)
            .Where(t => t.Splitted != 1);

        if (query.Period is { } period)
        {
            var range = TimeRange.Between(
                new DateTime(period.Year, period.FromMonth, 1, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(period.Year, period.ToMonth, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1));
            baseQuery = baseQuery.WithinUtc(range);
        }

        if (query.UncategorizedOnly)
        {
            baseQuery = baseQuery.WhereUncategorized(db, query.SourceId);
        }
        else if (query.CategoryLeafIds is { Count: > 0 } leafIds)
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
                || (t.Note != null && EF.Functions.Like(t.Note, $"%{needle}%"))
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

        if (query.Checked is { } wantChecked) baseQuery = baseQuery.Where(t => t.Checked == wantChecked);
        if (query.NewOnly) baseQuery = baseQuery.Where(t => t.IsNew);
        if (query.Direction == AmountDirection.Incoming) baseQuery = baseQuery.Where(t => t.Amount > 0);
        else if (query.Direction == AmountDirection.Outgoing) baseQuery = baseQuery.Where(t => t.Amount < 0);
        if (query.FromUtc is { } from)
        {
            var fromTs = new DateTimeOffset(DateTime.SpecifyKind(from, DateTimeKind.Utc)).ToUnixTimeSeconds();
            baseQuery = baseQuery.Where(t => t.Timestamp >= fromTs);
        }
        if (query.ToUtcExclusive is { } to)
        {
            var toTs = new DateTimeOffset(DateTime.SpecifyKind(to, DateTimeKind.Utc)).ToUnixTimeSeconds();
            baseQuery = baseQuery.Where(t => t.Timestamp < toTs);
        }
        if (!string.IsNullOrWhiteSpace(query.PayeeEquals))
        {
            var payee = query.PayeeEquals.Trim();
            baseQuery = baseQuery.Where(t => t.Name != null && EF.Functions.Collate(t.Name, "NOCASE") == payee);
        }

        return baseQuery;
    }

    /// <summary>
    /// The ids matching a filter in list order (newest first), for select-all and shift-click ranges. Capped so a select-all
    /// on a huge filter stays bounded.
    /// </summary>
    public async Task<List<long>> QueryIdsAsync(TransactionQuery query, int skip = 0, int take = 50_000, CancellationToken ct = default)
    {
        if (query.SourceId <= 0) throw new ArgumentException("SourceId is required.", nameof(query));
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await BuildFilteredQuery(db, query)
            .OrderByDescending(t => t.Timestamp)
            .ThenByDescending(t => t.Id)
            .Skip(skip)
            .Take(take)
            .Select(t => t.Id)
            .ToListAsync(ct);
    }

    /// <summary>The subset of <paramref name="ids"/> that still matches the filter (to prune a selection after an edit).</summary>
    public async Task<HashSet<long>> FilterIdsAsync(TransactionQuery query, IReadOnlyCollection<long> ids, CancellationToken ct = default)
    {
        var result = new HashSet<long>();
        if (ids.Count == 0) return result;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        foreach (var chunk in ids.Chunk(500))
        {
            var part = chunk;
            result.UnionWith(await BuildFilteredQuery(db, query).Where(t => part.Contains(t.Id)).Select(t => t.Id).ToListAsync(ct));
        }
        return result;
    }

    /// <summary>Sum (cents) of the given transactions the user can see, for the action bar's "N selected, sum".</summary>
    public async Task<(int Count, decimal Sum)> GetSelectionTotalsAsync(IReadOnlyCollection<long> ids, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var count = 0;
        var sum = 0L;
        foreach (var chunk in ids.Chunk(500))
        {
            var part = chunk;
            var rows = await db.Transactions.Where(t => part.Contains(t.Id)).Select(t => t.Amount).ToListAsync(ct);
            count += rows.Count;
            sum += rows.Sum();
        }
        return (count, sum / 100m);
    }

    /// <summary>Booked flows (UTC time, amount in euros) of an account, newest first, for the running balance.</summary>
    public async Task<List<(DateTime Date, decimal Amount)>> GetBookedFlowsAsync(long accountId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var raw = await db.Transactions
            .Where(t => t.AccountId == accountId && t.Booked && t.Splitted != 1)
            .OrderByDescending(t => t.Timestamp)
            .Select(t => new { t.Timestamp, t.Amount })
            .ToListAsync(ct);
        return raw.Select(r => (DateTimeOffset.FromUnixTimeSeconds(r.Timestamp).UtcDateTime, r.Amount / 100m)).ToList();
    }

    /// <summary>
    /// The statement document (PDF) a bank published for an account that covers a booking: the first one dated on or after
    /// the booking within 62 days. Null when there is none (or the user may not see it).
    /// </summary>
    public async Task<(long Id, string Name)?> FindStatementAsync(long accountId, DateTime bookingUtc, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var from = DateTime.SpecifyKind(bookingUtc.Date, DateTimeKind.Utc);
        var to = from.AddDays(62);
        var s = await db.BankStatements.AsNoTracking()
            .Where(x => x.AccountId == accountId && x.Date >= from && x.Date <= to)
            .OrderBy(x => x.Date)
            .Select(x => new { x.Id, x.Name })
            .FirstOrDefaultAsync(ct);
        return s == null ? null : (s.Id, s.Name);
    }

    /// <summary>
    /// Earliest and latest transaction timestamps within a scope (source + optional leaf
    /// categories and/or account ids), as UTC. The browse chart uses this so its x-axis
    /// spans only the selected node's actual history — a category that started in 2026 won't
    /// drag the chart back to 2020 just because the source has older rows in other
    /// categories. Either value may be null when the scope has no transactions.
    /// </summary>
    public async Task<(DateTime? Earliest, DateTime? Latest)> GetScopeDateRangeAsync(
        int sourceId,
        IReadOnlyCollection<long>? leafIds,
        IReadOnlyCollection<long>? accountIds,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var q = db.Transactions
            .Where(t => t.SourceId == sourceId)
            .Where(t => t.Splitted != 1);
        if (leafIds is { Count: > 0 })
        {
            var leafList = leafIds.ToList();
            q = q.Where(t => t.CategoryId.HasValue && leafList.Contains(t.CategoryId.Value));
        }
        if (accountIds is { Count: > 0 })
        {
            var acctList = accountIds.ToList();
            q = q.Where(t => t.AccountId.HasValue && acctList.Contains(t.AccountId.Value));
        }
        var bounds = await q
            .GroupBy(_ => 1)
            .Select(g => new { Min = g.Min(t => (long?)t.Timestamp), Max = g.Max(t => (long?)t.Timestamp) })
            .FirstOrDefaultAsync(ct);
        DateTime? earliest = bounds?.Min is { } mn ? DateTimeOffset.FromUnixTimeSeconds(mn).UtcDateTime : null;
        DateTime? latest = bounds?.Max is { } mx ? DateTimeOffset.FromUnixTimeSeconds(mx).UtcDateTime : null;
        return (earliest, latest);
    }

    /// <summary>
    /// The earliest and latest transaction months as (year, month) tuples, for bounding the month
    /// navigator (<see cref="Anchoring"/> / PeriodNav). Pass a <paramref name="sourceId"/> to scope
    /// to one source (Browse); pass null for the whole DB (the dashboard, which spans every
    /// source). Both values are null when no transactions match — the caller then falls back to the
    /// current month. The max may be in the future (e.g. the source's pending, future-dated rows),
    /// which is intentional: it lets navigation reach those months. Split parents are excluded.
    /// </summary>
    public async Task<((int Year, int Month)? Min, (int Year, int Month)? Max)> GetMonthBoundsAsync(
        int? sourceId,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var q = db.Transactions.Where(t => t.Splitted != 1);
        if (sourceId is { } sid) q = q.Where(t => t.SourceId == sid);
        var bounds = await q
            .GroupBy(_ => 1)
            .Select(g => new { Min = g.Min(t => (long?)t.Timestamp), Max = g.Max(t => (long?)t.Timestamp) })
            .FirstOrDefaultAsync(ct);
        return (ToMonth(bounds?.Min), ToMonth(bounds?.Max));

        static (int Year, int Month)? ToMonth(long? unixSeconds)
        {
            if (unixSeconds is not { } s) return null;
            var d = DateTimeOffset.FromUnixTimeSeconds(s).UtcDateTime;
            return (d.Year, d.Month);
        }
    }

    /// <summary>
    /// Bulk (timestamp, amount-in-euros) pairs across an arbitrary UTC range, scoped to the
    /// given source and either leaf-category set, account set, or both (AND when both are
    /// supplied). Used for chart-style queries (trailing history, account net-flow series)
    /// that don't fit into a single calendar-year window. Always excludes split parents.
    /// </summary>
    public async Task<List<(DateTime Date, decimal Amount)>> GetRawByDateRangeAsync(
        int sourceId,
        IReadOnlyCollection<long>? leafIds,
        IReadOnlyCollection<long>? accountIds,
        DateTime startUtc,
        DateTime endExclusiveUtc,
        CancellationToken ct = default)
    {
        bool hasLeaves = leafIds is { Count: > 0 };
        bool hasAccounts = accountIds is { Count: > 0 };
        if (!hasLeaves && !hasAccounts) return new List<(DateTime, decimal)>();

        var range = TimeRange.Between(startUtc, endExclusiveUtc);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var q = db.Transactions
            .Where(t => t.SourceId == sourceId)
            .Where(t => t.Splitted != 1)
            .WithinUtc(range);

        if (hasLeaves)
        {
            var leafList = leafIds!.ToList();
            q = q.Where(t => t.CategoryId.HasValue && leafList.Contains(t.CategoryId.Value));
        }
        if (hasAccounts)
        {
            var acctList = accountIds!.ToList();
            q = q.Where(t => t.AccountId.HasValue && acctList.Contains(t.AccountId.Value));
        }

        var raw = await q
            .Select(t => new { t.Timestamp, t.Amount })
            .ToListAsync(ct);
        return raw
            .Select(r => (DateTimeOffset.FromUnixTimeSeconds(r.Timestamp).UtcDateTime, r.Amount / 100m))
            .ToList();
    }
}
