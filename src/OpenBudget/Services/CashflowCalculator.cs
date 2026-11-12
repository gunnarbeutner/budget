using OpenBudget.Connectors;
using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>
/// One tracked category/group row in the cashflow view: its 12 monthly amounts (actuals for past
/// months, projection for future), a pristine base snapshot the recompute pipeline resets to, and
/// the origin project + aggregate-row flag used to avoid double-counting nested tracked items.
/// </summary>
public class CategoryCashflow
{
    public long CategoryId { get; set; }
    public CategoryItemType ItemType { get; set; }
    public string CategoryName { get; set; } = "";
    public decimal[] MonthlyAmounts { get; set; } = new decimal[12];
    /// <summary>
    /// Pristine snapshot taken at load time: actuals (past months) + self-corrected expense
    /// projection (future months for expenses) + zeros (future months for income, since
    /// ApplyIncomeProjection writes those). The recompute pipeline resets MonthlyAmounts to
    /// this each time before re-projecting and re-applying what-ifs, so each pass is a pure
    /// function of (base, projection settings, what-ifs) — no mutation-tracking required.
    /// </summary>
    public decimal[] BaseMonthlyAmounts { get; set; } = new decimal[12];

    /// <summary>
    /// The project this row's actuals and projected amounts came from. For a project with
    /// children, the cashflow view loads rows from the parent and each child; this lets
    /// the per-category table show which sub-project each line belongs to.
    /// </summary>
    public int OriginProjectId { get; set; }
    public string OriginProjectName { get; set; } = "";

    /// <summary>
    /// True iff this row is a leaf of its slice's tracking tree (i.e. no other tracked
    /// item in the same project is a descendant of it). Aggregates (totals) sum only over
    /// these rows so a tracked group + tracked sub-cat don't double-count.
    /// </summary>
    public bool IsAggregateRow { get; set; }
}

/// <summary>One execution of a standing order or scheduled transfer in the projection (amount signed, in currency units).</summary>
public sealed record ScheduledOccurrence(DateOnly Date, decimal Amount, string Label, ScheduledPaymentKind Kind);

/// <summary>
/// The bank's orders in the cashflow projection (<see cref="CashflowProjectionService.GetScheduledCashflowAsync"/>):
/// their executions, and per cashflow row the signed amounts per month (index 0 = January) that the row's category
/// projection already contains.
/// </summary>
public sealed class ScheduledCashflow
{
    public List<ScheduledOccurrence> Occurrences { get; } = [];

    public Dictionary<(long Id, CategoryItemType ItemType), decimal[]> CoveredByRow { get; } = [];
}

/// <summary>Per-month low / high / closing balance for the cashflow balance line.</summary>
public record struct MonthWatermark(int Month, decimal Low, decimal High, decimal End);

/// <summary>
/// A session-local "what if" cashflow adjustment: a signed amount on a date, optionally
/// attributed to a category ("type:id"). Unattributed entries move only the balance line.
/// </summary>
public class WhatIfEntry
{
    public DateTime Date { get; set; } = DateTime.UtcNow.Date;
    public decimal Amount { get; set; }
    public string Note { get; set; } = "";
    // Encoded as "type:id" (e.g. "1:42" = ItemType.Category, Id 42). Empty when the
    // adjustment is unattributed — only the balance line moves, no category gets credited.
    public string CategoryKey { get; set; } = "";

    public (long Id, CategoryItemType Type)? ParseCategory()
    {
        if (string.IsNullOrEmpty(CategoryKey)) return null;
        var parts = CategoryKey.Split(':');
        if (parts.Length != 2) return null;
        if (!int.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out var t)) return null;
        if (!long.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var id)) return null;
        return (id, (CategoryItemType)t);
    }
}

/// <summary>
/// Outputs of <see cref="CashflowCalculator.ComputeBalance"/>: the daily balance line plus the
/// summary points the Cashflow card reads (per-month watermarks, year/future minima, the current
/// balance, and whether/when the line crosses below the floor).
/// </summary>
public sealed record BalanceProjectionResult(
    bool HasAccounts,
    decimal StartingBalance,
    int LastActualDayOfYear,
    decimal[] BalanceDaily,
    MonthWatermark[] Watermarks,
    decimal YearMinBalance,
    int YearMinDayOfYear,
    decimal FutureMinBalance,
    int FutureMinDayOfYear,
    int FirstFutureNegativeDayOfYear,
    decimal CurrentBalanceForYear,
    bool CurrentlyNegative);

