// Offline accuracy check of the category and budget suggestions against a COPY of a database, opened read-only.
// Prints aggregate numbers only (no payees, amounts or IBANs; category paths in the confusion list). With --models it sends redacted bookings to the
// configured OpenAI endpoint (OPENBUDGET_OPENAI_API_KEY in the environment) — only run it on data whose owners agreed.
//
//   dotnet run --project tools/AssistEval -- --db <copy.db> [--sample 300] [--models gpt-6.1-terra,gpt-6-luna] [--budget-year 2025]
using System.Diagnostics;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OpenBudget.Assist;
using OpenBudget.Models;
using OpenBudget.Services;

var opts = Args.Parse(args);
// The copy is brought to the current schema first (it is a copy: never point this at a live database).
await using (var migrate = new BudgetDbContext(new DbContextOptionsBuilder<BudgetDbContext>().UseSqlite($"Data Source={opts.Db}")
    .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)).Options))
    await migrate.Database.MigrateAsync();
var dbOptions = new DbContextOptionsBuilder<BudgetDbContext>().UseSqlite($"Data Source={opts.Db};Mode=ReadOnly").Options;
await using var db = new BudgetDbContext(dbOptions);

var users = await db.Users.AsNoTracking().ToListAsync();
var personNames = users.SelectMany(u => new[] { u.DisplayName, u.UserName }).Where(n => !string.IsNullOrWhiteSpace(n) && n!.Length >= 3).Select(n => n!).ToList();
var owners = await db.Transactions.AsNoTracking().Select(t => t.OwnerUserId).Distinct().ToListAsync();

var data = new List<OwnerData>();
foreach (var owner in owners)
{
    var groups = await db.CategoryGroups.AsNoTracking().Where(g => g.OwnerUserId == owner).ToListAsync();
    var cats = await db.Categories.AsNoTracking().Where(c => c.OwnerUserId == owner).ToListAsync();
    var picker = CategoryPickerLogic.Build(groups, cats, await RowIds.SentinelRootAsync(db, owner));
    var valid = picker.Options.ToDictionary(o => o.Id);
    var accounts = await db.Accounts.AsNoTracking().Where(a => a.OwnerUserId == owner).ToDictionaryAsync(a => a.Id);
    var txs = await db.Transactions.AsNoTracking().Where(t => t.OwnerUserId == owner && t.Splitted != SplitState.Parent).ToListAsync();
    var rules = await db.CategoryRules.AsNoTracking().Where(r => r.Project.OwnerUserId == owner && r.Enabled && !r.Dismissed).OrderBy(r => r.SortOrder).ToListAsync();
    var scheduled = await db.ScheduledPayments.AsNoTracking().Where(p => p.OwnerUserId == owner).ToListAsync();
    data.Add(new OwnerData(owner, picker.Options.ToList(), valid, accounts, txs, rules, scheduled));
}
Console.WriteLine($"owners={data.Count} transactions={data.Sum(d => d.Txs.Count)} categorized={data.Sum(d => d.Categorized.Count)}");

if (opts.Sample > 0) await CategoryEval.RunAsync(data, personNames, opts);
if (opts.BudgetYear > 0) await BudgetEval.RunAsync(data, personNames, opts);
return 0;

internal sealed record Args(string Db, int Sample, string[] Models, int BudgetYear, int Seed)
{
    public static Args Parse(string[] a)
    {
        string Get(string name, string def) => Array.IndexOf(a, name) is var i and >= 0 && i + 1 < a.Length ? a[i + 1] : def;
        return new Args(Get("--db", ""), int.Parse(Get("--sample", "300"), CultureInfo.InvariantCulture),
            Get("--models", "").Split(',', StringSplitOptions.RemoveEmptyEntries), int.Parse(Get("--budget-year", "0"), CultureInfo.InvariantCulture),
            int.Parse(Get("--seed", "42"), CultureInfo.InvariantCulture));
    }
}

internal sealed record OwnerData(
    string Owner, List<CategoryOption> Options, Dictionary<long, CategoryOption> Valid, Dictionary<long, Account> Accounts,
    List<Transaction> Txs, List<CategoryRule> Rules, List<ScheduledPayment> Scheduled)
{
    public List<Transaction> Categorized { get; } = Txs.Where(t => t.CategoryId is { } c && Valid.ContainsKey(c)).ToList();

    public HashSet<long> IncomeCategories { get; } = Txs.Where(t => t.CategoryId is { } c && Valid.ContainsKey(c))
        .GroupBy(t => t.CategoryId!.Value).Where(g => g.Sum(t => t.Amount) > 0).Select(g => g.Key).ToHashSet();
}

