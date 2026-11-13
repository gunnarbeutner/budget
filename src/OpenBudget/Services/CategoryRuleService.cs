using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>A rule as the Rules page lists it.</summary>
public sealed record CategoryRuleView(CategoryRule Rule, string? CategoryName, int MatchCount);

/// <summary>An account offered in the rule editor (the IBAN composes <c>local_account="…"</c>).</summary>
public sealed record RuleAccountOption(long Id, string Name, string? Iban);

/// <summary>A category or account offered in the rule editor.</summary>
public sealed record RuleOption<TId>(TId Id, string Name);

/// <summary>Why a rule could not be saved.</summary>
public enum RuleValidation
{
    Ok,
    NoCondition,
    NoCategory,
    InvalidExpression,
    InvalidAmountRange,
    UnknownAccount,
}

public sealed class RuleValidationException : Exception
{
    public RuleValidationException()
    {
    }

    public RuleValidationException(string message) : base(message)
    {
    }

    public RuleValidationException(string message, Exception innerException) : base(message, innerException)
    {
    }

    public RuleValidationException(RuleValidation reason) : base(reason.ToString())
    {
        Reason = reason;
    }

    public RuleValidation Reason { get; }
}

/// <summary>
/// Applying rules (the part shared by the sync pipeline and the "apply to existing" action). Only transactions without a
/// category whose category was not chosen by hand are touched, and only on accounts OpenBudget owns (MoneyMoney-owned
/// accounts get their categories from MoneyMoney). The rules considered are those of the projects the account is
/// assigned to, by project order, then rule order; the first match wins.
/// </summary>
public static class CategoryRuleEngine
{
    /// <summary>Whether a transaction is open for a rule.</summary>
    public static bool IsCandidate(Transaction t) =>
        t.CategoryId == null && t.CategorySetBy != TransactionCategorySource.Manual && t.Splitted != SplitState.Parent;

    /// <summary>
    /// Called by the bank sync on the rows it is about to insert (still unsaved, tracked by <paramref name="db"/>, a
    /// system context). Returns how many got a category.
    /// </summary>
    public static async Task<int> ApplyToNewAsync(BudgetDbContext db, long accountId, IReadOnlyCollection<Transaction> added, CancellationToken ct)
    {
        if (added.Count == 0) return 0;
        var account = await db.Accounts.AsNoTracking().Where(a => a.Id == accountId).Select(a => new { a.OwnerUserId, a.Provider, a.Iban }).FirstOrDefaultAsync(ct);
        if (account == null || account.Provider == AccountProviders.MoneyMoney) return 0;

        var rules = await db.CategoryRules.AsNoTracking()
            .Where(r => r.Enabled && !r.Dismissed && db.ProjectAccountAssignments.Any(a => a.AccountId == accountId && a.ProjectId == r.ProjectId))
            .Where(r => db.Categories.Any(c => c.Id == r.CategoryId && c.OwnerUserId == account.OwnerUserId))
            .OrderBy(r => r.Project.SortOrder).ThenBy(r => r.ProjectId).ThenBy(r => r.SortOrder).ThenBy(r => r.Id)
            .ToListAsync(ct);
        if (rules.Count == 0) return 0;

        var applied = 0;
        foreach (var t in added)
        {
            if (!IsCandidate(t)) continue;
            if (CategoryRuleMatcher.FirstMatch(rules, RuleSubject.Of(t, account.Iban)) is not { } rule) continue;
            Apply(t, rule);
            applied++;
        }
        return applied;
    }

    public static void Apply(Transaction t, CategoryRule rule)
    {
        t.CategoryId = rule.CategoryId;
        t.CategorySetBy = TransactionCategorySource.Rule;
        t.RuleId = rule.Id;
    }
}

