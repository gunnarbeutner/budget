using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using OpenBudget.Connectors;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.Assist;

/// <summary>A budget row to suggest amounts for.</summary>
public sealed record BudgetSuggestionInput((long Id, CategoryItemType ItemType) Key, string Name, bool IsIncome);

/// <summary>An unusual booking of a category; <see cref="Kind"/>: one_off, recurring or refund. Excluded ones are not in the suggestion.</summary>
public sealed record BudgetOutlier(long TransactionId, DateOnly Date, decimal Amount, string? Payee, string Kind, string? Note, bool Excluded);

/// <summary>The suggestion for one row: the deterministic baseline and the proposal (the baseline, or the AI's when it
/// answered). <see cref="Flagged"/>: the AI's year total is more than 30 % away from the baseline's.</summary>
public sealed record BudgetSuggestionRow(
    (long Id, CategoryItemType ItemType) Key, string Name, bool IsIncome, decimal[] Baseline, decimal[] Suggested, string Basis,
    IReadOnlyList<BudgetOutlier> Outliers, string? Explanation, bool Flagged, int MonthsOfData);

public sealed record BudgetSuggestionResult(IReadOnlyList<BudgetSuggestionRow> Rows, string? AiError, bool AiUsed);

/// <summary>What a tool-calling run took: requests sent, tool calls per name, tokens and time.</summary>
public sealed record BudgetToolRun(AssistResponse Final, int Rounds, IReadOnlyDictionary<string, int> ToolCalls, int InputTokens, int OutputTokens, TimeSpan Elapsed);

/// <summary>
/// "Suggest budget": a deterministic baseline per row (<see cref="BudgetBaselineMath"/>) that works without AI. With
/// AI on (configured and opted in by the user and the data's owner), the model classifies the outliers (one-off,
/// recurring, refund), may adjust the months and writes a short explanation per row; the server keeps the AI's
/// months only when there are 12 non-negative ones and flags a year total more than 30 % off the baseline. The model
/// gets per category the baseline, history, year totals and payees, once per request a household context
/// (<see cref="BudgetSuggestionContext"/>), and may call read-only tools (<see cref="BudgetSuggestionTools"/>) for up
/// to <see cref="MaxToolRounds"/> rounds or <see cref="ToolTimeBudget"/>, after which it must answer without them.
/// </summary>
public sealed class BudgetSuggestionService
{
    public const double FlagDeviation = 0.3;
    public const string SchemaName = "budget_suggestions";
    /// <summary>Payees listed per category; the rest go into one "other payees" line.</summary>
    public const int MaxPayees = 15;
    /// <summary>A payee booked in more months than this gets a month count instead of the list of months.</summary>
    public const int MaxListedMonths = 12;
    /// <summary>Requests in which the model may call tools; the next one asks for the answer without tools.</summary>
    public const int MaxToolRounds = 8;
    /// <summary>Tool calls answered per run; later ones get an error.</summary>
    public const int MaxToolCalls = 40;
    /// <summary>After this the model is asked for its answer without tools.</summary>
    public static readonly TimeSpan ToolTimeBudget = TimeSpan.FromMinutes(4);
    /// <summary>The whole AI run is cancelled after this.</summary>
    public static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(7);
    public const string ClampedNote = "Erstattungen hätten einzelne Monate negativ gemacht; diese Monate stehen auf 0.";

    private readonly IDbContextFactory<BudgetDbContext> _dbFactory;
    private readonly CategoryHierarchyService _hierarchy;
    private readonly ProjectService _projects;
    private readonly AssistSettingsService _settings;
    private readonly IAssistClient _client;
    private readonly ILogger<BudgetSuggestionService> _log;
    private readonly TimeProvider _clock;

    public BudgetSuggestionService(
        IDbContextFactory<BudgetDbContext> dbFactory, CategoryHierarchyService hierarchy, ProjectService projects,
        AssistSettingsService settings, IAssistClient client, ILogger<BudgetSuggestionService> log, TimeProvider clock)
    {
        _dbFactory = dbFactory;
        _hierarchy = hierarchy;
        _projects = projects;
        _settings = settings;
        _client = client;
        _log = log;
        _clock = clock;
    }