internal sealed record Usage(int Calls, int Input, int Cached, int Output, double Seconds);

internal static class Ai
{
    private sealed class Factory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new() { Timeout = Timeout.InfiniteTimeSpan };
    }

    public static OpenAiAssistClient Client(string model) => new(new Factory(), AssistOptions.From(n => n == AssistOptions.ModelVariable ? model : Environment.GetEnvironmentVariable(n)),
        NullLogger<OpenAiAssistClient>.Instance);
}

internal static class CategoryEval
{
    private sealed record Item(OwnerData Owner, Transaction Tx, long Truth);

    private sealed record Prediction(long? Category, double Confidence, string Source, List<long> Top3);

    public static async Task RunAsync(List<OwnerData> data, List<string> names, Args opts)
    {
        var rng = new Random(opts.Seed);
        var latest = data.SelectMany(d => d.Txs).Max(t => t.Timestamp);
        var from = latest - (2 * 366 * 86400L);
        var pool = data.SelectMany(d => d.Categorized.Where(t => t.Timestamp >= from).Select(t => new Item(d, t, t.CategoryId!.Value))).ToList();
        var sample = pool.OrderBy(_ => rng.Next()).Take(opts.Sample).ToList();
        var hidden = sample.Select(s => s.Tx.Id).ToHashSet();
        Console.WriteLine($"\n== Categories: sample {sample.Count} of {pool.Count} categorized bookings in the last 2 years ({sample.GroupBy(s => s.Owner.Owner).Count()} owners)");

        var matchers = data.ToDictionary(d => d.Owner, d => new CategoryMatching(d.Categorized.Where(t => !hidden.Contains(t.Id)).Select(t =>
            new HistoryBooking(t.Id, t.CategoryId!.Value, CategoryMatching.IbanKey(t.CounterpartyIban), CategoryMatching.PayeeKey(t.Name),
                CategoryMatching.Tokens(t.Name, Purpose(t)), t.Amount))));

        var det = new Dictionary<long, Prediction>();
        var unsure = new List<(Item Item, List<Neighbour> Neighbours)>();
        foreach (var s in sample)
        {
            var m = matchers[s.Owner.Owner];
            var target = new TargetBooking(s.Tx.Id, CategoryMatching.IbanKey(s.Tx.CounterpartyIban), CategoryMatching.PayeeKey(s.Tx.Name),
                CategoryMatching.Tokens(s.Tx.Name, Purpose(s.Tx)), s.Tx.Amount);
            var vote = m.IbanVote(target);
            var neighbours = m.Neighbours(target);
            if (vote == null || vote.Confidence < CategorySuggestionService.SureEnough)
            {
                var knn = CategoryMatching.KnnVote(neighbours);
                if (knn != null && (vote == null || knn.Confidence > vote.Confidence)) vote = knn;
            }
            var top3 = Top3(neighbours, vote?.CategoryId);
            det[s.Tx.Id] = vote == null ? new Prediction(null, 0, "none", top3) : new Prediction(vote.CategoryId, vote.Confidence, vote.Source, top3);
            if (vote == null || vote.Confidence < CategorySuggestionService.SureEnough) unsure.Add((s, neighbours));
        }
        Report("deterministic only", sample, det, null);

        foreach (var model in opts.Models)
        {
            var preds = new Dictionary<long, Prediction>(det);
            var rules = new List<(OwnerData Owner, string Expression, long Category)>();
            var usage = new Usage(0, 0, 0, 0, 0);
            var failures = 0;
            var client = Ai.Client(model);
            foreach (var ownerGroup in unsure.GroupBy(u => u.Item.Owner))
            {
                var owner = ownerGroup.Key;
                var categories = owner.Options.Select((o, i) => new PromptCategory(CategoryPrompt.Handle(i + 1), o.Id, o.Path, owner.IncomeCategories.Contains(o.Id))).ToList();
                var byId = categories.ToDictionary(c => c.Id);
                var handles = categories.ToDictionary(c => c.Handle, StringComparer.Ordinal);
                var examples = owner.Rules.Where(r => byId.ContainsKey(r.CategoryId)).Take(30)
                    .Select(r => RuleExpression.TryParse(r.Expression, out _)?.Print() + " => " + byId[r.CategoryId].Handle)
                    .Where(x => !x.Contains("account", StringComparison.Ordinal)).ToList();
                var system = CategoryPrompt.SystemPrompt(categories, examples);
                var txById = owner.Txs.ToDictionary(t => t.Id);
                foreach (var batch in ownerGroup.Chunk(CategorySuggestionService.BatchSize))
                {
                    var redactor = new AssistRedactor(names);
                    var items = batch.Select(b => new PromptItem(redactor.Redact(Source(owner, b.Item.Tx)),
                        b.Neighbours.Take(3).Select(n => (redactor.Redact(Source(owner, txById[n.Booking.Id])), byId[n.Booking.CategoryId].Handle)).ToList())).ToList();
                    try
                    {
                        var response = await client.CompleteAsync(new AssistRequest(CategoryPrompt.SchemaName, CategoryPrompt.Schema(), system, CategoryPrompt.UserContent(items)));
                        usage = usage with
                        {
                            Calls = usage.Calls + 1, Input = usage.Input + response.InputTokens, Cached = usage.Cached + response.CachedTokens,
                            Output = usage.Output + response.OutputTokens, Seconds = usage.Seconds + response.Elapsed.TotalSeconds,
                        };
                        foreach (var a in CategoryPrompt.ParseAnswer(response, redactor, handles))
                        {
                            if (a.CategoryId == null) continue;
                            var old = preds[a.TransactionId];
                            if (old.Category != null && old.Confidence >= a.Confidence) continue;
                            var top3 = new List<long> { a.CategoryId.Value };
                            top3.AddRange(old.Top3.Where(c => c != a.CategoryId).Take(2));
                            preds[a.TransactionId] = new Prediction(a.CategoryId, a.Confidence, "ai", top3);
                            if (a.RuleExpression != null) rules.Add((owner, a.RuleExpression, a.CategoryId.Value));
                        }
                    }
                    catch (AssistException ex)
                    {
                        failures++;
                        Console.WriteLine($"  batch failed: {ex.Message}");
                    }
                }
            }
            Report($"with {model}", sample, preds, usage);
            if (failures > 0) Console.WriteLine($"  failed batches: {failures}");
            RuleReport(data, sample, hidden, preds, rules);
        }
        if (opts.Models.Length == 0) RuleReport(data, sample, hidden, det, []);
    }