/// <summary>
/// Manages a project's categorisation rules (the rules bar of a category in Transactions). Everything runs through the user-scoped context: reads see
/// only visible projects, and the SaveChanges guard requires Editor for rule changes and the transaction edit rights
/// for applying them to existing rows.
/// </summary>
public sealed class CategoryRuleService
{
    private const int Chunk = 500;

    private readonly IDbContextFactory<BudgetDbContext> _dbFactory;
    private readonly ChangeNotificationService _notifier;
    private readonly ChangeOrigin _origin;

    public CategoryRuleService(IDbContextFactory<BudgetDbContext> dbFactory, ChangeNotificationService notifier, ChangeOrigin origin)
    {
        _dbFactory = dbFactory;
        _notifier = notifier;
        _origin = origin;
    }

    /// <summary>The project's rules in order, each with how many of the project's transactions it matches (any category).</summary>
    public async Task<List<CategoryRuleView>> GetRulesAsync(int projectId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rules = await OrderedRulesAsync(db, projectId, ct);
        if (rules.Count == 0) return [];
        var categoryIds = rules.Select(r => r.CategoryId).Distinct().ToList();
        var categories = await db.Categories.AsNoTracking().Where(c => categoryIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var accounts = await ProjectAccountsAsync(db, projectId, ct);
        var subjects = await SubjectsAsync(db, accounts.Select(a => a.Id).ToList(), onlyCandidates: false, ct);
        return rules.Select(r => new CategoryRuleView(
            r,
            categories.GetValueOrDefault(r.CategoryId),
            subjects.Count(s => CategoryRuleMatcher.Matches(r, s.Subject)))).ToList();
    }

    /// <summary>Assignable categories of the project's source, by name.</summary>
    public async Task<List<RuleOption<long>>> GetCategoriesAsync(int projectId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var ownerId = await db.Projects.AsNoTracking().Where(p => p.Id == projectId).Select(p => p.OwnerUserId).FirstOrDefaultAsync(ct);
        if (ownerId == null) return [];
        var rows = await db.Categories.AsNoTracking()
            .Where(c => c.OwnerUserId == ownerId && c.Name != null && c.Name != "")
            .Select(c => new { c.Id, c.Name }).ToListAsync(ct);
        return rows.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).Select(c => new RuleOption<long>(c.Id, c.Name!)).ToList();
    }

    /// <summary>The project's accounts (for the optional account condition).</summary>
    public async Task<List<RuleAccountOption>> GetAccountsAsync(int projectId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return (await ProjectAccountsAsync(db, projectId, ct)).Select(a => new RuleAccountOption(a.Id, a.Name, a.Iban)).ToList();
    }

    /// <summary>Creates (Id 0, appended at the end) or updates a rule. Returns its id.</summary>
    public async Task<int> SaveAsync(CategoryRule input, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var ownerId = await db.Projects.AsNoTracking().Where(p => p.Id == input.ProjectId).Select(p => p.OwnerUserId).FirstOrDefaultAsync(ct)
            ?? throw new AccessDeniedException("Project not found.");
        var problem = Validate(input);
        if (problem == RuleValidation.Ok
            && !await db.Categories.AnyAsync(c => c.Id == input.CategoryId && c.OwnerUserId == ownerId && c.Name != null && c.Name != "", ct))
            problem = RuleValidation.NoCategory;
        if (problem == RuleValidation.Ok && input.Expression.Contains("account_id", StringComparison.Ordinal))
        {
            var own = await ProjectAccountsAsync(db, input.ProjectId, ct);
            if (!own.Any(a => RuleExpression.ReferencesAccount(input.Expression, a.Id))) problem = RuleValidation.UnknownAccount;
        }
        if (problem != RuleValidation.Ok) throw new RuleValidationException(problem);

        CategoryRule rule;
        if (input.Id == 0)
        {
            var last = await db.CategoryRules.Where(r => r.ProjectId == input.ProjectId).MaxAsync(r => (int?)r.SortOrder, ct) ?? -1;
            rule = new CategoryRule { ProjectId = input.ProjectId, SortOrder = last + 1 };
            db.CategoryRules.Add(rule);
        }
        else
        {
            rule = await db.CategoryRules.FirstOrDefaultAsync(r => r.Id == input.Id && r.ProjectId == input.ProjectId, ct)
                ?? throw new AccessDeniedException("Rule not found.");
        }
        rule.Name = string.IsNullOrWhiteSpace(input.Name) ? null : input.Name.Trim();
        rule.Enabled = input.Enabled;
        rule.Expression = input.Expression.Trim();
        rule.CategoryId = input.CategoryId;
        await db.SaveChangesAsync(ct);
        Publish(input.ProjectId);
        return rule.Id;
    }

