using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;
using OpenBudget.Services;

namespace OpenBudget.Assist;

/// <summary>
/// A category suggestion for one booking. <see cref="Source"/>: rule, iban, knn, ai or order (from the items of an
/// online order). With <see cref="Split"/> the booking is to be split into those parts (<see cref="CategoryId"/> is then
/// the largest part's category).
/// </summary>
public sealed record TxCategorySuggestion(long TransactionId, long? CategoryId, string? CategoryPath, double Confidence, string Source, string? Rationale)
{
    public IReadOnlyList<SuggestedSplitPart>? Split { get; init; }

    /// <summary>The order was shipped in parts and the charge could not be tied to one: the split covers the whole order.</summary>
    public bool ShipmentUnclear { get; init; }
}

/// <summary>One part of a suggested split: signed cents, category and the items it stands for.</summary>
public sealed record SuggestedSplitPart(long AmountCents, long CategoryId, string? CategoryPath, string Label, IReadOnlyList<long> ItemIds);

/// <summary>A rule worth creating, with what it would match: <see cref="Matches"/> bookings, of which
/// <see cref="Uncategorized"/> have no category and <see cref="ConflictIds"/> already have a different one.</summary>
public sealed record RuleSuggestion(string Expression, long CategoryId, string CategoryPath, int Matches, int Uncategorized, IReadOnlyList<long> ConflictIds, string Source)
{
    public int Conflicts => ConflictIds.Count;

    /// <summary>The conflicting bookings for display: date, payee, amount and their current category.</summary>
    public IReadOnlyList<string> ConflictLabels { get; init; } = [];
}

/// <summary>What a suggestion run produced. <see cref="AiError"/> says why the AI part failed (the rest still counts).</summary>
public sealed record CategorySuggestionResult(
    IReadOnlyList<TxCategorySuggestion> Suggestions, IReadOnlyList<RuleSuggestion> Rules, string? AiError, int SentToAi, int SkippedNoConsent);

/// <summary>
/// Suggests categories for uncategorized bookings of a project: the project's rules first, then a counterparty IBAN
/// whose bookings mostly have one category, then similar categorized bookings (kNN); only what stays unsure goes to the
/// AI, when the server has one configured and both the asking user and the booking's owner opted in. Nothing is applied
/// and no rule is created without the user accepting it. Results are cached per booking and category tree. A category
/// restricted to an account or account group is never suggested for a booking outside it.
/// </summary>
public sealed partial class CategorySuggestionService
{
    /// <summary>Deterministic suggestions at least this sure are not sent to the AI.</summary>
    public const double SureEnough = 0.75;
    public const int BatchSize = 25;
    public const int MinRuleMatches = 2;
    public const double MaxRuleConflictShare = 0.05;
    private const int MaxExampleRules = 30;

    private readonly IDbContextFactory<BudgetDbContext> _dbFactory;
    private readonly SystemDbContextFactory _system;
    private readonly CategorizationService _categorization;
    private readonly CategoryRuleService _rules;
    private readonly BudgetService _budgets;
    private readonly TransactionEditService _edits;
    private readonly AssistSettingsService _settings;
    private readonly IAssistClient _client;
    private readonly ILogger<CategorySuggestionService> _log;
    private readonly TimeProvider _clock;
    private readonly SplitService? _splits;
    private readonly IDecisionsClient? _decisions;
    private readonly AssistOptions? _options;

    public CategorySuggestionService(
        IDbContextFactory<BudgetDbContext> dbFactory, SystemDbContextFactory system, CategorizationService categorization, CategoryRuleService rules,
        BudgetService budgets, TransactionEditService edits, AssistSettingsService settings, IAssistClient client,
        ILogger<CategorySuggestionService> log, TimeProvider clock, SplitService? splits = null,
        IDecisionsClient? decisions = null, AssistOptions? options = null)
    {
        _decisions = decisions;
        _options = options;
        _splits = splits;
        _dbFactory = dbFactory;
        _system = system;
        _categorization = categorization;
        _rules = rules;
        _budgets = budgets;
        _edits = edits;
        _settings = settings;
        _client = client;
        _log = log;
        _clock = clock;
    }