    private static string Purpose(Transaction t) => SepaPurpose.Get(t.Purpose, "SVWZ") ?? t.Purpose ?? "";

    private static AssistBookingSource Source(OwnerData o, Transaction t) =>
        AssistSettingsService.ToSource(t, t.AccountId is { } a && o.Accounts.TryGetValue(a, out var acc) ? acc.KindLabel : null);

    private static List<long> Top3(List<Neighbour> neighbours, long? first)
    {
        var ranked = neighbours.GroupBy(n => n.Booking.CategoryId).OrderByDescending(g => g.Sum(n => n.Similarity)).Select(g => g.Key).ToList();
        if (first is { } f) { ranked.Remove(f); ranked.Insert(0, f); }
        return ranked.Take(3).ToList();
    }

    private static void Report(string title, List<Item> sample, Dictionary<long, Prediction> preds, Usage? usage)
    {
        var n = sample.Count;
        int Correct(IEnumerable<Item> items) => items.Count(s => preds[s.Tx.Id].Category == s.Truth);
        var covered = sample.Where(s => preds[s.Tx.Id].Category != null).ToList();
        Console.WriteLine($"\n-- {title}");
        Console.WriteLine($"  accuracy (all)      {Pct(Correct(sample), n)}  coverage {Pct(covered.Count, n)}  accuracy (covered) {Pct(Correct(covered), covered.Count)}");
        Console.WriteLine($"  top-3 (all)         {Pct(sample.Count(s => preds[s.Tx.Id].Top3.Contains(s.Truth)), n)}");
        foreach (var g in covered.GroupBy(s => preds[s.Tx.Id].Source).OrderBy(g => g.Key))
            Console.WriteLine($"  source {g.Key,-6}       n={g.Count(),4}  accuracy {Pct(Correct(g), g.Count())}");
        Console.WriteLine("  calibration (confidence bucket: n, accuracy, mean confidence)");
        foreach (var (lo, hi) in new[] { (0.0, 0.5), (0.5, 0.7), (0.7, 0.8), (0.8, 0.9), (0.9, 1.01) })
        {
            var b = covered.Where(s => preds[s.Tx.Id].Confidence >= lo && preds[s.Tx.Id].Confidence < hi).ToList();
            if (b.Count == 0) continue;
            Console.WriteLine($"    [{lo:0.0},{Math.Min(hi, 1):0.0})  n={b.Count,4}  acc {Pct(Correct(b), b.Count)}  conf {b.Average(s => preds[s.Tx.Id].Confidence):0.00}");
        }
        var sure = covered.Where(s => preds[s.Tx.Id].Confidence >= 0.8).ToList();
        Console.WriteLine($"  'Accept all >= 0.8': {Pct(sure.Count, n)} of bookings, precision {Pct(Correct(sure), sure.Count)}");
        // Error examples as category pairs only (no booking data).
        var errors = covered.Where(s => preds[s.Tx.Id].Category != s.Truth)
            .GroupBy(s => (Truth: s.Owner.Valid[s.Truth].Path, Pred: s.Owner.Valid.TryGetValue(preds[s.Tx.Id].Category!.Value, out var o) ? o.Path : "?"))
            .OrderByDescending(g => g.Count()).Take(5);
        Console.WriteLine("  most frequent confusions (true → suggested):");
        foreach (var e in errors) Console.WriteLine($"    {e.Count(),3}× {e.Key.Truth} → {e.Key.Pred}");
        if (usage != null)
            Console.WriteLine($"  usage: {usage.Calls} calls, {usage.Input} input tokens ({usage.Cached} cached), {usage.Output} output, {usage.Seconds / Math.Max(1, usage.Calls):0.0} s per call");
    }