    /// <summary>Checks what can be checked without the database.</summary>
    public static RuleValidation Validate(CategoryRule rule)
    {
        if (!CategoryRuleMatcher.HasCondition(rule)) return RuleValidation.NoCondition;
        if (rule.CategoryId == 0) return RuleValidation.NoCategory;
        if (!RuleExpression.IsValid(rule.Expression)) return RuleValidation.InvalidExpression;
        return RuleValidation.Ok;
    }

    public async Task DeleteAsync(int projectId, int ruleId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rule = await db.CategoryRules.FirstOrDefaultAsync(r => r.Id == ruleId && r.ProjectId == projectId && !r.Dismissed, ct);
        if (rule == null) return;
        // An imported rule stays as a hidden marker so a re-import does not recreate it.
        if (rule.ImportOrigin != null)
        {
            rule.Dismissed = true;
            rule.Enabled = false;
        }
        else
        {
            db.CategoryRules.Remove(rule);
        }
        await db.SaveChangesAsync(ct);
        Publish(projectId);
    }

    public async Task SetEnabledAsync(int projectId, int ruleId, bool enabled, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rule = await db.CategoryRules.FirstOrDefaultAsync(r => r.Id == ruleId && r.ProjectId == projectId, ct)
            ?? throw new AccessDeniedException("Rule not found.");
        if (rule.Enabled == enabled) return;
        rule.Enabled = enabled;
        await db.SaveChangesAsync(ct);
        Publish(projectId);
    }

    /// <summary>The rules that set <paramref name="categoryId"/>, in order (the category's rules bar).</summary>
    public async Task<List<CategoryRuleView>> GetCategoryRulesAsync(int projectId, long categoryId, CancellationToken ct = default) =>
        (await GetRulesAsync(projectId, ct)).Where(v => v.Rule.CategoryId == categoryId).ToList();

    /// <summary>
    /// Moves a rule one place up (-1) or down (+1) among the rules of the same category: it swaps places in the
    /// project's order with the neighbouring rule of that category. Rules of other categories keep their positions.
    /// </summary>
    public async Task MoveWithinCategoryAsync(int projectId, int ruleId, int delta, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rules = await db.CategoryRules.Where(r => r.ProjectId == projectId && !r.Dismissed).OrderBy(r => r.SortOrder).ThenBy(r => r.Id).ToListAsync(ct);
        var rule = rules.FirstOrDefault(r => r.Id == ruleId);
        if (rule == null || delta == 0) return;
        var siblings = rules.Where(r => r.CategoryId == rule.CategoryId).ToList();
        var target = siblings.IndexOf(rule) + Math.Sign(delta);
        if (target < 0 || target >= siblings.Count) return;
        var i = rules.IndexOf(rule);
        var j = rules.IndexOf(siblings[target]);
        (rules[i], rules[j]) = (rules[j], rules[i]);
        for (var k = 0; k < rules.Count; k++)
            if (rules[k].SortOrder != k) rules[k].SortOrder = k;
        await db.SaveChangesAsync(ct);
        Publish(projectId);
    }