/// <summary>
/// Pure cashflow balance math extracted from Cashflow.razor: turns a project's daily-delta result
/// plus the projected monthly amounts, day-of-month histograms, and what-ifs into a cumulative
/// daily balance line and its summary points. No component state, no clock reads — "today" is
/// passed in as <c>currentYear</c> so the projection is deterministic and unit-testable.
/// </summary>
public static class CashflowCalculator
{
    /// <summary>
    /// Adds <paramref name="amount"/> × <paramref name="weights"/> across the days of a month
    /// into the day-of-year <paramref name="deltas"/> array. Any weight mass on day-of-month
    /// indices past <paramref name="daysInMonth"/> is folded into the last day so a "31st"
    /// historical pattern still lands on Feb 28/29.
    /// </summary>
    public static void DistributeAcrossMonth(
        decimal[] deltas,
        int startDoy,
        int daysInMonth,
        decimal amount,
        decimal[] weights,
        int sign)
    {
        for (int d = 1; d <= daysInMonth; d++)
        {
            decimal w = weights[d - 1];
            if (d == daysInMonth)
            {
                for (int extraIdx = daysInMonth; extraIdx < weights.Length; extraIdx++)
                    w += weights[extraIdx];
            }
            int idx = startDoy - 1 + (d - 1);
            if (idx >= 0 && idx < deltas.Length)
                deltas[idx] += sign * amount * w;
        }
    }

    /// <summary>The signed amount of standing orders in <paramref name="row"/>'s projection for <paramref name="month"/> (1-12).</summary>
    private static decimal Covered(ScheduledCashflow? scheduled, CategoryCashflow row, int month) =>
        scheduled != null && scheduled.CoveredByRow.TryGetValue((row.CategoryId, row.ItemType), out var months) ? months[month - 1] : 0m;