    private sealed record Context(
        string OwnerId, List<PromptCategory> Categories, Dictionary<long, PromptCategory> ById, string Hash,
        Dictionary<long, Transaction> Rows, Dictionary<long, string?> AccountKinds, Dictionary<long, string?> AccountIbans, List<CategoryRule> ProjectRules,
        CategoryRestrictions Restrictions);

    private static bool Allowed(Context ctx, long? categoryId, long? accountId) =>
        categoryId is not { } c || ctx.Restrictions.Allows(c, accountId);

    private static bool Allowed(Context ctx, TxCategorySuggestion s) =>
        ctx.Rows.TryGetValue(s.TransactionId, out var row)
        && Allowed(ctx, s.CategoryId, row.AccountId)
        && (s.Split == null || s.Split.All(p => Allowed(ctx, p.CategoryId, row.AccountId)));

    /// <summary>
    /// Suggestions for the given uncategorized bookings of the project (null: all of them). Progress reports
    /// (done, total) of the AI batches.
    /// </summary>
    public async Task<CategorySuggestionResult> SuggestAsync(
        int projectId, IReadOnlyCollection<long>? transactionIds, bool useAi, IProgress<(int Done, int Total)>? progress = null, CancellationToken ct = default)
    {
        var ctx = await LoadAsync(projectId, ct);
        if (ctx == null) return new([], [], null, 0, 0);

        var history = ctx.Rows.Values.Where(t => t.CategoryId is { } c && ctx.ById.ContainsKey(c)).ToList();
        var targets = ctx.Rows.Values.Where(t => t.Amount != 0 && (t.CategoryId is not { } c || !ctx.ById.ContainsKey(c)))
            .Where(t => transactionIds == null || transactionIds.Contains(t.Id))
            .OrderByDescending(t => t.Timestamp)
            .ToList();
        var matching = new CategoryMatching(history.Select(h => ToHistory(h, h.CategoryId!.Value)));

        var cached = await CachedAsync(targets.Select(t => t.Id).ToList(), ctx.Hash, ct);
        var results = new Dictionary<long, TxCategorySuggestion>();
        var unsure = new List<(Transaction Row, TargetBooking Target, List<Neighbour> Neighbours)>();
        foreach (var t in targets)
        {
            if (cached.TryGetValue(t.Id, out var hit) && (hit.Source != "ai" || useAi))
            {
                var present = Present(ctx, hit.TransactionId, hit.CategoryId, hit.Confidence, hit.Source, hit.Rationale, hit.SplitJson);
                if (Allowed(ctx, present))
                {
                    results[t.Id] = present;
                    continue;
                }
                cached.Remove(t.Id);
            }
            var target = ToTarget(t);
            var iban = matching.IbanVote(target);
            if (iban != null && !Allowed(ctx, iban.CategoryId, t.AccountId)) iban = null;
            var vote = RuleVote(ctx, t) ?? iban;
            var neighbours = matching.Neighbours(target).Where(n => Allowed(ctx, n.Booking.CategoryId, t.AccountId)).ToList();
            if (vote == null || vote.Confidence < SureEnough)
            {
                var knn = CategoryMatching.KnnVote(neighbours);
                if (knn != null && (vote == null || knn.Confidence > vote.Confidence)) vote = knn;
            }
            if (vote != null) results[t.Id] = Present(ctx, t.Id, vote.CategoryId, vote.Confidence, vote.Source, vote.Rationale);
            if (vote == null || vote.Confidence < SureEnough) unsure.Add((t, target, neighbours));
        }

        string? aiError = null;
        var sent = 0;
        var skipped = 0;
        var orderTargets = targets.Where(t => !string.IsNullOrEmpty(t.OrderItems) && !(cached.TryGetValue(t.Id, out var c) && (c.Source != "ai" || useAi))).ToList();
        if (orderTargets.Count > 0)
        {
            try
            {
                var (orderSent, orderSkipped) = await OrderSuggestionsAsync(ctx, orderTargets, useAi, results, ct);
                sent += orderSent;
                skipped += orderSkipped;
                // Bookings with an order suggestion need no AI guess from their payee.
                unsure.RemoveAll(u => results.TryGetValue(u.Row.Id, out var r) && r.Source == "order");
            }
            catch (AssistException ex)
            {
                aiError = ex.Message;
                _log.LogWarning(ex, "AI item categories failed");
            }
        }
        var aiRules = new List<(string Expression, long CategoryId)>();
        if (useAi && _client.Enabled && unsure.Count > 0)
        {
            var allowed = await AllowedOwnersAsync(ct);
            var eligible = unsure.Where(u => allowed.Contains(u.Row.OwnerUserId)).ToList();
            skipped = unsure.Count - eligible.Count;
            if (eligible.Count > 0)
            {
                try
                {
                    sent = UseDecisions
                        ? await AskDecisionsAsync(ctx, eligible, results, progress, ct)
                        : await AskAiAsync(ctx, eligible, results, aiRules, progress, ct);
                }
                catch (AssistException ex)
                {
                    aiError = ex.Message;
                    _log.LogWarning(ex, "AI category suggestions failed");
                }
            }
        }

        foreach (var id in results.Where(r => !Allowed(ctx, r.Value)).Select(r => r.Key).ToList()) results.Remove(id);
        await StoreAsync(results.Values.Where(r => !cached.ContainsKey(r.TransactionId)), ctx.Hash, ct);
        var rules = await RuleSuggestionsAsync(projectId, ctx, targets, results, aiRules, ct);
        var ordered = targets.Where(t => results.ContainsKey(t.Id)).Select(t => results[t.Id]).ToList();
        return new CategorySuggestionResult(ordered, rules, aiError, sent, skipped);
    }