    /// <summary>
    /// Live preview of a draft rule: the ids of the project's transactions (any category) it would match. The draft
    /// need not be valid or saved; a draft without a condition matches nothing.
    /// </summary>
    public async Task<List<long>> PreviewMatchIdsAsync(int projectId, CategoryRule draft, CancellationToken ct = default)
    {
        if (!CategoryRuleMatcher.HasCondition(draft)) return [];
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var accounts = await ProjectAccountsAsync(db, projectId, ct);
        var subjects = await SubjectsAsync(db, accounts.Select(a => a.Id).ToList(), onlyCandidates: false, ct);
        return subjects.Where(s => CategoryRuleMatcher.Matches(draft, s.Subject)).Select(s => s.Row.Id).ToList();
    }

    /// <summary>Moves a rule one place up (<paramref name="delta"/> = -1) or down (+1); renumbers the list densely.</summary>
    public async Task MoveAsync(int projectId, int ruleId, int delta, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rules = await db.CategoryRules.Where(r => r.ProjectId == projectId && !r.Dismissed).OrderBy(r => r.SortOrder).ThenBy(r => r.Id).ToListAsync(ct);
        var index = rules.FindIndex(r => r.Id == ruleId);
        var target = index + Math.Sign(delta);
        if (index < 0 || target < 0 || target >= rules.Count) return;
        (rules[index], rules[target]) = (rules[target], rules[index]);
        for (var i = 0; i < rules.Count; i++)
            if (rules[i].SortOrder != i) rules[i].SortOrder = i;
        await db.SaveChangesAsync(ct);
        Publish(projectId);
    }

    /// <summary>How many uncategorised transactions of the project "Apply to existing" would categorise.</summary>
    /// <remarks>With <paramref name="categoryId"/>, only the rows whose winning rule sets that category.</remarks>
    public async Task<int> PreviewApplyExistingAsync(int projectId, long? categoryId = null, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var plan = await PlanAsync(db, projectId, categoryId, ct);
        return plan.Count;
    }