    /// <summary>
    /// Suggestions for the rows of a budget year of a project, from the 36 months before <paramref name="asOf"/>
    /// (default: the start of the current month, or the start of the year after when that is earlier).
    /// </summary>
    public async Task<BudgetSuggestionResult> SuggestAsync(
        int projectId, int year, IReadOnlyList<BudgetSuggestionInput> rows, bool useAi, DateOnly? asOf = null, CancellationToken ct = default)
    {
        var ownerId = await _projects.GetOwnerIdForProjectAsync(projectId);
        if (ownerId == null || rows.Count == 0) return new([], null, false);
        var now = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);
        var end = asOf ?? new[] { new DateOnly(now.Year, now.Month, 1), new DateOnly(year + 1, 1, 1) }.Min();
        var start = end.AddMonths(-36);

        var hierarchy = await _hierarchy.GetAllCategoriesWithChildCountsAsync(ownerId);
        var leaves = CategoryHierarchyService.BuildItemToLeafCategoryIds(rows.Select(r => r.Key), hierarchy);
        var allLeaves = leaves.Values.SelectMany(s => s).Distinct().ToList();

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var range = TimeRange.Between(new DateTimeOffset(start.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), new DateTimeOffset(end.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero));
        var txs = await db.Transactions.AsNoTracking()
            .Where(t => t.OwnerUserId == ownerId)
            .CountedAsFlow()
            .Where(t => t.CategoryId.HasValue && allLeaves.Contains(t.CategoryId.Value))
            .WithinUtc(range)
            .ToListAsync(ct);
        // Bookings after the cut-off (the running month, including ones the bank already lists with a later date): shown
        // to the model as the latest amounts, never part of the baseline.
        var today = DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);
        var recentTxs = today >= end
            ? await db.Transactions.AsNoTracking()
                .Where(t => t.OwnerUserId == ownerId)
                .CountedAsFlow()
                .Where(t => t.CategoryId.HasValue && allLeaves.Contains(t.CategoryId.Value))
                .WithinUtc(TimeRange.Between(new DateTimeOffset(end.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero), new DateTimeOffset(end.AddMonths(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)))
                .ToListAsync(ct)
            : [];
        var allSchedules = await db.ScheduledPayments.AsNoTracking().Where(p => p.OwnerUserId == ownerId).ToListAsync(ct);
        var schedules = allSchedules.Where(p => p.Kind == ScheduledPaymentKind.Standing && p.IntervalUnit == ScheduleUnit.Month).ToList();

        var computed = new List<(BudgetSuggestionInput Input, BudgetBaseline Baseline, Dictionary<long, Transaction> Rows)>();
        var recent = new List<IReadOnlyList<Transaction>>();
        var allBookings = new List<IReadOnlyList<BaselineBooking>>();
        foreach (var row in rows)
        {
            var leafIds = leaves.GetValueOrDefault(row.Key) ?? [];
            var own = txs.Where(t => leafIds.Contains(t.CategoryId!.Value)).ToList();
            var sign = row.IsIncome ? 1m : -1m;
            var bookings = own.Select(t => new BaselineBooking(t.Id, DateOnly.FromDateTime(t.Date), sign * t.Amount / 100m, CategoryMatching.PayeeKey(t.Name))).ToList();
            var payees = bookings.Select(b => b.PayeeKey).Where(k => k.Length > 0).ToHashSet(StringComparer.Ordinal);
            var rowSchedules = schedules
                .Select(s => new BaselineSchedule(CategoryMatching.PayeeKey(s.CounterpartyName), sign * s.Amount / 100m, s.IntervalCount ?? 1, s.NextDate))
                .Where(s => payees.Contains(s.PayeeKey))
                .ToList();
            computed.Add((row, BudgetBaselineMath.Compute(bookings, rowSchedules, year, end), own.ToDictionary(t => t.Id)));
            allBookings.Add(bookings);
            recent.Add(recentTxs.Where(t => leafIds.Contains(t.CategoryId!.Value)).OrderBy(t => t.Date).ToList());
        }

        var result = computed.Select(c => new BudgetSuggestionRow(
            c.Input.Key, c.Input.Name, c.Input.IsIncome, c.Baseline.Monthly, (decimal[])c.Baseline.Monthly.Clone(), c.Baseline.Basis,
            c.Baseline.Outliers.Select(o => new BudgetOutlier(o.Id, o.Date, o.Amount, c.Rows[o.Id].Name, "one_off", null, true)).ToList(),
            c.Baseline.Clamped ? ClampedNote : null, false, c.Baseline.MonthsOfData)).ToList();

        string? aiError = null;
        var aiUsed = false;
        if (useAi && _client.Enabled && await _settings.GetOptInAsync(ct) && (await _settings.OptedInAsync([ownerId], ct)).Contains(ownerId))
        {
            try
            {
                var household = await LoadHouseholdAsync(db, projectId, ownerId, start, end, allSchedules, ct);
                result = await AskAiAsync(year, computed, allBookings, recent, household, start, end, today, result, ct);
                aiUsed = true;
            }
            catch (AssistException ex)
            {
                aiError = ex.Message;
                _log.LogWarning(ex, "AI budget suggestions failed");
            }
        }
        return new BudgetSuggestionResult(result, aiError, aiUsed);
    }

    private sealed record Household(List<Account> Accounts, List<HouseholdAccount> DebtAccounts, List<ScheduledPayment> Schedules);

    /// <summary>The project's accounts (as the owner's), their debt-like balances and the schedules on them.</summary>
    private static async Task<Household> LoadHouseholdAsync(
        BudgetDbContext db, int projectId, string ownerId, DateOnly start, DateOnly end, List<ScheduledPayment> schedules, CancellationToken ct)
    {
        var accounts = await db.Accounts.AsNoTracking()
            .Where(a => a.OwnerUserId == ownerId && db.ProjectAccountAssignments.Any(p => p.ProjectId == projectId && p.AccountId == a.Id))
            .ToListAsync(ct);
        var candidates = accounts.Where(a => !a.Closed && a.Type is 1 or 3 or 4).Select(a => a.Id).ToList();
        // Split parts are left out: their parent carries the booking's full amount on the account.
        var rows = await db.Transactions.AsNoTracking()
            .Where(t => t.OwnerUserId == ownerId && t.AccountId.HasValue && candidates.Contains(t.AccountId.Value) && t.Splitted != SplitState.Child)
            .Select(t => new { AccountId = t.AccountId!.Value, t.Amount, t.Timestamp })
            .ToListAsync(ct);
        var bookings = rows.Select(r => (r.AccountId, r.Amount, DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(r.Timestamp).UtcDateTime))).ToList();
        var debt = BudgetSuggestionContext.Accounts(accounts, bookings, end, ((end.Year - start.Year) * 12) + end.Month - start.Month);
        var ids = accounts.Select(a => a.Id).ToHashSet();
        return new Household(accounts, debt, schedules.Where(s => ids.Contains(s.AccountId)).ToList());
    }

    private async Task<List<BudgetSuggestionRow>> AskAiAsync(
        int year, List<(BudgetSuggestionInput Input, BudgetBaseline Baseline, Dictionary<long, Transaction> Rows)> computed,
        List<IReadOnlyList<BaselineBooking>> bookings, List<IReadOnlyList<Transaction>> recent, Household household,
        DateOnly start, DateOnly end, DateOnly today, List<BudgetSuggestionRow> rows, CancellationToken ct)
    {
        var redactor = new AssistRedactor(await _settings.PersonNamesAsync(ct));
        redactor.SetOwnAccounts(household.Accounts.Select(a => a.Id));
        var handleOf = Enumerable.Range(0, computed.Count).Select(i => CategoryPrompt.Handle(i + 1)).ToList();

        // The category a payee's bookings mostly fall in, for the schedules.
        var payeeCategory = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var g in bookings.SelectMany((list, i) => list.Select(b => (b.PayeeKey, Handle: handleOf[i]))).Where(x => x.PayeeKey.Length > 0).GroupBy(x => x.PayeeKey, StringComparer.Ordinal))
            payeeCategory[g.Key] = g.GroupBy(x => x.Handle, StringComparer.Ordinal).OrderByDescending(h => h.Count()).ThenBy(h => h.Key, StringComparer.Ordinal).First().Key;
        var scheduled = household.Schedules.Select(s => CategoryMatching.PayeeKey(s.CounterpartyName)).Where(k => k.Length > 0).ToHashSet(StringComparer.Ordinal);
        var ended = BudgetSuggestionContext.EndedPayees(bookings.Select((list, i) => (handleOf[i], list)), scheduled, end);
        var context = BudgetSuggestionContext.Build(household.DebtAccounts, household.Schedules, ended, k => payeeCategory.GetValueOrDefault(k), redactor);

        var (request, handles) = BuildRequest(year, computed.Select(c => (c.Input, c.Baseline, (IReadOnlyDictionary<long, Transaction>)c.Rows)).ToList(), redactor, context,
            recent.Count > 0 && end <= today ? new BudgetCurrentMonth(end, today, recent) : null);
        var tools = new BudgetSuggestionTools(redactor,
            computed.Select((c, i) => (Handle: handleOf[i], Rows: (IReadOnlyList<Transaction>)c.Rows.Values.ToList())).ToDictionary(x => x.Handle, x => x.Rows, StringComparer.Ordinal),
            household.DebtAccounts, household.Accounts.ToDictionary(a => a.Id, a => a.KindLabel), start, end);
        var run = await RunAsync(_client, request, tools, _clock, ct);
        _log.LogInformation(
            "AI budget suggestion for {Count} rows ({Rounds} rounds, tools {Tools}, {Input} input tokens, {Output} output, {Ms} ms)",
            computed.Count, run.Rounds, run.ToolCalls.Count == 0 ? "none" : string.Join(", ", run.ToolCalls.Select(t => $"{t.Key}={t.Value}")),
            run.InputTokens, run.OutputTokens, (int)run.Elapsed.TotalMilliseconds);
        return Merge(run.Final, redactor, handles, rows);
    }

    /// <summary>
    /// Sends the request with the tools and answers the model's tool calls until it gives its answer. After
    /// <see cref="MaxToolRounds"/> requests with tools, or once <see cref="ToolTimeBudget"/> has passed, the next request
    /// forbids tool calls; the whole run is cancelled after <see cref="RunTimeout"/>.
    /// </summary>
    public static async Task<BudgetToolRun> RunAsync(IAssistClient client, AssistRequest request, BudgetSuggestionTools tools, TimeProvider clock, CancellationToken ct)
    {
        var started = clock.GetTimestamp();
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(RunTimeout);
        var turns = new JsonArray();
        int rounds = 0, inputTokens = 0, outputTokens = 0, answered = 0;
        try
        {
            while (true)
            {
                var final = rounds >= MaxToolRounds || clock.GetElapsedTime(started) >= ToolTimeBudget;
                var response = await client.CompleteAsync(request with
                {
                    Tools = BudgetSuggestionTools.Definitions,
                    Turns = (JsonArray)turns.DeepClone(),
                    ToolsDisabled = final,
                }, budget.Token);
                rounds++;
                inputTokens += response.InputTokens;
                outputTokens += response.OutputTokens;
                if (final || response.ToolCalls.Count == 0)
                    return new BudgetToolRun(response, rounds, tools.Calls, inputTokens, outputTokens, clock.GetElapsedTime(started));
                if (response.OutputItems != null)
                {
                    foreach (var item in response.OutputItems) turns.Add(item?.DeepClone());
                }
                else
                {
                    foreach (var call in response.ToolCalls)
                        turns.Add(new JsonObject { ["type"] = "function_call", ["call_id"] = call.CallId, ["name"] = call.Name, ["arguments"] = call.Arguments });
                }
                foreach (var call in response.ToolCalls)
                {
                    var output = ++answered > MaxToolCalls
                        ? "{\"error\":\"tool call limit reached; answer with what you have\"}"
                        : tools.Execute(call);
                    turns.Add(new JsonObject { ["type"] = "function_call_output", ["call_id"] = call.CallId, ["output"] = output });
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AssistException("The AI did not finish in time.");
        }
    }

    /// <summary>A row's bookings of the running month (redacted): payee, date and amount, positive like the other fields.</summary>
    private static JsonObject CurrentMonthNode(BudgetCurrentMonth month, int row, bool income, AssistRedactor redactor)
    {
        var sign = income ? 1m : -1m;
        var list = row < month.Rows.Count ? month.Rows[row] : [];
        return new JsonObject
        {
            ["month"] = month.From.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            ["through"] = month.Through.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["bookings"] = new JsonArray(list.Take(30).Select(t => (JsonNode)new JsonObject
            {
                ["payee"] = redactor.CleanText(CategoryMatching.PayeeKey(t.Name), [], 60),
                ["date"] = DateOnly.FromDateTime(t.Date).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["amount"] = sign * t.Amount / 100m,
            }).ToArray()),
        };
    }

    /// <summary>The request for a set of rows (redacted) and which handle ("c1") stands for which row.</summary>
    public static (AssistRequest Request, Dictionary<string, int> Handles) BuildRequest(
        int year, IReadOnlyList<(BudgetSuggestionInput Input, BudgetBaseline Baseline, IReadOnlyDictionary<long, Transaction> Rows)> computed, AssistRedactor redactor,
        JsonObject? context = null, BudgetCurrentMonth? currentMonth = null)
    {
        var handles = new Dictionary<string, int>(StringComparer.Ordinal);
        var items = new JsonArray();
        for (var i = 0; i < computed.Count; i++)
        {
            var (input, baseline, txRows) = computed[i];
            var handle = CategoryPrompt.Handle(i + 1);
            handles[handle] = i;
            items.Add(new JsonObject
            {
                ["category_id"] = handle,
                ["name"] = redactor.CleanText(input.Name, [], 80),
                ["income"] = input.IsIncome,
                ["months_of_data"] = baseline.MonthsOfData,
                ["median_month"] = baseline.Median,
                ["trend"] = baseline.Trend,
                ["basis"] = baseline.Basis,
                ["baseline_monthly"] = new JsonArray(baseline.Monthly.Select(m => (JsonNode)JsonValue.Create(m)).ToArray()),
                ["refunds_clamped"] = baseline.Clamped,
                ["year_totals"] = Totals(baseline),
                ["monthly_history"] = History(baseline),
                ["current_month"] = currentMonth == null ? null : CurrentMonthNode(currentMonth, i, input.IsIncome, redactor),
                ["payees"] = Payees(baseline, redactor),
                ["recurring"] = new JsonArray(baseline.Recurring.Select(r => (JsonNode)new JsonObject
                {
                    ["payee"] = redactor.CleanText(r.PayeeKey, [], 60),
                    ["every_months"] = r.IntervalMonths,
                    ["amount"] = r.Amount,
                }).ToArray()),
                ["outliers"] = new JsonArray(baseline.Outliers.Select(o =>
                    JsonSerializer.SerializeToNode(redactor.Redact(AssistSettingsService.ToSource(txRows[o.Id], null) with
                    {
                        Recurrence = baseline.OutlierNotes.GetValueOrDefault(o.Id),
                    }), CategoryPrompt.Json)).ToArray()),
            });
        }
        var system = $"""
            You help a German household plan a yearly budget ({year.ToString(CultureInfo.InvariantCulture)}) per category.
            For each category you get (amounts in EUR, positive = spending, or income for income categories):
            - baseline_monthly: a statistical proposal for the 12 months, computed as described by basis; months that
              refunds would have made negative are set to 0 (refunds_clamped).
            - year_totals: computed totals per full calendar year, for the current year to date (months = how many
              months it covers) and for the trailing 12 months, each with all bookings (total) and without the
              outlier bookings (without_outliers). Use these instead of adding up monthly_history yourself.
            - monthly_history: the category's actual total per month ("YYYY-MM"), oldest first, all bookings included.
            - current_month: the bookings of the running month so far, including ones already listed with a later date
              (the month is not complete, "through" is today). They are
              not in the baseline, the totals or the history; use them as the latest amount of a payee (a contract's
              newest payment), never as a monthly total or a trend.
            - payees: per payee the number of bookings (n), total, min/typical (median)/max amount, the months it was
              booked in (or months_count when booked in many months), first and last date; the largest payees only,
              the rest summed up as "other payees".
            - recurring: payments detected as repeating (every_months, amount).
            - outliers: unusual single bookings; recurrence says whether it was the payee's only booking or far above
              that payee's usual amount.
            The household context (once, before the categories) lists debt_accounts (loans, credit cards and checking
            accounts below zero: current balance, zero_now and the last month-end balances), scheduled_payments the
            bank holds (signed as booked, negative = money out; category_id when its payee's bookings mostly fall in
            one of the categories) and ended_monthly_payees (booked monthly, then no booking for more than 2 months).
            You may call tools to look at the bookings behind a category or payee, or at an account's balance history,
            when the summaries leave a question open; do not call them for every category.
            Classify every outlier: one_off (will not repeat), recurring (will happen again in the same month) or refund
            (money back). Return 12 non-negative monthly amounts. Start from the baseline but check it against
            year_totals, monthly_history and payees and correct it where they show a reason:
            - a payee booked once a year (or every quarter / half year) in the same month belongs in that month at
              about its latest amount, not spread over the year; add it if the baseline misses it;
            - large spending that recurs every year in different bookings (trips, equipment for a hobby, larger
              household purchases) is a reserve to plan, not a one-off: plan about the yearly amount without truly
              unique items, averaging the full years and the trailing 12 months with the trailing 12 months weighted
              at least half (recent spending decides when it differs), and place it in the months it usually happens,
              or spread it evenly when there is no pattern;
            - a payee's latest amount is its newest booking, including current_month; a single lower or higher payment
              between two regular ones is an exception, not the new amount;
            - a large one-off purchase hidden in a payee that is otherwise small and regular (for example a shop or
              marketplace) should not inflate the months; take it out unless it clearly repeats;
            - a monthly contract (phone, transport ticket, subscription) should show up every month at its latest amount;
              a monthly payee with no booking for more than 2 months has ended (see ended_monthly_payees): do not plan
              it, unless a scheduled payment says it continues;
            - a change is a trend only when it holds for at least 3 months and has a cause visible in the data (a new
              or ended payee, a new contract amount); a jump in one or two months is not a trend, do not extrapolate it;
              when the current year's run rate (current_year_to_date) differs from the last full year, weigh it by
              how many months it covers;
            - interest and fees that depend on a balance (overdraft or loan interest, credit-card fees) follow the
              current balance of the accounts in debt_accounts: an account at zero now causes no interest going
              forward.
            Do not merely repeat the baseline when the data shows such a case. basis: one of median, seasonal, recurring,
            trend, none. explanation: one or two short sentences in German for the household saying what you changed and
            why, no amounts of single bookings.
            Tokens like PERSON_A stand for household members, ACC_1 for a counterparty account, OWN_1 for an own account,
            # for removed digits.
            """;
        var user = (context == null ? "" : "Household context:\n" + context.ToJsonString(CategoryPrompt.Json) + "\n\n")
            + "Categories:\n" + items.ToJsonString(CategoryPrompt.Json);
        return (new AssistRequest(SchemaName, Schema(), system, user), handles);
    }

    private static JsonObject Totals(BudgetBaseline baseline)
    {
        static JsonObject Node(PeriodTotal p) => new() { ["months"] = p.Months, ["total"] = p.Total, ["without_outliers"] = p.WithoutOutliers };
        var o = new JsonObject();
        if (baseline.Totals is not { } totals) return o;
        var years = new JsonObject();
        foreach (var (y, total) in totals.FullYears) years[y.ToString(CultureInfo.InvariantCulture)] = Node(total);
        o["full_years"] = years;
        if (totals.CurrentYear is { } current)
        {
            var node = Node(current);
            node["year"] = totals.Year;
            o["current_year_to_date"] = node;
        }
        o["trailing_12_months"] = Node(totals.Trailing12);
        return o;
    }

    private static JsonObject History(BudgetBaseline baseline)
    {
        var history = new JsonObject();
        for (var i = 0; i < baseline.History.Length; i++)
            history[MonthLabel(baseline.HistoryStart.AddMonths(i))] = baseline.History[i];
        return history;
    }

    private static JsonArray Payees(BudgetBaseline baseline, AssistRedactor redactor)
    {
        var list = new JsonArray();
        foreach (var p in baseline.Payees.Take(MaxPayees))
        {
            var o = new JsonObject
            {
                ["payee"] = redactor.CleanText(p.PayeeKey, [], 60),
                ["n"] = p.Count,
                ["total"] = p.Total,
                ["min"] = p.Min,
                ["typical"] = p.Typical,
                ["max"] = p.Max,
            };
            if (p.Months.Count > MaxListedMonths) o["months_count"] = p.Months.Count;
            else o["months"] = string.Join(", ", p.Months.Select(MonthLabel));
            o["first"] = p.First.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            o["last"] = p.Last.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            list.Add(o);
        }
        var rest = baseline.Payees.Skip(MaxPayees).ToList();
        if (rest.Count > 0)
            list.Add(new JsonObject { ["payee"] = "other payees", ["payees"] = rest.Count, ["n"] = rest.Sum(p => p.Count), ["total"] = rest.Sum(p => p.Total) });
        return list;
    }

    private static string MonthLabel(DateOnly d) => d.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    /// <summary>Applies the model's answer onto the baseline rows (see the class summary for what is checked).</summary>
    public static List<BudgetSuggestionRow> Merge(AssistResponse response, AssistRedactor redactor, IReadOnlyDictionary<string, int> handles, List<BudgetSuggestionRow> rows)
    {
        if (response.Refusal != null) throw new AssistException("The AI declined to answer.");
        if (string.IsNullOrWhiteSpace(response.Json)) throw new AssistException("The AI sent an empty answer.");
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(response.Json);
        }
        catch (JsonException ex)
        {
            throw new AssistException("The AI sent an unreadable answer.", ex);
        }
        if (root?["categories"] is not JsonArray list) throw new AssistException("The AI answer does not match the schema.");
        var result = rows.ToList();
        foreach (var node in list.OfType<JsonObject>())
        {
            if (Str(node["categoryId"]) is not { } h || !handles.TryGetValue(h, out var index)) continue;
            var row = result[index];
            var kinds = new Dictionary<long, (string Kind, string? Note)>();
            foreach (var o in (node["outliers"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var kind = Str(o["kind"]);
                if (redactor.Resolve(Str(o["tx"])) is { } tx && kind is "one_off" or "recurring" or "refund")
                    kinds[tx] = (kind, Str(o["note"]));
            }
            var outliers = row.Outliers.Select(o => kinds.TryGetValue(o.TransactionId, out var k)
                ? o with { Kind = k.Kind, Note = k.Note, Excluded = k.Kind != "recurring" }
                : o).ToList();
            var suggested = (decimal[])row.Suggested.Clone();
            var months = (node["monthly"] as JsonArray)?.Select(v => v is JsonValue jv && jv.TryGetValue<decimal>(out var d) ? d : -1m).ToList();
            if (months is { Count: 12 } && months.All(m => m >= 0))
            {
                suggested = months.Select(m => Math.Round(m, 0, MidpointRounding.AwayFromZero)).ToArray();
            }
            else
            {
                // Without usable months a recurring outlier still comes back in its month.
                foreach (var o in outliers.Where(o => !o.Excluded)) suggested[o.Date.Month - 1] += Math.Round(o.Amount, 0);
            }
            var baseTotal = row.Baseline.Sum();
            var total = suggested.Sum();
            var flagged = baseTotal == 0 ? total != 0 : (double)Math.Abs((total - baseTotal) / baseTotal) > FlagDeviation;
            var basis = Str(node["basis"]) is { } b && b is "median" or "seasonal" or "recurring" or "trend" or "none" ? b : row.Basis;
            var explanation = Str(node["explanation"]);
            if (explanation is { Length: > 400 }) explanation = explanation[..400];
            result[index] = row with { Suggested = suggested, Outliers = outliers, Explanation = explanation, Flagged = flagged, Basis = basis };
        }
        return result;
    }

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static JsonObject Schema() => (JsonObject)JsonNode.Parse("""
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["categories"],
          "properties": {
            "categories": {
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["categoryId", "monthly", "basis", "outliers", "explanation"],
                "properties": {
                  "categoryId": { "type": "string" },
                  "monthly": { "type": "array", "items": { "type": "number" } },
                  "basis": { "type": "string", "enum": ["median", "seasonal", "recurring", "trend", "none"] },
                  "outliers": {
                    "type": "array",
                    "items": {
                      "type": "object",
                      "additionalProperties": false,
                      "required": ["tx", "kind", "note"],
                      "properties": {
                        "tx": { "type": "string" },
                        "kind": { "type": "string", "enum": ["one_off", "recurring", "refund"] },
                        "note": { "type": "string" }
                      }
                    }
                  },
                  "explanation": { "type": "string" }
                }
              }
            }
          }
        }
        """)!;
}

/// <summary>The running month after the cut-off: its first day, today and each row's bookings so far.</summary>
public sealed record BudgetCurrentMonth(DateOnly From, DateOnly Through, IReadOnlyList<IReadOnlyList<Transaction>> Rows);