    /// <summary>Owners whose bookings may be sent: everybody who opted in, but nobody unless the asking user did.</summary>
    private async Task<HashSet<string>> AllowedOwnersAsync(CancellationToken ct)
    {
        if (!await _settings.GetOptInAsync(ct)) return [];
        await using var db = await _system.CreateDbContextAsync(ct);
        return await _settings.OptedInAsync(await db.UserDataStates.AsNoTracking().Select(s => s.UserId).ToListAsync(ct), ct);
    }

    private async Task<int> AskAiAsync(
        Context ctx, List<(Transaction Row, TargetBooking Target, List<Neighbour> Neighbours)> items,
        Dictionary<long, TxCategorySuggestion> results, List<(string, long)> aiRules, IProgress<(int, int)>? progress, CancellationToken ct)
    {
        var examples = ctx.ProjectRules.Where(r => r.Enabled && ctx.ById.ContainsKey(r.CategoryId))
            .Take(MaxExampleRules)
            .Select(r => RuleExpression.TryParse(r.Expression, out _)?.Print() + " => " + ctx.ById[r.CategoryId].Handle)
            .Where(s => !s.Contains("account_id", StringComparison.Ordinal) && !s.Contains("_account", StringComparison.Ordinal))
            .ToList();
        var system = CategoryPrompt.SystemPrompt(ctx.Categories, examples);
        var handles = ctx.Categories.ToDictionary(c => c.Handle, StringComparer.Ordinal);
        var names = await _settings.PersonNamesAsync(ct);
        var batches = items.Chunk(BatchSize).ToList();
        var done = 0;
        progress?.Report((0, items.Count));
        foreach (var batch in batches)
        {
            var redactor = new AssistRedactor(names);
            var prompt = batch.Select(b => new PromptItem(
                redactor.Redact(AssistSettingsService.ToSource(b.Row, ctx.AccountKinds.GetValueOrDefault(b.Row.AccountId ?? 0))),
                b.Neighbours.Take(3).Select(n => (
                    redactor.Redact(AssistSettingsService.ToSource(ctx.Rows[n.Booking.Id], ctx.AccountKinds.GetValueOrDefault(ctx.Rows[n.Booking.Id].AccountId ?? 0))),
                    ctx.ById[n.Booking.CategoryId].Handle)).ToList())).ToList();
            var response = await _client.CompleteAsync(new AssistRequest(CategoryPrompt.SchemaName, CategoryPrompt.Schema(), system, CategoryPrompt.UserContent(prompt)), ct);
            _log.LogInformation("AI categorized {Count} bookings ({Input} input tokens, {Cached} cached, {Output} output, {Ms} ms)",
                batch.Length, response.InputTokens, response.CachedTokens, response.OutputTokens, (int)response.Elapsed.TotalMilliseconds);
            foreach (var s in CategoryPrompt.ParseAnswer(response, redactor, handles))
            {
                if (s.CategoryId == null) continue;
                if (!ctx.Rows.TryGetValue(s.TransactionId, out var asked) || !Allowed(ctx, s.CategoryId, asked.AccountId)) continue;
                if (results.TryGetValue(s.TransactionId, out var existing) && existing.Confidence >= s.Confidence) continue;
                results[s.TransactionId] = Present(ctx, s.TransactionId, s.CategoryId, s.Confidence, "ai", s.Rationale);
                if (s.RuleExpression != null) aiRules.Add((s.RuleExpression, s.CategoryId.Value));
            }
            done += batch.Length;
            progress?.Report((done, items.Count));
        }
        return items.Count;
    }

