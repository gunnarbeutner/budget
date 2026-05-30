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
        decimal floor)
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
                foreach (var income in incomeCategories)
                {
                    if (!income.IsAggregateRow) continue;
                    var amount = income.MonthlyAmounts[m - 1];
                    if (amount == 0m) continue;
                    if (!weights.TryGetValue((income.CategoryId, income.ItemType), out var dist)) continue;
                    DistributeAcrossMonth(deltas, startDoy, daysInMonth, amount, dist.Weights, sign: +1);
                }

                foreach (var expense in expenseCategories)
                {
                    if (!expense.IsAggregateRow) continue;
                    var amount = expense.MonthlyAmounts[m - 1];
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
}