    private static void RuleReport(List<OwnerData> data, List<Item> sample, HashSet<long> hidden, Dictionary<long, Prediction> preds, List<(OwnerData Owner, string Expression, long Category)> aiRules)
    {
        var candidates = aiRules.Select(r => (r.Owner, r.Expression, r.Category, Source: "ai")).ToList();
        foreach (var g in sample.Where(s => preds[s.Tx.Id] is { Category: not null, Source: not "ai" } && !string.IsNullOrWhiteSpace(s.Tx.Name))
                     .GroupBy(s => (s.Owner, Name: s.Tx.Name!.Trim(), preds[s.Tx.Id].Category!.Value)))
            if (g.Count() >= 3 && !g.Key.Name.Contains('"')) candidates.Add((g.Key.Owner, $"name:\"{g.Key.Name}\"", g.Key.Value, "knn"));
        var offered = 0;
        var wrongRules = 0;
        var hiddenMatched = 0;
        var hiddenWrong = 0;
        var invalid = 0;
        foreach (var c in candidates.DistinctBy(c => (c.Owner.Owner, c.Expression)))
        {
            var node = c.Expression.Contains("ACC_") || c.Expression.Contains("PERSON_") ? null : RuleExpression.TryParse(c.Expression, out _);
            if (node == null || !c.Owner.Valid.ContainsKey(c.Category)) { invalid++; continue; }
            var ibans = c.Owner.Accounts.ToDictionary(a => a.Key, a => a.Value.Iban);
            var matched = c.Owner.Txs.Where(t => RuleExpression.Evaluate(node, RuleSubject.Of(t, t.AccountId is { } a ? ibans.GetValueOrDefault(a) : null))).ToList();
            bool Visible(Transaction t) => t.CategoryId is { } id && c.Owner.Valid.ContainsKey(id) && !hidden.Contains(t.Id);
            var conflicts = matched.Count(t => Visible(t) && t.CategoryId != c.Category);
            var uncategorized = matched.Count(t => !Visible(t));
            var rule = new RuleSuggestion(node.Print(), c.Category, "", matched.Count, uncategorized, Enumerable.Repeat(0L, conflicts).ToList(), c.Source);
            if (!CategorySuggestionService.Offerable(rule)) continue;
            offered++;
            var h = matched.Where(t => hidden.Contains(t.Id)).ToList();
            hiddenMatched += h.Count;
            var wrong = h.Count(t => t.CategoryId != c.Category);
            hiddenWrong += wrong;
            if (wrong > 0) wrongRules++;
        }
        Console.WriteLine($"  rules: {candidates.Count} candidates, {invalid} invalid, {offered} offered; offered rules with a wrong hidden booking {Pct(wrongRules, offered)}; " +
            $"hidden bookings they categorize {hiddenMatched}, wrong {Pct(hiddenWrong, hiddenMatched)}");
    }

