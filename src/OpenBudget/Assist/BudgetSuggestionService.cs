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

/// <summary>
/// "Suggest budget": a deterministic baseline per row (<see cref="BudgetBaselineMath"/>) that works without AI. With
/// AI on (configured and opted in by the user and the data's owner), the model classifies the outliers (one-off,
/// recurring, refund), may adjust the months and writes a short explanation per row; the server keeps the AI's
/// months only when there are 12 non-negative ones and flags a year total more than 30 % off the baseline.
/// </summary>
public sealed class BudgetSuggestionService
{
    public const double FlagDeviation = 0.3;
    public const string SchemaName = "budget_suggestions";

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
        var schedules = await db.ScheduledPayments.AsNoTracking()
            .Where(p => p.OwnerUserId == ownerId && p.Kind == ScheduledPaymentKind.Standing && p.IntervalUnit == ScheduleUnit.Month)
            .ToListAsync(ct);

        var computed = new List<(BudgetSuggestionInput Input, BudgetBaseline Baseline, Dictionary<long, Transaction> Rows)>();
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
        }

        var result = computed.Select(c => new BudgetSuggestionRow(
            c.Input.Key, c.Input.Name, c.Input.IsIncome, c.Baseline.Monthly, (decimal[])c.Baseline.Monthly.Clone(), c.Baseline.Basis,
            c.Baseline.Outliers.Select(o => new BudgetOutlier(o.Id, o.Date, o.Amount, c.Rows[o.Id].Name, "one_off", null, true)).ToList(),
            null, false, c.Baseline.MonthsOfData)).ToList();

        string? aiError = null;
        var aiUsed = false;
        if (useAi && _client.Enabled && await _settings.GetOptInAsync(ct) && (await _settings.OptedInAsync([ownerId], ct)).Contains(ownerId))
        {
            try
            {
                result = await AskAiAsync(year, computed, result, ct);
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

    private async Task<List<BudgetSuggestionRow>> AskAiAsync(
        int year, List<(BudgetSuggestionInput Input, BudgetBaseline Baseline, Dictionary<long, Transaction> Rows)> computed,
        List<BudgetSuggestionRow> rows, CancellationToken ct)
    {
        var redactor = new AssistRedactor(await _settings.PersonNamesAsync(ct));
        var (request, handles) = BuildRequest(year, computed.Select(c => (c.Input, c.Baseline, (IReadOnlyDictionary<long, Transaction>)c.Rows)).ToList(), redactor);
        var response = await _client.CompleteAsync(request, ct);
        _log.LogInformation("AI budget suggestion for {Count} rows ({Input} input tokens, {Output} output, {Ms} ms)",
            computed.Count, response.InputTokens, response.OutputTokens, (int)response.Elapsed.TotalMilliseconds);
        return Merge(response, redactor, handles, rows);
    }

    /// <summary>The request for a set of rows (redacted) and which handle ("c1") stands for which row.</summary>
    public static (AssistRequest Request, Dictionary<string, int> Handles) BuildRequest(
        int year, IReadOnlyList<(BudgetSuggestionInput Input, BudgetBaseline Baseline, IReadOnlyDictionary<long, Transaction> Rows)> computed, AssistRedactor redactor)
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
                ["recurring"] = new JsonArray(baseline.Recurring.Select(r => (JsonNode)new JsonObject
                {
                    ["payee"] = redactor.CleanText(r.PayeeKey, [], 60),
                    ["every_months"] = r.IntervalMonths,
                    ["amount"] = r.Amount,
                }).ToArray()),
                ["outliers"] = new JsonArray(baseline.Outliers.Select(o =>
                    JsonSerializer.SerializeToNode(redactor.Redact(AssistSettingsService.ToSource(txRows[o.Id], null)), CategoryPrompt.Json)).ToArray()),
            });
        }
        var system = $"""
            You help a German household plan a yearly budget ({year.ToString(CultureInfo.InvariantCulture)}) per category.
            For each category you get a statistical baseline (12 months, in EUR, positive = expected spending or for income
            categories expected income), how it was computed, recurring payments and unusual single bookings (outliers) of
            the last years. Classify every outlier: one_off (will not repeat), recurring (will happen again in the same
            month) or refund (money back). Return 12 monthly amounts: start from the baseline and only change it with a
            reason (recurring outliers, an obvious trend). basis: one of median, seasonal, recurring, trend, none.
            explanation: one or two short sentences in German for the household, no amounts of single bookings.
            Tokens like PERSON_A stand for household members, ACC_1 for an account, # for removed digits.
            """;
        return (new AssistRequest(SchemaName, Schema(), system, "Categories:\n" + items.ToJsonString(CategoryPrompt.Json)), handles);
    }

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
