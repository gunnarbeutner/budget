using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using OpenBudget.Connectors;
using OpenBudget.Models;

namespace OpenBudget.Assist;

/// <summary>A loan, credit card or overdrawn checking account of the household and its balance at the end of each
/// month of the window (oldest first, labelled "YYYY-MM").</summary>
public sealed record HouseholdAccount(long AccountId, string Kind, decimal Current, IReadOnlyList<(string Month, decimal Balance)> MonthEnds);

/// <summary>A payee of a category booked in at least <see cref="BudgetSuggestionContext.MinEndedMonths"/> consecutive
/// months that has had no booking since <see cref="Last"/>.</summary>
public sealed record EndedPayee(string Handle, string PayeeKey, DateOnly Last, decimal Usual, int Months, bool Scheduled);

/// <summary>
/// The household context of "Suggest budget", sent once per request: balances of loan, credit-card and overdrawn
/// checking accounts, the standing orders and scheduled transfers the bank holds, and monthly payees that stopped.
/// Every text goes through the request's <see cref="AssistRedactor"/>.
/// </summary>
public static class BudgetSuggestionContext
{
    /// <summary>Month-end balances shown in the context; the tool gives the whole window.</summary>
    public const int ContextMonths = 6;
    public const int MinEndedMonths = 4;
    /// <summary>A monthly payee counts as ended without a booking in this many months before the cut-off.</summary>
    public const int EndedGapMonths = 2;
    public const int MaxEnded = 20;
    public const int MaxSchedules = 40;

    private static int MonthIndex(DateOnly d) => (d.Year * 12) + d.Month - 1;

    private static string Label(int index) => new DateOnly(index / 12, (index % 12) + 1, 1).ToString("yyyy-MM", CultureInfo.InvariantCulture);

    /// <summary>
    /// The accounts whose balance can drive interest or fees: loans (type 4), credit cards (type 3) and checking
    /// accounts (type 1) below zero now or at a month end of the last <see cref="ContextMonths"/> months. Balances are
    /// the account's current balance minus the bookings after each month end, for the <paramref name="months"/> months
    /// before <paramref name="end"/>. <paramref name="bookings"/> are all bookings of the accounts (no split parts).
    /// </summary>
    public static List<HouseholdAccount> Accounts(IEnumerable<Account> accounts, IReadOnlyList<(long AccountId, long Amount, DateOnly Date)> bookings, DateOnly end, int months = 36)
    {
        var endIndex = MonthIndex(end);
        var result = new List<HouseholdAccount>();
        foreach (var a in accounts.Where(a => !a.Closed && a.Type is 1 or 3 or 4).OrderBy(a => a.Id))
        {
            var own = bookings.Where(b => b.AccountId == a.Id).ToList();
            var current = a.CurrentBalance ?? ((a.InitialBalance + own.Sum(b => b.Amount)) / 100m);
            var ends = new List<(string, decimal)>();
            for (var m = endIndex - months; m < endIndex; m++)
                ends.Add((Label(m), current - (own.Where(b => MonthIndex(b.Date) > m).Sum(b => b.Amount) / 100m)));
            var kind = a.Type switch { 3 => "credit_card", 4 => "loan", _ => "checking_overdraft" };
            if (a.Type == 1 && current >= 0 && ends.TakeLast(ContextMonths).All(e => e.Item2 >= 0)) continue;
            result.Add(new HouseholdAccount(a.Id, kind, current, ends));
        }
        return result;
    }

    /// <summary>
    /// Payees per category (handle → its bookings, positive amounts) booked in at least <see cref="MinEndedMonths"/>
    /// consecutive months up to their last booking, which lies more than <see cref="EndedGapMonths"/> months before
    /// <paramref name="end"/> but within the last year. <see cref="EndedPayee.Scheduled"/>: the bank holds a schedule for it.
    /// </summary>
    public static List<EndedPayee> EndedPayees(IEnumerable<(string Handle, IReadOnlyList<BaselineBooking> Bookings)> categories, IReadOnlySet<string> scheduledPayees, DateOnly end)
    {
        var endIndex = MonthIndex(end);
        var result = new List<EndedPayee>();
        foreach (var (handle, bookings) in categories)
        {
            foreach (var g in bookings.Where(b => b.PayeeKey.Length > 0 && MonthIndex(b.Date) < endIndex).GroupBy(b => b.PayeeKey, StringComparer.Ordinal))
            {
                var monthsBooked = g.Select(b => MonthIndex(b.Date)).Distinct().OrderDescending().ToList();
                var last = monthsBooked[0];
                if (last > endIndex - 1 - EndedGapMonths || last < endIndex - 12) continue;
                var run = 1;
                while (run < monthsBooked.Count && monthsBooked[run] == last - run) run++;
                if (run < MinEndedMonths) continue;
                var runBookings = g.Where(b => MonthIndex(b.Date) > last - run).Select(b => b.Amount).ToList();
                result.Add(new EndedPayee(handle, g.Key, g.Max(b => b.Date), BudgetBaselineMath.Median(runBookings), run, scheduledPayees.Contains(g.Key)));
            }
        }
        return result.OrderByDescending(e => e.Last).ThenBy(e => e.Handle, StringComparer.Ordinal).Take(MaxEnded).ToList();
    }

