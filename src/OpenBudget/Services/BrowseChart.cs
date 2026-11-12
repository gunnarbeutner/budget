namespace OpenBudget.Services;

/// <summary>
/// Bars + their (year, month) mapping, each bar's first day, and whether the y-axis should be
/// inverted, for the Browse page's history chart.
/// </summary>
public sealed record BrowseChartData(
    (string Label, decimal Value, bool IsAnchor)[] Bars,
    (int Year, int Month)[] BarMonths,
    bool InvertY,
    DateTime[] BarStarts);

/// <summary>
/// A depot's stored value history for the balance chart: ascending (day, value) points from
/// <c>portfolio_chart</c> and the depot's current value.
/// </summary>
public sealed record DepotValueSeries(
    IReadOnlyList<(DateTime Day, decimal Value)> Points,
    decimal CurrentValue);

/// <summary>
/// Pure bucketing/window math for the Browse history chart. Lifted out of Browse.razor so it can
/// be unit-tested without a circuit; the page keeps only the DB queries that feed it.
/// </summary>
public static class BrowseChart
{
    /// <summary>
    /// A category's or group's 12 monthly budget amounts from one budget's stored entries, signed like
    /// the chart's bars: income budgets count positive, expense budgets negative (per
    /// <paramref name="isIncome"/>). A group nets its children (budgeted income − budgeted spending);
    /// a group whose subtree carries no amount falls back to its own entry. Null when nothing
    /// non-zero is budgeted.
    /// </summary>
    public static decimal[]? BudgetMonthsFor(
        long id, CategoryItemType itemType,
        IReadOnlyDictionary<(long Id, CategoryItemType ItemType), decimal[]> stored,
        List<CategoryGroupWithChildCount> hierarchy,
        Func<(long Id, CategoryItemType ItemType), bool> isIncome)
    {
        var children = hierarchy
            .Where(h => h.ParentCategoryGroupId.HasValue)
            .GroupBy(h => h.ParentCategoryGroupId!.Value)
            .ToDictionary(g => g.Key, g => g.Select(h => (h.Id, h.ItemType)).Distinct().ToList());

        decimal[]? Signed((long Id, CategoryItemType ItemType) key, HashSet<(long, CategoryItemType)> seen)
        {
            if (!seen.Add(key)) return null;
            if (key.ItemType == CategoryItemType.Group && children.TryGetValue(key.Id, out var kids))
            {
                var sum = new decimal[12];
                var any = false;
                foreach (var kid in kids)
                {
                    if (Signed(kid, seen) is not { } km) continue;
                    for (int m = 0; m < 12; m++) sum[m] += km[m];
                    any = true;
                }
                if (any) return sum;
            }
            if (!stored.TryGetValue(key, out var own) || own.All(x => x == 0m)) return null;
            var sign = isIncome(key) ? 1m : -1m;
            return own.Select(x => sign * x).ToArray();
        }

        var months = Signed((id, itemType), new HashSet<(long, CategoryItemType)>());
        return months != null && months.Any(x => x != 0m) ? months : null;
    }

    /// <summary>
    /// Per-bar budget amounts for the history chart: a monthly bar takes that month's budget,
    /// a quarter or year bar the sum of its months. Bars in a year without a budget are null.
    /// Daily bars get no series. The monthly amounts arrive signed like the bars (see
    /// <see cref="BudgetMonthsFor"/>). Null when no bar has a budget.
    /// </summary>
    public static decimal?[]? BuildBudgetSeries(
        (int Year, int Month)[] barMonths, string granularity,
        IReadOnlyDictionary<int, decimal[]> monthsByYear)
    {
        int span = granularity switch { "M" => 1, "Q" => 3, "Y" => 12, _ => 0 };
        if (span == 0 || barMonths.Length == 0) return null;
        var series = new decimal?[barMonths.Length];
        var any = false;
        for (int i = 0; i < barMonths.Length; i++)
        {
            var (y, m) = barMonths[i];
            if (!monthsByYear.TryGetValue(y, out var months)) continue;
            int first = span == 12 ? 0 : m - 1;
            decimal total = 0m;
            for (int k = first; k < first + span && k < 12; k++) total += months[k];
            series[i] = total;
            any = true;
        }
        return any ? series : null;
    }