    /// <summary>Categories are chosen with the Decisions API (no rationale, no AI rule proposals).</summary>
    private bool UseDecisions => _options?.CategoryRoute == CategoryRoute.Decisions && _decisions is { Enabled: true };

    private async Task<int> AskDecisionsAsync(
        Context ctx, List<(Transaction Row, TargetBooking Target, List<Neighbour> Neighbours)> items,
        Dictionary<long, TxCategorySuggestion> results, IProgress<(int, int)>? progress, CancellationToken ct)
    {
        var redactor = new AssistRedactor(await _settings.PersonNamesAsync(ct));
        var requests = items.Select(b => new DecisionItem(b.Row.Id,
            new PromptItem(
                redactor.Redact(AssistSettingsService.ToSource(b.Row, ctx.AccountKinds.GetValueOrDefault(b.Row.AccountId ?? 0))),
                b.Neighbours.Take(3).Select(n => (
                    redactor.Redact(AssistSettingsService.ToSource(ctx.Rows[n.Booking.Id], ctx.AccountKinds.GetValueOrDefault(ctx.Rows[n.Booking.Id].AccountId ?? 0))),
                    ctx.ById[n.Booking.CategoryId].Handle)).ToList()),
            ctx.Categories.Where(c => Allowed(ctx, c.Id, b.Row.AccountId)).ToList())).ToList();
        progress?.Report((0, items.Count));
        var answers = await new DecisionsCategoryChooser(_decisions!).ChooseAsync(requests, new SyncProgress(d => progress?.Report((d, items.Count))), ct);
        _log.LogInformation("AI decided {Count} bookings ({Input} input tokens, {Cached} cached, {Ms} ms per booking)",
            answers.Count, answers.Sum(a => a.Response.InputTokens), answers.Sum(a => a.Response.CachedTokens),
            answers.Count == 0 ? 0 : (int)answers.Average(a => a.Response.Elapsed.TotalMilliseconds));
        foreach (var (s, _) in answers)
        {
            if (s == null || !ctx.Rows.TryGetValue(s.TransactionId, out var asked) || !Allowed(ctx, s.CategoryId, asked.AccountId)) continue;
            if (results.TryGetValue(s.TransactionId, out var existing) && existing.Confidence >= s.Probability) continue;
            var second = s.SecondCategoryId is { } c && ctx.ById.TryGetValue(c, out var cat) ? cat.Path : null;
            results[s.TransactionId] = Present(ctx, s.TransactionId, s.CategoryId, s.Probability, "ai", DecisionsCategoryChooser.Rationale(s.Probability, second));
        }
        return items.Count;
    }