    /// <summary>"monthly", "every 3 months", "weekly", "once".</summary>
    public static string Interval(ScheduledPayment s)
    {
        if (s.Kind != ScheduledPaymentKind.Standing) return "once";
        var n = s.IntervalCount ?? 1;
        var unit = s.IntervalUnit == ScheduleUnit.Week ? "week" : "month";
        return n == 1 ? (unit == "week" ? "weekly" : "monthly") : string.Create(CultureInfo.InvariantCulture, $"every {n} {unit}s");
    }

    /// <summary>
    /// The context block. <paramref name="payeeCategory"/> maps a payee key to the handle of the category its bookings
    /// mostly fall in (among the categories of the request).
    /// </summary>
    public static JsonObject Build(
        IReadOnlyList<HouseholdAccount> accounts, IReadOnlyList<ScheduledPayment> schedules, IReadOnlyList<EndedPayee> ended,
        Func<string, string?> payeeCategory, AssistRedactor redactor)
    {
        var accountList = new JsonArray();
        foreach (var a in accounts)
        {
            var ends = new JsonObject();
            foreach (var (month, balance) in a.MonthEnds.TakeLast(ContextMonths)) ends[month] = balance;
            accountList.Add(new JsonObject
            {
                ["account"] = redactor.OwnAccountToken(a.AccountId),
                ["kind"] = a.Kind,
                ["balance_now"] = a.Current,
                ["zero_now"] = a.Current == 0,
                ["month_end_balances"] = ends,
            });
        }
        var scheduleList = new JsonArray();
        foreach (var s in schedules.OrderBy(s => s.NextDate).ThenBy(s => s.Id).Take(MaxSchedules))
        {
            var o = new JsonObject
            {
                ["payee"] = redactor.CleanText(s.CounterpartyName, [], 60),
                ["amount"] = s.Amount / 100m,
                ["interval"] = Interval(s),
                ["next"] = s.NextDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            };
            if (s.LastDate is { } until) o["until"] = until.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (payeeCategory(CategoryMatching.PayeeKey(s.CounterpartyName)) is { } handle) o["category_id"] = handle;
            scheduleList.Add(o);
        }
        var endedList = new JsonArray(ended.Select(e => (JsonNode)new JsonObject
        {
            ["payee"] = redactor.CleanText(e.PayeeKey, [], 60),
            ["category_id"] = e.Handle,
            ["last"] = e.Last.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["usual_amount"] = e.Usual,
            ["months_in_a_row"] = e.Months,
            ["has_schedule"] = e.Scheduled,
        }).ToArray());
        return new JsonObject
        {
            ["debt_accounts"] = accountList,
            ["scheduled_payments"] = scheduleList,
            ["ended_monthly_payees"] = endedList,
        };
    }
}

/// <summary>
/// The read-only tools the model may call during "Suggest budget": the bookings of a category in a month range, the
/// bookings of one payee of a category, and the month-end balances of a household account. Handles and tokens are
/// those of the request; every booking goes through the request's <see cref="AssistRedactor"/>, so pseudonyms stay
/// consistent within the run. Unknown handles and bad arguments come back as an error for the model.
/// </summary>
public sealed class BudgetSuggestionTools
{
    public const int MaxRows = 60;

    private readonly AssistRedactor _redactor;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<Transaction>> _categories;
    private readonly Dictionary<long, HouseholdAccount> _accounts;
    private readonly IReadOnlyDictionary<long, string> _accountKinds;
    private readonly DateOnly _start;
    private readonly DateOnly _end;

    /// <summary>The tools of one run.</summary>
    /// <param name="redactor">The request's redactor (with the own accounts' tokens set).</param>
    /// <param name="categories">Per category handle its bookings in the window.</param>
    /// <param name="accounts">The accounts of the context block.</param>
    /// <param name="accountKinds">Account id → kind label for the bookings.</param>
    /// <param name="start">First day of the window.</param>
    /// <param name="end">The cut-off (exclusive).</param>
    public BudgetSuggestionTools(
        AssistRedactor redactor, IReadOnlyDictionary<string, IReadOnlyList<Transaction>> categories, IEnumerable<HouseholdAccount> accounts,
        IReadOnlyDictionary<long, string> accountKinds, DateOnly start, DateOnly end)
    {
        _redactor = redactor;
        _categories = categories;
        _accounts = accounts.ToDictionary(a => a.AccountId);
        _accountKinds = accountKinds;
        _start = start;
        _end = end;
    }

    /// <summary>Calls per tool name in this run.</summary>
    public Dictionary<string, int> Calls { get; } = new(StringComparer.Ordinal);