    /// <summary>
    /// Reshape a <see cref="ProjectDailyDeltaResult"/> into the daily balance line + summary
    /// points. Layers histogram-spread projected amounts (for months ≥ <paramref name="firstProjectedMonth"/>)
    /// and session what-ifs onto the daily deltas, cumulates, and derives the watermarks, year/future
    /// minima, current balance, and floor-crossing day. <paramref name="currentYear"/> locates
    /// "today" relative to <paramref name="selectedYear"/>.
    /// </summary>
    public static BalanceProjectionResult ComputeBalance(
        ProjectDailyDeltaResult data,
        int selectedYear,
        int currentYear,
        int firstProjectedMonth,
        IReadOnlyList<CategoryCashflow> incomeCategories,
        IReadOnlyList<CategoryCashflow> expenseCategories,
        IReadOnlyDictionary<(long, CategoryItemType), DayOfMonthDistribution>? histograms,
        IReadOnlyList<WhatIfEntry> whatIfs,
        decimal floor,
        ScheduledCashflow? scheduled = null)
    {
        var hasAssignedAccounts = data.HasAccounts;
        var startingBalance = data.StartingBalance;
        var lastActualDayOfYear = data.LastActualDayOfYear;
        if (!hasAssignedAccounts)
            return new BalanceProjectionResult(false, startingBalance, lastActualDayOfYear,
                Array.Empty<decimal>(), Array.Empty<MonthWatermark>(), 0m, 0, 0m, 0, 0, 0m, false);

        var deltas = (decimal[])data.DailyDelta.Clone();

        // Layer projection: per category, multiply the projected monthly amount by a learned
        // day-of-month histogram so projected months show real-looking patterns (payday lumps,
        // mid-month groceries, end-of-month rent). firstProjectedMonth is "next month" in the
        // current year, so the current month's remaining days get neither real txns nor
        // projection — the line goes flat for those days. Accepting that gap for now.
        if (firstProjectedMonth <= 12 && histograms != null)
        {
            var weights = histograms;

            for (int m = firstProjectedMonth; m <= 12; m++)
            {
                int daysInMonth = DateTime.DaysInMonth(selectedYear, m);
                if (daysInMonth == 0) continue;
                int startDoy = new DateTime(selectedYear, m, 1).DayOfYear; // 1-indexed

                // Filter to aggregate rows so a tracked parent group + its tracked descendants
                // don't both distribute the same money. The displayed total uses the same
                // filter — keeping them in lockstep means the balance line matches the total.
                // The bank's standing orders are booked on their own days below, so the part of a row's
                // projection they cover is taken out here (incoming orders from income, outgoing from expenses).
                foreach (var income in incomeCategories)
                {
                    if (!income.IsAggregateRow) continue;
                    var amount = Math.Max(0m, income.MonthlyAmounts[m - 1] - Math.Max(0m, Covered(scheduled, income, m)));
                    if (amount == 0m) continue;
                    if (!weights.TryGetValue((income.CategoryId, income.ItemType), out var dist)) continue;
                    DistributeAcrossMonth(deltas, startDoy, daysInMonth, amount, dist.Weights, sign: +1);
                }

                foreach (var expense in expenseCategories)
                {
                    if (!expense.IsAggregateRow) continue;
                    var amount = Math.Max(0m, expense.MonthlyAmounts[m - 1] - Math.Max(0m, -Covered(scheduled, expense, m)));
                    if (amount == 0m) continue;
                    if (!weights.TryGetValue((expense.CategoryId, expense.ItemType), out var dist)) continue;
                    DistributeAcrossMonth(deltas, startDoy, daysInMonth, amount, dist.Weights, sign: -1);
                }
            }
        }

        // Layer session-local what-ifs into the balance line. Each entry contributes once:
        //   - Uncategorized: raw delta on the chosen day.
        //   - Categorized + future month (>= firstProjectedMonth): the histogram spread above
        //     already carried the amount into deltas via MonthlyAmounts, so skip here.
        //   - Categorized + current or past month: the histogram only runs for projected
        //     months, so MonthlyAmounts updates the bar chart but not the balance — add the
        //     raw delta now to make the balance line reflect the injected flow.
        foreach (var w in whatIfs)
        {
            if (w.Date.Year != selectedYear) continue;
            if (w.Amount == 0m) continue;
            int doy = w.Date.DayOfYear;
            int idx = doy - 1;
            if (idx < 0 || idx >= deltas.Length) continue;

            var key = w.ParseCategory();
            if (key != null && w.Date.Month >= firstProjectedMonth) continue;
            deltas[idx] += w.Amount;
        }

        // The bank's standing orders and scheduled transfers on their execution days (only after the last day with
        // real bookings, so an execution already booked is not counted twice).
        if (scheduled != null)
        {
            foreach (var o in scheduled.Occurrences)
            {
                if (o.Date.Year != selectedYear) continue;
                int idx = o.Date.DayOfYear - 1;
                if (idx < lastActualDayOfYear || idx >= deltas.Length) continue;
                deltas[idx] += o.Amount;
            }
        }

        // Cumulate into the daily balance line.
        var balance = new decimal[deltas.Length];
        decimal running = startingBalance;
        for (int i = 0; i < deltas.Length; i++)
        {
            running += deltas[i];
            balance[i] = running;
        }

        // Per-month low/high/end.
        var wm = new List<MonthWatermark>(12);
        for (int m = 1; m <= 12; m++)
        {
            int start = new DateTime(selectedYear, m, 1).DayOfYear - 1;
            int endExclusive = m == 12
                ? balance.Length
                : new DateTime(selectedYear, m + 1, 1).DayOfYear - 1;
            if (endExclusive <= start) continue;
            decimal low = decimal.MaxValue, high = decimal.MinValue;
            for (int i = start; i < endExclusive; i++)
            {
                if (balance[i] < low) low = balance[i];
                if (balance[i] > high) high = balance[i];
            }
            wm.Add(new MonthWatermark(m, low, high, balance[endExclusive - 1]));
        }
        var monthlyWatermarks = wm.ToArray();

        // Year-min summary used in the card header.
        decimal min = balance[0];
        int minIdx = 0;
        for (int i = 1; i < balance.Length; i++)
        {
            if (balance[i] < min) { min = balance[i]; minIdx = i; }
        }
        var yearMinBalance = min;
        var yearMinDayOfYear = minIdx + 1;

        // Locate "today" within the balance array so we can talk about future risk separately
        // from historical dips. Past years collapse "today" to year-end; future years sit before
        // the array entirely.
        int currentDayIdx;
        if (selectedYear < currentYear) currentDayIdx = balance.Length - 1;
        else if (selectedYear > currentYear) currentDayIdx = -1;
        else currentDayIdx = lastActualDayOfYear - 1;

        var currentBalanceForYear = currentDayIdx >= 0 ? balance[currentDayIdx] : 0m;
        var currentlyNegative = currentDayIdx >= 0 && balance[currentDayIdx] < floor;

        // Min over today..end-of-year, used by the FutureNegative / CurrentlyNegative alerts.
        // Including today (not currentDayIdx+1) so "Projected low" can quote today's balance
        // when we're currently negative and don't dip further. Past years collapse currentDayIdx
        // to balance.Length-1, leaving futureStart at the last day — a degenerate one-element
        // window that won't fire either alert anyway.
        var futureMinBalance = 0m;
        var futureMinDayOfYear = 0;
        int futureStart = Math.Max(0, currentDayIdx);
        if (futureStart < balance.Length)
        {
            decimal fMin = balance[futureStart];
            int fIdx = futureStart;
            for (int i = futureStart + 1; i < balance.Length; i++)
            {
                if (balance[i] < fMin) { fMin = balance[i]; fIdx = i; }
            }
            futureMinBalance = fMin;
            futureMinDayOfYear = fIdx + 1;
        }

        // First FUTURE crossing below the floor — the actionable date. Only meaningful when
        // the line is currently above the floor; if we're already underwater, there's no
        // "before D" to act on and the banner falls through to a different message.
        var firstFutureNegativeDayOfYear = 0;
        if (!currentlyNegative)
        {
            int searchFrom = Math.Max(0, currentDayIdx + 1);
            for (int i = searchFrom; i < balance.Length; i++)
            {
                if (balance[i] < floor) { firstFutureNegativeDayOfYear = i + 1; break; }
            }
        }

        return new BalanceProjectionResult(
            hasAssignedAccounts, startingBalance, lastActualDayOfYear,
            balance, monthlyWatermarks, yearMinBalance, yearMinDayOfYear,
            futureMinBalance, futureMinDayOfYear, firstFutureNegativeDayOfYear,
            currentBalanceForYear, currentlyNegative);
    }