    /// <summary>
    /// Resolve the chart's bucket window for a granularity ("Y"/"Q"/"D", default "M"): the start,
    /// the exclusive end, the bucket count, a date→bucket-index map, and an index→label map.
    /// A daily window ends with <paramref name="lastDay"/> when that falls inside the right-edge month,
    /// so no empty days follow it.
    /// </summary>
    public static (DateTime Start, DateTime EndExclusive, int Count, Func<DateTime, int> BucketOf, Func<int, string> LabelOf)
        ResolveChartWindow(DateTime earliestTx, DateTime anchorMonthStart, string granularity, DateTime? lastDay = null)
    {
        switch (granularity)
        {
            case "Y":
            {
                int startYear = earliestTx.Year;
                int endYear = anchorMonthStart.Year;
                int count = Math.Max(1, endYear - startYear + 1);
                var start = new DateTime(startYear, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                var end = new DateTime(endYear + 1, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                return (start, end, count,
                    d => d.Year - startYear,
                    i => (startYear + i).ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            case "Q":
            {
                int totalStartQ = earliestTx.Year * 4 + (earliestTx.Month - 1) / 3;
                int totalAnchorQ = anchorMonthStart.Year * 4 + (anchorMonthStart.Month - 1) / 3;
                int count = Math.Max(1, totalAnchorQ - totalStartQ + 1);
                int startYear = totalStartQ / 4;
                int startQ = totalStartQ - startYear * 4;
                var start = new DateTime(startYear, startQ * 3 + 1, 1, 0, 0, 0, DateTimeKind.Utc);
                int endYear = totalAnchorQ / 4;
                int endQ = totalAnchorQ - endYear * 4;
                var end = new DateTime(endYear, endQ * 3 + 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(3);
                return (start, end, count,
                    d =>
                    {
                        int q = (d.Month - 1) / 3;
                        int totalQ = d.Year * 4 + q;
                        return totalQ - totalStartQ;
                    },
                    i =>
                    {
                        int totalQ = totalStartQ + i;
                        int y = totalQ / 4;
                        int q = totalQ - y * 4;
                        return $"Q{q + 1}/{(y % 100):D2}";
                    }
                );
            }
            case "D":
            {
                var start = earliestTx.Date;
                var end = anchorMonthStart.AddMonths(1);
                if (lastDay is { } ld && ld.Date >= start && ld.Date.AddDays(1) < end)
                    end = DateTime.SpecifyKind(ld.Date.AddDays(1), DateTimeKind.Utc);
                int count = Math.Max(1, (int)(end - start).TotalDays);
                return (DateTime.SpecifyKind(start, DateTimeKind.Utc), end, count,
                    d => (int)(d.Date - start).TotalDays,
                    i => start.AddDays(i).ToString("dd.MM", System.Globalization.CultureInfo.InvariantCulture));
            }
            default: // "M"
            {
                int startTotal = earliestTx.Year * 12 + (earliestTx.Month - 1);
                int anchorTotal = anchorMonthStart.Year * 12 + (anchorMonthStart.Month - 1);
                int count = Math.Max(1, anchorTotal - startTotal + 1);
                int startYear = startTotal / 12;
                int startMonth = startTotal - startYear * 12 + 1;
                var start = new DateTime(startYear, startMonth, 1, 0, 0, 0, DateTimeKind.Utc);
                var end = anchorMonthStart.AddMonths(1);
                var monthNames = new[] { "Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec" };
                return (start, end, count,
                    d =>
                    {
                        int total = d.Year * 12 + (d.Month - 1);
                        return total - startTotal;
                    },
                    i =>
                    {
                        int total = startTotal + i;
                        int y = total / 12;
                        int m = total - y * 12;
                        return $"{monthNames[m]} '{(y % 100):D2}";
                    }
                );
            }
        }
    }

    /// <summary>
    /// Build the chart series from already-fetched raw (date, amount) rows. Sums signed amounts
    /// per bucket (the source app convention: outflows negative), flags buckets inside the anchor
    /// period [<paramref name="fromMonth"/>, <paramref name="toMonth"/>] of
    /// <paramref name="anchorYear"/>, and — for the account daily view — replaces flows with a
    /// back-walked running closing balance from <paramref name="currentBalanceForDaily"/>.
    /// <paramref name="depots"/> add each depot's stored value at the end of every balance bucket
    /// (see <see cref="DepotValueAt"/>); <paramref name="raw"/> and
    /// <paramref name="currentBalanceForDaily"/> then cover only the booking-based accounts.
    /// <paramref name="highlight"/> (a day span, end exclusive) flags every bucket overlapping it
    /// instead of the anchor months; <paramref name="lastDay"/> ends a daily window (see
    /// <see cref="ResolveChartWindow"/>).
    /// </summary>
    public static BrowseChartData BuildChartSeries(
        DateTime earliest, DateTime rightEdge, string granularity,
        int anchorYear, int fromMonth, int toMonth,
        bool requestInvertY, bool isAccountDaily,
        IReadOnlyList<(DateTime Date, decimal Amount)> raw,
        decimal? currentBalanceForDaily,
        IReadOnlyList<DepotValueSeries>? depots = null,
        DateTime? today = null,
        (DateTime From, DateTime ToExclusive)? highlight = null,
        DateTime? lastDay = null)
    {
        var (windowStart, windowEndExclusive, bucketCount, bucketOf, labelOf) =
            ResolveChartWindow(earliest, rightEdge, granularity, lastDay);

        var bars = new (string Label, decimal Value, bool IsAnchor)[bucketCount];
        var months = new (int Year, int Month)[bucketCount];
        for (int i = 0; i < bucketCount; i++)
        {
            bars[i] = (labelOf(i), 0m, false);
        }
        // Reconstruct bucket month-starts: walk forward bucket-by-bucket.
        var bucketEnds = new DateTime[bucketCount];
        var starts = new DateTime[bucketCount];
        var cursor = windowStart;
        for (int i = 0; i < bucketCount; i++)
        {
            starts[i] = cursor;
            months[i] = (cursor.Year, cursor.Month);
            cursor = granularity switch
            {
                "Y" => cursor.AddYears(1),
                "Q" => cursor.AddMonths(3),
                "D" => cursor.AddDays(1),
                _ => cursor.AddMonths(1)
            };
            bucketEnds[i] = cursor;
        }
        if (highlight is { } hl)
        {
            for (int i = 0; i < bucketCount; i++)
                if (starts[i] < hl.ToExclusive && bucketEnds[i] > hl.From)
                    bars[i] = (bars[i].Label, bars[i].Value, true);
        }
        else for (int m = fromMonth; m <= toMonth; m++)
        {
            if (granularity == "D")
            {
                var monthStart = new DateTime(anchorYear, m, 1, 0, 0, 0, DateTimeKind.Utc);
                var monthEnd = monthStart.AddMonths(1);
                for (var d = monthStart; d < monthEnd; d = d.AddDays(1))
                {
                    var bucket = bucketOf(d);
                    if (bucket >= 0 && bucket < bucketCount)
                        bars[bucket] = (bars[bucket].Label, bars[bucket].Value, true);
                }
            }
            else
            {
                var bucket = bucketOf(new DateTime(anchorYear, m, 1));
                if (bucket >= 0 && bucket < bucketCount)
                {
                    bars[bucket] = (bars[bucket].Label, bars[bucket].Value, true);
                }
            }
        }
        decimal sumAfterWindow = 0m;
        foreach (var (date, amount) in raw)
        {
            var b = bucketOf(date);
            if (b < 0 || b >= bucketCount)
            {
                // Out-of-window transactions only matter for the account-D back-walk below;
                // ignore them otherwise. Anything before windowStart wouldn't be returned by
                // the query, so only the "past the right edge" case can hit this branch.
                if (isAccountDaily && date >= windowEndExclusive) sumAfterWindow += amount;
                continue;
            }
            // Sum raw signed amounts — the source's convention is negative for outflows and
            // positive for inflows. Refunds against an expense bring the bar closer to zero.
            bars[b] = (bars[b].Label, bars[b].Value + amount, bars[b].IsAnchor);
        }
        // Account daily view shows the running closing balance per day (the natural view for
        // a checking account), not net flow — daily flow is too sparse to be useful. Back-walk
        // from the snapshot's current balance: balance[end] = current − Σ(txns after window),
        // balance[i] = balance[i+1] − flow[i+1].
        if (isAccountDaily && currentBalanceForDaily is { } currentBalance)
        {
            var dayFlows = new decimal[bucketCount];
            for (int i = 0; i < bucketCount; i++) dayFlows[i] = bars[i].Value;
            var closing = currentBalance - sumAfterWindow;
            bars[bucketCount - 1] = (bars[bucketCount - 1].Label, closing, bars[bucketCount - 1].IsAnchor);
            for (int i = bucketCount - 2; i >= 0; i--)
            {
                closing -= dayFlows[i + 1];
                bars[i] = (bars[i].Label, closing, bars[i].IsAnchor);
            }
        }
        if (isAccountDaily && depots is { Count: > 0 })
        {
            var todayDate = (today ?? DateTime.UtcNow).Date;
            for (int i = 0; i < bucketCount; i++)
            {
                decimal sum = 0m;
                foreach (var depot in depots) sum += DepotValueAt(depot, bucketEnds[i], todayDate);
                bars[i] = (bars[i].Label, bars[i].Value + sum, bars[i].IsAnchor);
            }
        }
        // Y-axis inversion is only honest when every bar is non-positive: with invert on,
        // positive values would grow downward. The moment one bucket has a positive value
        // (a refund larger than the period's outflows, or income mixed into an "expense"
        // group), drop the inversion so positive reads up and negative reads down again.
        var invertY = requestInvertY;
        if (invertY && bars.Any(b => b.Value > 0)) invertY = false;

        return new BrowseChartData(bars, months, invertY, starts);
    }

    /// <summary>
    /// A depot's value at the end of the bucket ending (exclusively) at <paramref name="bucketEndExclusive"/>:
    /// the current value once the bucket reaches today, otherwise the last stored point before the
    /// bucket's end (carried forward between points), and 0 before the first point.
    /// </summary>
    public static decimal DepotValueAt(DepotValueSeries depot, DateTime bucketEndExclusive, DateTime today)
    {
        if (bucketEndExclusive > today.Date) return depot.CurrentValue;
        decimal value = 0m;
        foreach (var (day, v) in depot.Points)
        {
            if (day >= bucketEndExclusive) break;
            value = v;
        }
        return value;
    }
}