    public static string Pct(int a, int b) => b == 0 ? "  n/a" : $"{100.0 * a / b,5:0.0}%";
}

internal static class BudgetEval
{
    public static async Task RunAsync(List<OwnerData> data, List<string> names, Args opts)
    {
        var year = opts.BudgetYear;
        var asOf = new DateOnly(year, 1, 1);
        Console.WriteLine($"\n== Budget backtest {year}: suggestion from data before {asOf:yyyy-MM-dd} vs. actuals");
        // Only categories that some budget tracks (as a category row): those are what "Suggest budget" is used for.
        await using var db = new BudgetDbContext(new DbContextOptionsBuilder<BudgetDbContext>().UseSqlite($"Data Source={opts.Db};Mode=ReadOnly").Options);
        var budgeted = (await db.MonthlyBudgetEntries.AsNoTracking().Where(e => e.ItemType == CategoryItemType.Category).Select(e => e.CategoryId).ToListAsync()).ToHashSet();
        var cases = new List<(OwnerData Owner, BudgetSuggestionInput Input, BudgetBaseline Baseline, Dictionary<long, Transaction> Rows, decimal Actual, decimal LastYear)>();
        foreach (var o in data)
        {
            foreach (var g in o.Categorized.Where(t => budgeted.Contains(t.CategoryId!.Value)).GroupBy(t => t.CategoryId!.Value))
            {
                var income = o.IncomeCategories.Contains(g.Key);
                var sign = income ? 1m : -1m;
                var bookings = g.Select(t => new BaselineBooking(t.Id, DateOnly.FromDateTime(t.Date), sign * t.Amount / 100m, CategoryMatching.PayeeKey(t.Name))).ToList();
                var payees = bookings.Select(b => b.PayeeKey).ToHashSet();
                var schedules = o.Scheduled.Where(s => s.IntervalUnit == OpenBudget.Connectors.ScheduleUnit.Month)
                    .Select(s => new BaselineSchedule(CategoryMatching.PayeeKey(s.CounterpartyName), sign * s.Amount / 100m, s.IntervalCount ?? 1, s.NextDate))
                    .Where(s => payees.Contains(s.PayeeKey)).ToList();
                var baseline = BudgetBaselineMath.Compute(bookings, schedules, year, asOf);
                var actual = bookings.Where(b => b.Date.Year == year).Sum(b => b.Amount);
                var last = bookings.Where(b => b.Date.Year == year - 1).Sum(b => b.Amount);
                if (baseline.MonthsOfData < 12 || Math.Max(Math.Abs(actual), Math.Abs(baseline.Monthly.Sum())) < 100) continue;
                cases.Add((o, new BudgetSuggestionInput((g.Key, CategoryItemType.Category), o.Valid[g.Key].Path, income), baseline, g.ToDictionary(t => t.Id), actual, last));
            }
        }
        Console.WriteLine($"  categories: {cases.Count} (budgeted, ≥ 12 months of data, ≥ 100 € a year)");
        Summary("last year's total", cases.Select(c => (c.LastYear, c.Actual)).ToList());
        Summary("baseline", cases.Select(c => (c.Baseline.Monthly.Sum(), c.Actual)).ToList());
        Monthly("baseline", cases.Select(c => (c.Baseline.Monthly, Actuals(c.Rows.Values, c.Input.IsIncome, year))).ToList());
        Console.WriteLine("  largest baseline errors (basis, months of data, recurring series, outliers; predicted/actual as share of last year):");
        foreach (var c in cases.OrderByDescending(c => Math.Abs(c.Baseline.Monthly.Sum() - c.Actual)).Take(8))
            Console.WriteLine($"    {c.Baseline.Basis,-9} {c.Baseline.MonthsOfData,2} mo  rec {c.Baseline.Recurring.Count(r => r.IntervalMonths > 1)}/{c.Baseline.Recurring.Count}  out {c.Baseline.Outliers.Count}  " +
                $"pred {Ratio(c.Baseline.Monthly.Sum(), c.LastYear)}  actual {Ratio(c.Actual, c.LastYear)}  income {c.Input.IsIncome}");

        foreach (var model in opts.Models)
        {
            var client = Ai.Client(model);
            var suggested = new decimal[cases.Count][];
            var flagged = 0;
            var usage = new Usage(0, 0, 0, 0, 0);
            var index = 0;
            foreach (var chunk in cases.Chunk(15))
            {
                var redactor = new AssistRedactor(names);
                var list = chunk.Select(c => (c.Input, c.Baseline, (IReadOnlyDictionary<long, Transaction>)c.Rows)).ToList();
                var (request, handles) = BudgetSuggestionService.BuildRequest(year, list, redactor);
                var rows = chunk.Select(c => new BudgetSuggestionRow(c.Input.Key, c.Input.Name, c.Input.IsIncome, c.Baseline.Monthly, (decimal[])c.Baseline.Monthly.Clone(),
                    c.Baseline.Basis, c.Baseline.Outliers.Select(x => new BudgetOutlier(x.Id, x.Date, x.Amount, null, "one_off", null, true)).ToList(), null, false, c.Baseline.MonthsOfData)).ToList();
                try
                {
                    var response = await client.CompleteAsync(request);
                    usage = usage with { Calls = usage.Calls + 1, Input = usage.Input + response.InputTokens, Output = usage.Output + response.OutputTokens, Seconds = usage.Seconds + response.Elapsed.TotalSeconds };
                    rows = BudgetSuggestionService.Merge(response, redactor, handles, rows);
                }
                catch (AssistException ex)
                {
                    Console.WriteLine($"  batch failed: {ex.Message}");
                }
                foreach (var r in rows)
                {
                    suggested[index++] = r.Suggested;
                    if (r.Flagged) flagged++;
                }
            }
            Summary($"AI-adjusted ({model})", cases.Select((c, i) => (suggested[i].Sum(), c.Actual)).ToList());
            Monthly($"AI-adjusted ({model})", cases.Select((c, i) => (suggested[i], Actuals(c.Rows.Values, c.Input.IsIncome, year))).ToList());
            Console.WriteLine($"  flagged >30 % off baseline: {flagged}; usage: {usage.Calls} calls, {usage.Input} in, {usage.Output} out, {usage.Seconds / Math.Max(1, usage.Calls):0.0} s per call");
        }
    }