    /// <summary>
    /// "Pace to break even" income: the income each split source still has to bring in so the
    /// balance line (the project's In-cashflow accounts) ends the selected year at the balance it
    /// started it with. The need is measured on the line itself: the line is computed with no
    /// projected income beyond the standing orders already in each row, and the gap between its
    /// year-end value and the starting balance is the income still required. That gap is split by
    /// <paramref name="splits"/> (percent per income source name); each source's income what-ifs
    /// in the year count toward its own share only. Shares never go below zero.
    /// Returns per source name the remaining amount and the flat monthly amount over the
    /// projected months, plus the monthly amounts to write into each income row
    /// (<see cref="BreakevenIncome.RowMonthly"/>, indexed like <paramref name="incomeCategories"/>).
    /// </summary>
    public static BreakevenIncome ComputeBreakevenIncome(
        ProjectDailyDeltaResult data,
        int selectedYear,
        int currentYear,
        int firstProjectedMonth,
        IReadOnlyList<CategoryCashflow> incomeCategories,
        IReadOnlyList<CategoryCashflow> expenseCategories,
        IReadOnlyDictionary<(long, CategoryItemType), DayOfMonthDistribution>? histograms,
        IReadOnlyList<WhatIfEntry> whatIfs,
        IReadOnlyDictionary<string, decimal> splits,
        ScheduledCashflow? scheduled = null)
    {
        int remainingMonths = Math.Max(0, 13 - firstProjectedMonth);

        // Income rows carry only their actuals and, in projected months, the standing orders
        // already booked on their execution days.
        var baseIncome = incomeCategories.Select(c =>
        {
            var row = CopyRow(c);
            for (int m = firstProjectedMonth; m <= 12; m++)
                row.MonthlyAmounts[m - 1] = Math.Max(0m, Covered(scheduled, c, m));
            return row;
        }).ToList();
        var expenses = expenseCategories.Select(CopyRow).ToList();

        // What-ifs: income what-ifs of a split source are that source's prepayments and stay out
        // of the baseline; every other what-if is part of it (expense what-ifs on their rows,
        // the rest as the line's raw deltas).
        var whatIfBySource = new Dictionary<string, decimal>(StringComparer.Ordinal);
        var baselineWhatIfs = new List<WhatIfEntry>();
        foreach (var w in whatIfs)
        {
            var key = w.ParseCategory();
            if (key != null && w.Amount != 0m && w.Date.Year == selectedYear)
            {
                var incomeRow = incomeCategories.FirstOrDefault(c =>
                    c.CategoryId == key.Value.Id && c.ItemType == key.Value.Type && c.IsAggregateRow);
                if (incomeRow != null && splits.GetValueOrDefault(incomeRow.CategoryName, 0m) > 0m)
                {
                    whatIfBySource[incomeRow.CategoryName] = whatIfBySource.GetValueOrDefault(incomeRow.CategoryName) + w.Amount;
                    continue;
                }
                int idx = w.Date.Month - 1;
                var target = baseIncome.FirstOrDefault(c => c.CategoryId == key.Value.Id && c.ItemType == key.Value.Type && c.IsAggregateRow);
                var exp = expenses.FirstOrDefault(c => c.CategoryId == key.Value.Id && c.ItemType == key.Value.Type && c.IsAggregateRow);
                if (target is not null)
                    target.MonthlyAmounts[idx] += w.Amount;
                else
                    exp?.MonthlyAmounts[idx] += -w.Amount;
            }
            baselineWhatIfs.Add(w);
        }

        var result = new BreakevenIncome { RemainingMonths = remainingMonths };
        if (data.HasAccounts && remainingMonths > 0)
        {
            var baseline = ComputeBalance(data, selectedYear, currentYear, firstProjectedMonth,
                baseIncome, expenses, histograms, baselineWhatIfs, 0m, scheduled);
            decimal yearEnd = baseline.BalanceDaily.Length > 0 ? baseline.BalanceDaily[^1] : data.StartingBalance;
            result.Needed = data.StartingBalance - yearEnd;
        }

        foreach (var c in incomeCategories)
        {
            var split = splits.GetValueOrDefault(c.CategoryName, 0m);
            if (!c.IsAggregateRow || split <= 0m || result.Sources.ContainsKey(c.CategoryName)) continue;
            decimal remaining = remainingMonths > 0
                ? Math.Max(0m, result.Needed * split / 100m - whatIfBySource.GetValueOrDefault(c.CategoryName))
                : 0m;
            result.Sources[c.CategoryName] = remaining;
        }

        // A source's share goes onto its first aggregate row only.
        var placed = new HashSet<string>(StringComparer.Ordinal);
        result.RowMonthly = incomeCategories.Select((c, i) =>
        {
            var monthly = (decimal[])baseIncome[i].MonthlyAmounts.Clone();
            // Rows start from the pre-what-if amounts; ApplyWhatIfsToCategories layers what-ifs later.
            for (int m = 0; m < firstProjectedMonth - 1 && m < 12; m++) monthly[m] = c.MonthlyAmounts[m];
            decimal perMonth = c.IsAggregateRow && result.Sources.TryGetValue(c.CategoryName, out var r)
                && remainingMonths > 0 && placed.Add(c.CategoryName)
                ? r / remainingMonths : 0m;
            for (int m = firstProjectedMonth; m <= 12; m++)
                monthly[m - 1] = Math.Max(0m, Covered(scheduled, c, m)) + perMonth;
            return monthly;
        }).ToList();
        return result;
    }

    private static CategoryCashflow CopyRow(CategoryCashflow c) => new()
    {
        CategoryId = c.CategoryId,
        ItemType = c.ItemType,
        CategoryName = c.CategoryName,
        MonthlyAmounts = (decimal[])c.MonthlyAmounts.Clone(),
        IsAggregateRow = c.IsAggregateRow,
        OriginProjectId = c.OriginProjectId,
        OriginProjectName = c.OriginProjectName,
    };
}

/// <summary>Result of <see cref="CashflowCalculator.ComputeBreakevenIncome"/>.</summary>
public sealed class BreakevenIncome
{
    /// <summary>Income the balance line still needs by year end to close at its starting balance (negative when it already ends above).</summary>
    public decimal Needed { get; set; }
    public int RemainingMonths { get; set; }
    /// <summary>Remaining income per split source (never negative).</summary>
    public Dictionary<string, decimal> Sources { get; } = new(StringComparer.Ordinal);
    /// <summary>Monthly amounts per income row, in the order of the rows passed in.</summary>
    public List<decimal[]> RowMonthly { get; set; } = [];
}