    /// <summary>Reports synchronously (Progress&lt;T&gt; would post to a captured context, out of order).</summary>
    private sealed class SyncProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }

    // ---------------------------------------------------------------- rules

    private async Task<List<RuleSuggestion>> RuleSuggestionsAsync(
        int projectId, Context ctx, List<Transaction> targets, Dictionary<long, TxCategorySuggestion> results, List<(string Expression, long CategoryId)> aiRules, CancellationToken ct)
    {
        var candidates = new List<(string Expression, long CategoryId, string Source)>();
        candidates.AddRange(aiRules.Select(r => (r.Expression, r.CategoryId, "ai")));
        // At least three uncategorized bookings with the same payee and the same suggested category.
        foreach (var g in targets.Where(t => results.TryGetValue(t.Id, out var s) && s.CategoryId != null && s.Source != "rule" && !string.IsNullOrWhiteSpace(t.Name))
                     .GroupBy(t => (Name: t.Name!.Trim(), results[t.Id].CategoryId!.Value)))
        {
            if (g.Count() < 3 || g.Key.Name.Contains('"', StringComparison.Ordinal)) continue;
            candidates.Add(($"name:\"{g.Key.Name}\"", g.Key.Value, "knn"));
        }
        var existing = ctx.ProjectRules.Select(r => RuleExpression.TryParse(r.Expression, out _)?.Print()).Where(e => e != null).ToHashSet(StringComparer.Ordinal);
        var list = new List<RuleSuggestion>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (expression, categoryId, source) in candidates)
        {
            if (await CheckRuleAsync(projectId, expression, categoryId, source, ctx, ct) is not { } check) continue;
            if (existing.Contains(check.Expression) || !seen.Add(check.Expression)) continue;
            if (Offerable(check)) list.Add(check);
        }
        return list.OrderByDescending(r => r.Uncategorized).ToList();
    }

    /// <summary>A rule is offered when it matches at least two bookings and at most 5 % of them have another category.</summary>
    public static bool Offerable(RuleSuggestion r) =>
        r.Matches >= MinRuleMatches && r.Uncategorized > 0 && r.Conflicts <= Math.Floor(r.Matches * MaxRuleConflictShare);

    /// <summary>
    /// Parses and validates a rule and counts what it would match. Null when it does not parse, is not valid or uses
    /// redaction tokens. The expression comes back in canonical form.
    /// </summary>
    public async Task<RuleSuggestion?> CheckRuleAsync(int projectId, string expression, long categoryId, string source, CancellationToken ct = default)
    {
        var ctx = await LoadAsync(projectId, ct);
        return ctx == null ? null : await CheckRuleAsync(projectId, expression, categoryId, source, ctx, ct);
    }

    private async Task<RuleSuggestion?> CheckRuleAsync(int projectId, string expression, long categoryId, string source, Context ctx, CancellationToken ct)
    {
        if (!ctx.ById.TryGetValue(categoryId, out var category)) return null;
        if (ContainsRedactionToken(expression)) return null;
        var node = RuleExpression.TryParse(expression, out _);
        if (node == null) return null;
        var canonical = node.Print();
        var draft = new CategoryRule { ProjectId = projectId, CategoryId = categoryId, Expression = canonical, Enabled = true };
        if (CategoryRuleService.Validate(draft) != RuleValidation.Ok) return null;
        var ids = await _rules.PreviewMatchIdsAsync(projectId, draft, ct);
        var uncategorized = 0;
        var conflicts = new List<long>();
        foreach (var id in ids)
        {
            var c = ctx.Rows.TryGetValue(id, out var row) ? row.CategoryId : null;
            if (c is not { } cid || !ctx.ById.ContainsKey(cid)) uncategorized++;
            else if (cid != categoryId) conflicts.Add(id);
        }
        return new RuleSuggestion(canonical, categoryId, category.Path, ids.Count, uncategorized, conflicts, source)
        {
            ConflictLabels = conflicts.Take(20).Select(id => ctx.Rows[id]).Select(r => string.Create(CultureInfo.CurrentCulture,
                $"{r.Date:dd.MM.yyyy} · {r.Name} · {Formatting.FormatMoneyDe(r.Amount / 100m)} · {ctx.ById[r.CategoryId!.Value].Path}")).ToList(),
        };
    }

    private static bool ContainsRedactionToken(string expression) =>
        expression.Contains("ACC_", StringComparison.Ordinal) || expression.Contains("PERSON_", StringComparison.Ordinal)
        || expression.Contains("[CARD]", StringComparison.Ordinal) || expression.Contains('#', StringComparison.Ordinal);

    /// <summary>Creates the rule (at the end of the project's rules) and applies it to uncategorized bookings. Returns how many got the category.</summary>
    public async Task<int> CreateRuleAndApplyAsync(int projectId, RuleSuggestion rule, CancellationToken ct = default)
    {
        await _rules.SaveAsync(new CategoryRule { ProjectId = projectId, CategoryId = rule.CategoryId, Expression = rule.Expression, Enabled = true }, ct);
        return await _rules.ApplyExistingAsync(projectId, rule.CategoryId, ct);
    }

    // ---------------------------------------------------------------- accept / reject

    /// <summary>Gives the bookings the category (as chosen by hand) and forgets their suggestions.</summary>
    public async Task<EditResult> AcceptAsync(IReadOnlyCollection<long> transactionIds, long categoryId, CancellationToken ct = default)
    {
        var result = await _edits.SetCategoryAsync(transactionIds, categoryId, ct);
        await ForgetAsync(transactionIds, ct);
        return result;
    }

    /// <summary>Drops the cached suggestions of the bookings (a rejected suggestion is not shown again until asked anew).</summary>
    public async Task ForgetAsync(IReadOnlyCollection<long> transactionIds, CancellationToken ct = default)
    {
        await using var db = await _system.CreateDbContextAsync(ct);
        await db.CategorySuggestions.Where(s => transactionIds.Contains(s.TransactionId)).ExecuteDeleteAsync(ct);
    }

    // ---------------------------------------------------------------- data

    private async Task<Context?> LoadAsync(int projectId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var ownerId = await db.Projects.AsNoTracking().Where(p => p.Id == projectId).Select(p => p.OwnerUserId).FirstOrDefaultAsync(ct);
        if (ownerId == null) return null;
        var picker = await _categorization.GetPickerDataAsync(ownerId, ct);
        var income = (await _budgets.GetIncomeItemsForProjectAsync(projectId)).Where(i => i.ItemType == CategoryItemType.Category).Select(i => i.Id).ToHashSet();
        var categories = picker.Options.Select((o, i) => new PromptCategory(CategoryPrompt.Handle(i + 1), o.Id, o.Path, income.Contains(o.Id))).ToList();

        var accounts = await db.Accounts.AsNoTracking()
            .Where(a => db.ProjectAccountAssignments.Any(p => p.ProjectId == projectId && p.AccountId == a.Id))
            .ToListAsync(ct);
        var accountIds = accounts.Select(a => a.Id).ToList();
        var rows = await db.Transactions.AsNoTracking()
            .Where(t => t.OwnerUserId == ownerId && t.AccountId.HasValue && accountIds.Contains(t.AccountId.Value) && t.Splitted != SplitState.Parent)
            .ToDictionaryAsync(t => t.Id, ct);
        var rules = await db.CategoryRules.AsNoTracking().Where(r => r.ProjectId == projectId && !r.Dismissed).OrderBy(r => r.SortOrder).ThenBy(r => r.Id).ToListAsync(ct);
        var restrictions = await CategoryRestrictions.LoadAsync(db, categories.Select(c => c.Id), accountIds, ct);
        return new Context(ownerId, categories, categories.ToDictionary(c => c.Id), TreeHash(categories), rows,
            accounts.ToDictionary(a => a.Id, a => (string?)a.KindLabel), accounts.ToDictionary(a => a.Id, a => a.Iban), rules, restrictions);
    }

    public static string TreeHash(IEnumerable<PromptCategory> categories)
    {
        var text = string.Join('\n', categories.OrderBy(c => c.Id).Select(c => $"{c.Id}|{c.Path}|{c.Income}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private static Vote? RuleVote(Context ctx, Transaction t)
    {
        var subject = RuleSubject.Of(t, ctx.AccountIbans.GetValueOrDefault(t.AccountId ?? 0), ctx.Restrictions.Groups.GroupsOf(t.AccountId));
        var rule = ctx.ProjectRules.FirstOrDefault(r => ctx.ById.ContainsKey(r.CategoryId) && CategoryRuleMatcher.Matches(r, subject, ctx.Restrictions));
        return rule == null ? null : new Vote(rule.CategoryId, rule.Enabled ? 0.95 : 0.8, "rule", rule.Name ?? rule.Expression);
    }

    private static HistoryBooking ToHistory(Transaction t, long categoryId) =>
        new(t.Id, categoryId, CategoryMatching.IbanKey(t.CounterpartyIban), CategoryMatching.PayeeKey(t.Name), CategoryMatching.Tokens(t.Name, PurposeOnly(t.Purpose)), t.Amount);

    private static TargetBooking ToTarget(Transaction t) =>
        new(t.Id, CategoryMatching.IbanKey(t.CounterpartyIban), CategoryMatching.PayeeKey(t.Name), CategoryMatching.Tokens(t.Name, PurposeOnly(t.Purpose)), t.Amount);

    private static string PurposeOnly(string? purpose) => SepaPurpose.Get(purpose, "SVWZ") ?? purpose ?? "";

    private static TxCategorySuggestion Present(Context ctx, long id, long? categoryId, double confidence, string source, string? rationale, string? splitJson = null)
    {
        var suggestion = new TxCategorySuggestion(id, categoryId, categoryId is { } c && ctx.ById.TryGetValue(c, out var cat) ? cat.Path : null, Math.Round(confidence, 2), source, rationale);
        if (splitJson == null) return suggestion;
        var stored = ReadSplit(splitJson);
        if (stored == null) return suggestion;
        if (stored.Parts.Any(p => !ctx.ById.ContainsKey(p.CategoryId))) return suggestion with { ShipmentUnclear = stored.Unclear };
        return suggestion with
        {
            Split = stored.Parts.Count == 0 ? null : stored.Parts.Select(p => p with { CategoryPath = ctx.ById[p.CategoryId].Path }).ToList(),
            ShipmentUnclear = stored.Unclear,
        };
    }

    private async Task<Dictionary<long, CategorySuggestion>> CachedAsync(List<long> ids, string treeHash, CancellationToken ct)
    {
        var result = new Dictionary<long, CategorySuggestion>();
        await using var db = await _system.CreateDbContextAsync(ct);
        foreach (var chunk in ids.Chunk(500))
            foreach (var s in await db.CategorySuggestions.AsNoTracking().Where(s => chunk.Contains(s.TransactionId) && s.TreeHash == treeHash).ToListAsync(ct))
                result[s.TransactionId] = s;
        return result;
    }

    private async Task StoreAsync(IEnumerable<TxCategorySuggestion> suggestions, string treeHash, CancellationToken ct)
    {
        var list = suggestions.ToList();
        if (list.Count == 0) return;
        await using var db = await _system.CreateDbContextAsync(ct);
        var ids = list.Select(s => s.TransactionId).ToList();
        foreach (var chunk in ids.Chunk(500))
            await db.CategorySuggestions.Where(s => chunk.Contains(s.TransactionId)).ExecuteDeleteAsync(ct);
        var now = _clock.GetUtcNow().UtcDateTime;
        db.CategorySuggestions.AddRange(list.Select(s => new CategorySuggestion
        {
            TransactionId = s.TransactionId,
            CategoryId = s.CategoryId,
            Confidence = s.Confidence,
            Source = s.Source,
            Rationale = s.Rationale is { Length: > 300 } r ? r[..300] : s.Rationale,
            Model = s.Source == "ai" ? (UseDecisions ? _decisions!.Model : _client.Model) : null,
            SplitJson = s.Split == null && !s.ShipmentUnclear ? null : WriteSplit(s),
            TreeHash = treeHash,
            CreatedAt = now,
        }));
        await db.SaveChangesAsync(ct);
    }
}