    /// <summary>Categorises the project's existing uncategorised (and never hand-categorised) transactions. Returns how many.</summary>
    public async Task<int> ApplyExistingAsync(int projectId, long? categoryId = null, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var plan = await PlanAsync(db, projectId, categoryId, ct);
        if (plan.Count == 0) return 0;
        var sources = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chunk in plan.Chunk(Chunk))
        {
            var ids = chunk.Select(p => p.TransactionId).ToList();
            var rows = await db.Transactions.Where(t => ids.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);
            foreach (var (id, rule) in chunk)
            {
                if (!rows.TryGetValue(id, out var t) || !CategoryRuleEngine.IsCandidate(t)) continue;
                CategoryRuleEngine.Apply(t, rule);
                sources.Add(t.OwnerUserId);
            }
        }
        var changed = db.ChangeTracker.Entries<Transaction>().Count(e => e.State == EntityState.Modified);
        await db.SaveChangesAsync(ct);
        foreach (var s in sources) _notifier.Publish(new DataChange(DataArea.ImportedData, OwnerUserId: s), _origin.Id);
        return changed;
    }

    /// <summary>(transaction, rule) pairs for every candidate the user may edit that one of the project's rules matches.</summary>
    private static async Task<List<(long TransactionId, CategoryRule Rule)>> PlanAsync(BudgetDbContext db, int projectId, long? categoryId, CancellationToken ct)
    {
        var ownerId = await db.Projects.AsNoTracking().Where(p => p.Id == projectId).Select(p => p.OwnerUserId).FirstOrDefaultAsync(ct);
        if (ownerId == null) return [];
        var rules = (await OrderedRulesAsync(db, projectId, ct)).Where(r => r.Enabled).ToList();
        if (rules.Count == 0) return [];
        var validCategories = (await db.Categories.AsNoTracking().Where(c => c.OwnerUserId == ownerId).Select(c => c.Id).ToListAsync(ct)).ToHashSet();
        rules = rules.Where(r => validCategories.Contains(r.CategoryId)).ToList();

        var accounts = (await ProjectAccountsAsync(db, projectId, ct))
            .Where(a => a.Provider != AccountProviders.MoneyMoney
                && TransactionEditRules.Check(db.Scope, a.OwnerUserId, a.Id, a.Provider) == EditBlock.None)
            .Select(a => a.Id).ToList();
        var subjects = await SubjectsAsync(db, accounts, onlyCandidates: true, ct);
        var plan = new List<(long, CategoryRule)>();
        foreach (var (t, subject) in subjects)
            if (CategoryRuleMatcher.FirstMatch(rules, subject) is { } rule && (categoryId == null || rule.CategoryId == categoryId)) plan.Add((t.Id, rule));
        return plan;
    }

    private static Task<List<CategoryRule>> OrderedRulesAsync(BudgetDbContext db, int projectId, CancellationToken ct) =>
        db.CategoryRules.AsNoTracking().Where(r => r.ProjectId == projectId && !r.Dismissed).OrderBy(r => r.SortOrder).ThenBy(r => r.Id).ToListAsync(ct);

    private sealed record ProjectAccount(long Id, string Name, string OwnerUserId, string Provider, string? Iban);

    private static async Task<List<ProjectAccount>> ProjectAccountsAsync(BudgetDbContext db, int projectId, CancellationToken ct)
    {
        var rows = await db.Accounts.AsNoTracking()
            .Where(a => db.ProjectAccountAssignments.Any(p => p.ProjectId == projectId && p.AccountId == a.Id))
            .Select(a => new { a.Id, a.Name, a.OwnerUserId, a.Provider, a.Iban })
            .ToListAsync(ct);
        return rows.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(a => new ProjectAccount(a.Id, a.Name ?? "", a.OwnerUserId, a.Provider, a.Iban)).ToList();
    }

    private static async Task<List<(Transaction Row, RuleSubject Subject)>> SubjectsAsync(BudgetDbContext db, List<long> accountIds, bool onlyCandidates, CancellationToken ct)
    {
        var ibans = new Dictionary<long, string?>();
        foreach (var chunk in accountIds.Chunk(Chunk))
        {
            var part = chunk;
            foreach (var a in await db.Accounts.AsNoTracking().Where(a => part.Contains(a.Id)).Select(a => new { a.Id, a.Iban }).ToListAsync(ct))
                ibans[a.Id] = a.Iban;
        }
        var result = new List<(Transaction, RuleSubject)>();
        foreach (var chunk in accountIds.Chunk(Chunk))
        {
            var part = chunk;
            var q = db.Transactions.AsNoTracking().Where(t => t.AccountId.HasValue && part.Contains(t.AccountId.Value) && (t.Splitted ?? 0) != SplitState.Parent);
            if (onlyCandidates) q = q.Where(t => t.CategoryId == null && t.CategorySetBy != TransactionCategorySource.Manual);
            result.AddRange((await q.Select(t => new Transaction
            {
                Id = t.Id,
                OwnerUserId = t.OwnerUserId,
                AccountId = t.AccountId,
                Amount = t.Amount,
                Name = t.Name,
                Purpose = t.Purpose,
                BookingText = t.BookingText,
                Comment = t.Comment,
                Note = t.Note,
                CounterpartyIban = t.CounterpartyIban,
                Currency = t.Currency,
                BankReference = t.BankReference,
                EndToEndReference = t.EndToEndReference,
                MandateReference = t.MandateReference,
                CreditorId = t.CreditorId,
                CategoryId = t.CategoryId,
                CategorySetBy = t.CategorySetBy,
            }).ToListAsync(ct)).Select(t => (t, RuleSubject.Of(t, ibans.GetValueOrDefault(t.AccountId!.Value)))));
        }
        return result;
    }

    private void Publish(int projectId) => _notifier.Publish(new DataChange(DataArea.Projects, ProjectId: projectId), _origin.Id);
}
