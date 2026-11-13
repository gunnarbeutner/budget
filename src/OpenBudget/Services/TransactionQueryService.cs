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
        if (string.IsNullOrEmpty(query.OwnerUserId)) throw new ArgumentException("OwnerUserId is required.", nameof(query));
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
                t.OwnerUserId,
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
                t.BankReference,
                t.EndToEndReference,
                t.MandateReference,
                t.CreditorId,
                t.TransactionCode,
                t.Primanota,
                t.ReturnReason,
                t.SplitParentId
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
            EditBlock = TransactionEditRules.Check(db.Scope, r.OwnerUserId, r.AccountId,
                r.AccountId.HasValue && accts.TryGetValue(r.AccountId.Value, out var p2) ? p2.Provider : null),
            CanEditBooking = r.AccountId.HasValue && accts.TryGetValue(r.AccountId.Value, out var p3) && p3.Provider == AccountProviders.Manual
                && (db.Scope == null || db.Scope.Owns(r.OwnerUserId)),
            OwnerUserId = r.OwnerUserId,
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
            EndToEndReference = r.EndToEndReference,
            MandateReference = r.MandateReference,
            CreditorId = r.CreditorId,
            TransactionCode = r.TransactionCode,
            Primanota = r.Primanota,
            ReturnReason = r.ReturnReason,
            Amount = r.Amount / 100m,
            Currency = r.Currency,
            IsSplitChild = r.Splitted == SplitState.Child,
            SplitParentId = r.Splitted == SplitState.Child ? r.SplitParentId : null,
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
    public async Task<TransactionTotals> QueryTotalsAsync(TransactionQuery query, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(query.OwnerUserId)) throw new ArgumentException("OwnerUserId is required.", nameof(query));
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var perCurrency = await BuildFilteredQuery(db, query)
            .GroupBy(t => t.Currency)
            .Select(g => new { Currency = g.Key, Count = g.Count(), Cents = g.Sum(t => t.Amount) })
            .ToListAsync(ct);
        return new TransactionTotals(perCurrency.Sum(p => p.Count),
            CurrencySums.Group(perCurrency.Select(p => (p.Currency, p.Cents / 100m))));
    }

    /// <summary>
    /// Count of transactions strictly newer than <paramref name="endExclusiveUtc"/> for the
    /// given filter set. Browse uses this to jump the tx list to the position of the latest
    /// transaction at-or-before a chart bar's period.
    /// </summary>
    public async Task<int> CountNewerThanAsync(TransactionQuery query, DateTime endExclusiveUtc, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(query.OwnerUserId)) throw new ArgumentException("OwnerUserId is required.", nameof(query));
        var unixSeconds = new DateTimeOffset(DateTime.SpecifyKind(endExclusiveUtc, DateTimeKind.Utc)).ToUnixTimeSeconds();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await BuildFilteredQuery(db, query)
            .Where(t => t.Timestamp >= unixSeconds)
            .CountAsync(ct);
    }

    internal static IQueryable<Transaction> BuildFilteredQuery(BudgetDbContext db, TransactionQuery query)
    {
        var baseQuery = db.Transactions
            .Where(t => t.OwnerUserId == query.OwnerUserId)
            .Where(t => t.Splitted != 1);

        if (query.OnlyIds is { } onlyIds)
        {
            var idList = onlyIds.ToList();
            baseQuery = baseQuery.Where(t => idList.Contains(t.Id));
        }

        if (query.Period is { } period)
        {
            var range = TimeRange.Between(
                new DateTime(period.Year, period.FromMonth, 1, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(period.Year, period.ToMonth, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1));
            baseQuery = baseQuery.WithinUtc(range);
        }

        if (query.UncategorizedOnly)
        {
            baseQuery = baseQuery.WhereUncategorized(db, query.OwnerUserId);
        }
        else if (query.IncludeUncategorized && query.CategoryLeafIds is { } trackedLeaves)
        {
            var ownerId = query.OwnerUserId;
            var leafList = trackedLeaves.ToList();
            var uncatAccounts = query.UncategorizedAccountIds is { Count: > 0 } ua ? ua.ToList() : null;
            baseQuery = baseQuery.Where(t =>
                (t.CategoryId.HasValue && leafList.Contains(t.CategoryId.Value))
                || ((t.CategoryId == null
                        || !db.Categories.Any(c => c.OwnerUserId == ownerId && c.Id == t.CategoryId && !string.IsNullOrEmpty(c.Name)))
                    && (uncatAccounts == null || (t.AccountId.HasValue && uncatAccounts.Contains(t.AccountId.Value)))));
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
            // Text matches name, comment, note or purpose; a text that reads as an amount (AmountSearch) also matches
            // bookings of exactly that amount, in either direction unless the text carries a sign.
            var needle = query.SearchText.Trim();
            var amount = AmountSearch.TryParse(needle);
            var hasAmount = amount != null;
            var cents = amount?.Cents ?? 0;
            var negCents = -cents;
            var sign = amount?.Sign ?? 0;
            baseQuery = baseQuery.Where(t =>
                (t.Name != null && EF.Functions.Like(t.Name, $"%{needle}%"))
                || (t.Comment != null && EF.Functions.Like(t.Comment, $"%{needle}%"))
                || (t.Note != null && EF.Functions.Like(t.Note, $"%{needle}%"))
                || (t.Purpose != null && EF.Functions.Like(t.Purpose, $"%{needle}%"))
                || (hasAmount && ((sign >= 0 && t.Amount == cents) || (sign <= 0 && t.Amount == negCents))));
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
        if (string.IsNullOrEmpty(query.OwnerUserId)) throw new ArgumentException("OwnerUserId is required.", nameof(query));
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

    /// <summary>Count and per-currency sums of the given transactions, for the action bar's "N selected, sum".</summary>
    public async Task<TransactionTotals> GetSelectionTotalsAsync(IReadOnlyCollection<long> ids, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = new List<(string? Currency, decimal Amount)>();
        foreach (var chunk in ids.Chunk(500))
        {
            var part = chunk;
            var found = await db.Transactions.Where(t => part.Contains(t.Id)).Select(t => new { t.Currency, t.Amount }).ToListAsync(ct);
            rows.AddRange(found.Select(r => (r.Currency, r.Amount / 100m)));
        }
        return new TransactionTotals(rows.Count, CurrencySums.Group(rows));
    }

    /// <summary>Booked flows (UTC time, amount) of an account in the account's own currency, newest first, for the running
    /// balance; bookings in other currencies do not move that balance.</summary>
    public async Task<List<(DateTime Date, decimal Amount)>> GetBookedFlowsAsync(long accountId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var accountCurrency = CurrencySums.Normalize(await db.Accounts.Where(a => a.Id == accountId).Select(a => a.Currency).FirstOrDefaultAsync(ct));
        var raw = await db.Transactions
            .Where(t => t.AccountId == accountId && t.Booked && t.Splitted != 1)
            .OrderByDescending(t => t.Timestamp)
            .Select(t => new { t.Timestamp, t.Amount, t.Currency })
            .ToListAsync(ct);
        return raw.Where(r => CurrencySums.Normalize(r.Currency) == accountCurrency)
            .Select(r => (DateTimeOffset.FromUnixTimeSeconds(r.Timestamp).UtcDateTime, r.Amount / 100m)).ToList();
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
    /// drag the chart back to 2020 just because the owner has older rows in other
    /// categories. Either value may be null when the scope has no transactions.
    /// </summary>
    public async Task<(DateTime? Earliest, DateTime? Latest)> GetScopeDateRangeAsync(
        string ownerId,
        IReadOnlyCollection<long>? leafIds,
        IReadOnlyCollection<long>? accountIds,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var q = db.Transactions
            .Where(t => t.OwnerUserId == ownerId)
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
    /// navigator (<see cref="Anchoring"/> / PeriodNav). Pass a <paramref name="ownerId"/> to scope
    /// to one owner (Browse); pass null for the whole DB (the dashboard, which spans every
    /// source). Both values are null when no transactions match — the caller then falls back to the
    /// current month. The max may be in the future (e.g. the source's pending, future-dated rows),
    /// which is intentional: it lets navigation reach those months. Split parents are excluded.
    /// </summary>
    public async Task<((int Year, int Month)? Min, (int Year, int Month)? Max)> GetMonthBoundsAsync(
        string? ownerId,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var q = db.Transactions.Where(t => t.Splitted != 1);
        if (ownerId is { } sid) q = q.Where(t => t.OwnerUserId == sid);
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
    /// given owner and either leaf-category set, account set, or both (AND when both are
    /// supplied). Used for chart-style queries (trailing history, account net-flow series)
    /// that don't fit into a single calendar-year window. Always excludes split parents.
    /// </summary>
    public async Task<List<(DateTime Date, decimal Amount)>> GetRawByDateRangeAsync(
        string ownerId,
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
            .Where(t => t.OwnerUserId == ownerId)
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

    /// <summary>
    /// Stored value history (<c>portfolio_chart</c>) of the given accounts, per account as ascending
    /// (day, value) points. A day's value is the account-total row (no security) when the source
    /// stored one, otherwise the sum of that day's per-security rows. Accounts without any point
    /// are absent from the result. One grouped query across all accounts.
    /// </summary>
    public async Task<Dictionary<long, List<(DateTime Day, decimal Value)>>> GetDepotValueHistoryAsync(
        string ownerId,
        IReadOnlyCollection<long> accountIds,
        CancellationToken ct = default)
    {
        var result = new Dictionary<long, List<(DateTime Day, decimal Value)>>();
        if (accountIds.Count == 0) return result;
        var acctList = accountIds.ToList();
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rows = await db.PortfolioChartPoints
            .Where(p => p.OwnerUserId == ownerId && acctList.Contains(p.AccountId))
            .GroupBy(p => new { p.AccountId, p.Timestamp })
            .Select(g => new
            {
                g.Key.AccountId,
                g.Key.Timestamp,
                Totals = g.Count(p => p.SecurityId == null),
                Total = g.Sum(p => p.SecurityId == null ? p.Balance ?? 0 : 0),
                Securities = g.Sum(p => p.SecurityId != null ? p.Balance ?? 0 : 0),
            })
            .ToListAsync(ct);
        foreach (var r in rows.OrderBy(r => r.Timestamp))
        {
            if (!result.TryGetValue(r.AccountId, out var list)) result[r.AccountId] = list = new();
            var day = DateTimeOffset.FromUnixTimeSeconds(r.Timestamp).UtcDateTime.Date;
            list.Add((day, Math.Round((decimal)(r.Totals > 0 ? r.Total : r.Securities), 2)));
        }
        return result;
    }
}