    private static string Ratio(decimal a, decimal b) => b == 0 ? "n/a" : (a / b).ToString("0.00", CultureInfo.InvariantCulture);

    private static decimal[] Actuals(IEnumerable<Transaction> rows, bool income, int year)
    {
        var m = new decimal[12];
        foreach (var t in rows.Where(t => t.Date.Year == year)) m[t.Date.Month - 1] += (income ? 1 : -1) * t.Amount / 100m;
        return m;
    }

    private static void Summary(string title, List<(decimal Predicted, decimal Actual)> pairs)
    {
        var apes = pairs.Select(p => (double)(Math.Abs(p.Predicted - p.Actual) / Math.Max(Math.Abs(p.Actual), 1m))).Order().ToList();
        var capped = apes.Select(a => Math.Min(a, 2.0)).ToList();
        var totalErr = pairs.Sum(p => Math.Abs(p.Predicted - p.Actual)) / Math.Max(1m, pairs.Sum(p => Math.Abs(p.Actual)));
        Console.WriteLine($"  {title,-34} yearly APE median {100 * apes[apes.Count / 2]:0.0}%  mean (capped 200%) {100 * capped.Average():0.0}%  " +
            $"within 20% {100.0 * apes.Count(a => a <= 0.2) / apes.Count:0}%  weighted {100 * totalErr:0.0}%");
    }

    private static void Monthly(string title, List<(decimal[] Predicted, decimal[] Actual)> pairs)
    {
        var err = pairs.Sum(p => Enumerable.Range(0, 12).Sum(i => Math.Abs(p.Predicted[i] - p.Actual[i])));
        var tot = pairs.Sum(p => p.Actual.Sum(Math.Abs));
        Console.WriteLine($"  {title,-34} monthly weighted abs error {100 * err / Math.Max(1m, tot):0.0}%");
    }
}