    public static IReadOnlyList<AssistTool> Definitions { get; } =
    [
        new("category_bookings",
            "The bookings of a category (category_id like c3) from from_month to to_month (YYYY-MM, inclusive) within the 36-month window, " +
            $"newest {MaxRows} at most, with count and sum of all. Amounts are signed as booked (negative = money out).",
            Parameters(("category_id", "Category handle, e.g. c3"), ("from_month", "YYYY-MM"), ("to_month", "YYYY-MM"))),
        new("payee_bookings",
            "The bookings of one payee (exactly as written in that category's payees list) in a category over the 36-month window, " +
            $"newest {MaxRows} at most. Amounts are signed as booked.",
            Parameters(("category_id", "Category handle, e.g. c3"), ("payee", "Payee as shown in the category's payees"))),
        new("account_balance_history",
            "Month-end balances of a household account from debt_accounts (token like OWN_2) for the 36-month window.",
            Parameters(("account", "Account token, e.g. OWN_2"))),
    ];

    private static JsonObject Parameters(params (string Name, string Description)[] props)
    {
        var properties = new JsonObject();
        foreach (var (name, description) in props) properties[name] = new JsonObject { ["type"] = "string", ["description"] = description };
        return new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray(props.Select(p => (JsonNode)p.Name).ToArray()),
            ["properties"] = properties,
        };
    }

    /// <summary>Runs one call and gives its result as JSON text.</summary>
    public string Execute(AssistToolCall call)
    {
        Calls[call.Name] = Calls.GetValueOrDefault(call.Name) + 1;
        JsonObject? args;
        try
        {
            args = JsonNode.Parse(call.Arguments) as JsonObject;
        }
        catch (JsonException)
        {
            args = null;
        }
        if (args == null) return Error("arguments are not a JSON object");
        var result = call.Name switch
        {
            "category_bookings" => CategoryBookings(Arg(args, "category_id"), Arg(args, "from_month"), Arg(args, "to_month")),
            "payee_bookings" => PayeeBookings(Arg(args, "category_id"), Arg(args, "payee")),
            "account_balance_history" => BalanceHistory(Arg(args, "account")),
            _ => ErrorNode($"unknown tool {_redactor.CleanText(call.Name, [], 40)}"),
        };
        return result.ToJsonString(CategoryPrompt.Json);
    }

    private static string Arg(JsonObject args, string name) => args[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : "";

    private static string Error(string message) => ErrorNode(message).ToJsonString(CategoryPrompt.Json);

    private static JsonObject ErrorNode(string message) => new() { ["error"] = message };

    private JsonObject CategoryBookings(string handle, string from, string to)
    {
        if (!_categories.TryGetValue(handle, out var rows)) return ErrorNode("unknown category_id; use one of the category_id values of the request");
        if (!TryMonth(from, out var first) || !TryMonth(to, out var last)) return ErrorNode("from_month and to_month must be YYYY-MM");
        var lastEnd = last.AddMonths(1);
        return Bookings(rows.Where(t => DateOnly.FromDateTime(t.Date) >= first && DateOnly.FromDateTime(t.Date) < lastEnd));
    }

    private JsonObject PayeeBookings(string handle, string payee)
    {
        if (!_categories.TryGetValue(handle, out var rows)) return ErrorNode("unknown category_id; use one of the category_id values of the request");
        // The model knows the payee only in its redacted form; match that against each payee key's redacted form.
        var matches = rows.Where(t => string.Equals(_redactor.CleanText(CategoryMatching.PayeeKey(t.Name), [], 60), payee, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count == 0 ? ErrorNode("no bookings of this payee in this category") : Bookings(matches);
    }

    private JsonObject Bookings(IEnumerable<Transaction> rows)
    {
        var list = rows.OrderByDescending(t => t.Timestamp).ThenByDescending(t => t.Id).ToList();
        var shown = list.Take(MaxRows).OrderBy(t => t.Timestamp).ThenBy(t => t.Id).Select(t =>
            JsonSerializer.SerializeToNode(_redactor.Redact(AssistSettingsService.ToSource(t, t.AccountId is { } a ? _accountKinds.GetValueOrDefault(a) : null) with
            {
                OwnAccount = _redactor.OwnAccountToken(t.AccountId),
            }), CategoryPrompt.Json)).ToArray();
        return new JsonObject
        {
            ["count"] = list.Count,
            ["sum"] = list.Sum(t => t.Amount) / 100m,
            ["truncated"] = list.Count > MaxRows,
            ["bookings"] = new JsonArray(shown),
        };
    }

    private JsonObject BalanceHistory(string token)
    {
        if (_redactor.ResolveOwnAccount(token) is not { } id || !_accounts.TryGetValue(id, out var account))
            return ErrorNode("unknown account; use an account token from debt_accounts");
        var ends = new JsonObject();
        foreach (var (month, balance) in account.MonthEnds) ends[month] = balance;
        return new JsonObject { ["account"] = token, ["kind"] = account.Kind, ["balance_now"] = account.Current, ["month_end_balances"] = ends };
    }

    private bool TryMonth(string text, out DateOnly month)
    {
        month = default;
        if (!DateOnly.TryParseExact(text + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return false;
        month = d < _start ? _start : d >= _end ? _end.AddMonths(-1) : d;
        return true;
    }
}
