using Microsoft.EntityFrameworkCore;
using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>A rule as the Rules page lists it.</summary>
public sealed record CategoryRuleView(CategoryRule Rule, string? CategoryName, string? AccountName, int MatchCount);

/// <summary>A category or account offered in the rule editor.</summary>
public sealed record RuleOption<TId>(TId Id, string Name);

/// <summary>Why a rule could not be saved.</summary>
public enum RuleValidation
{
    Ok,
    NoCondition,
    NoCategory,
    InvalidRegex,
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
        var account = await db.Accounts.AsNoTracking().Where(a => a.Id == accountId).Select(a => new { a.SourceId, a.Provider }).FirstOrDefaultAsync(ct);
        if (account == null || account.Provider == AccountProviders.MoneyMoney) return 0;

        var rules = await db.CategoryRules.AsNoTracking()
            .Where(r => r.Enabled && db.ProjectAccountAssignments.Any(a => a.AccountId == accountId && a.ProjectId == r.ProjectId))
            .Where(r => db.Categories.Any(c => c.Id == r.CategoryId && c.SourceId == account.SourceId))
            .OrderBy(r => r.Project.SortOrder).ThenBy(r => r.ProjectId).ThenBy(r => r.SortOrder).ThenBy(r => r.Id)
            .ToListAsync(ct);
        if (rules.Count == 0) return 0;

        var applied = 0;
        foreach (var t in added)
        {
            if (!IsCandidate(t)) continue;
            if (CategoryRuleMatcher.FirstMatch(rules, RuleSubject.Of(t)) is not { } rule) continue;
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
/// Manages a project's categorisation rules (Gear › Rules). Everything runs through the user-scoped context: reads see
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
            r.AccountId is { } a ? accounts.FirstOrDefault(x => x.Id == a)?.Name : null,
            subjects.Count(s => CategoryRuleMatcher.Matches(r, RuleSubject.Of(s))))).ToList();
    }

    /// <summary>Assignable categories of the project's source, by name.</summary>
    public async Task<List<RuleOption<long>>> GetCategoriesAsync(int projectId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var sourceId = await db.Projects.AsNoTracking().Where(p => p.Id == projectId).Select(p => (int?)p.SourceId).FirstOrDefaultAsync(ct);
        if (sourceId == null) return [];
        var rows = await db.Categories.AsNoTracking()
            .Where(c => c.SourceId == sourceId && c.Name != null && c.Name != "")
            .Select(c => new { c.Id, c.Name }).ToListAsync(ct);
        return rows.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).Select(c => new RuleOption<long>(c.Id, c.Name!)).ToList();
    }

    /// <summary>The project's accounts (for the optional account condition).</summary>
    public async Task<List<RuleOption<long>>> GetAccountsAsync(int projectId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return (await ProjectAccountsAsync(db, projectId, ct)).Select(a => new RuleOption<long>(a.Id, a.Name)).ToList();
    }

    /// <summary>Creates (Id 0, appended at the end) or updates a rule. Returns its id.</summary>
    public async Task<int> SaveAsync(CategoryRule input, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var sourceId = await db.Projects.AsNoTracking().Where(p => p.Id == input.ProjectId).Select(p => (int?)p.SourceId).FirstOrDefaultAsync(ct)
            ?? throw new AccessDeniedException("Project not found.");
        var problem = Validate(input);
        if (problem == RuleValidation.Ok
            && !await db.Categories.AnyAsync(c => c.Id == input.CategoryId && c.SourceId == sourceId && c.Name != null && c.Name != "", ct))
            problem = RuleValidation.NoCategory;
        if (problem == RuleValidation.Ok && input.AccountId is { } accountId
            && !(await ProjectAccountsAsync(db, input.ProjectId, ct)).Any(a => a.Id == accountId))
            problem = RuleValidation.UnknownAccount;
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
        rule.PayeeText = string.IsNullOrWhiteSpace(input.PayeeText) ? null : input.PayeeText.Trim();
        rule.PurposeText = string.IsNullOrWhiteSpace(input.PurposeText) ? null : input.PurposeText.Trim();
        rule.UseRegex = input.UseRegex;
        rule.AccountId = input.AccountId;
        rule.AmountSign = input.AmountSign;
        rule.MinAmountCents = input.MinAmountCents;
        rule.MaxAmountCents = input.MaxAmountCents;
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
        if (rule.UseRegex)
        {
            foreach (var text in new[] { rule.PayeeText, rule.PurposeText })
                if (!string.IsNullOrWhiteSpace(text) && !CategoryRuleMatcher.IsValidRegex(text.Trim())) return RuleValidation.InvalidRegex;
        }
        if (rule.MinAmountCents < 0 || rule.MaxAmountCents < 0 || (rule.MinAmountCents is { } min && rule.MaxAmountCents is { } max && min > max))
            return RuleValidation.InvalidAmountRange;
        return RuleValidation.Ok;
    }

    public async Task DeleteAsync(int projectId, int ruleId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rule = await db.CategoryRules.FirstOrDefaultAsync(r => r.Id == ruleId && r.ProjectId == projectId, ct);
        if (rule == null) return;
        db.CategoryRules.Remove(rule);
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

    /// <summary>Moves a rule one place up (<paramref name="delta"/> = -1) or down (+1); renumbers the list densely.</summary>
    public async Task MoveAsync(int projectId, int ruleId, int delta, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var rules = await db.CategoryRules.Where(r => r.ProjectId == projectId).OrderBy(r => r.SortOrder).ThenBy(r => r.Id).ToListAsync(ct);
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
    public async Task<int> PreviewApplyExistingAsync(int projectId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var plan = await PlanAsync(db, projectId, ct);
        return plan.Count;
    }

    /// <summary>Categorises the project's existing uncategorised (and never hand-categorised) transactions. Returns how many.</summary>
    public async Task<int> ApplyExistingAsync(int projectId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var plan = await PlanAsync(db, projectId, ct);
        if (plan.Count == 0) return 0;
        var sources = new HashSet<int>();
        foreach (var chunk in plan.Chunk(Chunk))
        {
            var ids = chunk.Select(p => p.TransactionId).ToList();
            var rows = await db.Transactions.Where(t => ids.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);
            foreach (var (id, rule) in chunk)
            {
                if (!rows.TryGetValue(id, out var t) || !CategoryRuleEngine.IsCandidate(t)) continue;
                CategoryRuleEngine.Apply(t, rule);
                sources.Add(t.SourceId);
            }
        }
        var changed = db.ChangeTracker.Entries<Transaction>().Count(e => e.State == EntityState.Modified);
        await db.SaveChangesAsync(ct);
        foreach (var s in sources) _notifier.Publish(new DataChange(DataArea.ImportedData, SourceId: s), _origin.Id);
        return changed;
    }

    /// <summary>(transaction, rule) pairs for every candidate the user may edit that one of the project's rules matches.</summary>
    private static async Task<List<(long TransactionId, CategoryRule Rule)>> PlanAsync(BudgetDbContext db, int projectId, CancellationToken ct)
    {
        var sourceId = await db.Projects.AsNoTracking().Where(p => p.Id == projectId).Select(p => (int?)p.SourceId).FirstOrDefaultAsync(ct);
        if (sourceId == null) return [];
        var rules = (await OrderedRulesAsync(db, projectId, ct)).Where(r => r.Enabled).ToList();
        if (rules.Count == 0) return [];
        var validCategories = (await db.Categories.AsNoTracking().Where(c => c.SourceId == sourceId).Select(c => c.Id).ToListAsync(ct)).ToHashSet();
        rules = rules.Where(r => validCategories.Contains(r.CategoryId)).ToList();

        var accounts = (await ProjectAccountsAsync(db, projectId, ct))
            .Where(a => a.Provider != AccountProviders.MoneyMoney
                && TransactionEditRules.Check(db.Scope, a.SourceId, a.Id, a.Provider) == EditBlock.None)
            .Select(a => a.Id).ToList();
        var subjects = await SubjectsAsync(db, accounts, onlyCandidates: true, ct);
        var plan = new List<(long, CategoryRule)>();
        foreach (var t in subjects)
            if (CategoryRuleMatcher.FirstMatch(rules, RuleSubject.Of(t)) is { } rule) plan.Add((t.Id, rule));
        return plan;
    }

    private static Task<List<CategoryRule>> OrderedRulesAsync(BudgetDbContext db, int projectId, CancellationToken ct) =>
        db.CategoryRules.AsNoTracking().Where(r => r.ProjectId == projectId).OrderBy(r => r.SortOrder).ThenBy(r => r.Id).ToListAsync(ct);

    private sealed record ProjectAccount(long Id, string Name, int SourceId, string Provider);

    private static async Task<List<ProjectAccount>> ProjectAccountsAsync(BudgetDbContext db, int projectId, CancellationToken ct)
    {
        var rows = await db.Accounts.AsNoTracking()
            .Where(a => db.ProjectAccountAssignments.Any(p => p.ProjectId == projectId && p.AccountId == a.Id))
            .Select(a => new { a.Id, a.Name, a.SourceId, a.Provider })
            .ToListAsync(ct);
        return rows.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(a => new ProjectAccount(a.Id, a.Name ?? "", a.SourceId, a.Provider)).ToList();
    }

    private static async Task<List<Transaction>> SubjectsAsync(BudgetDbContext db, List<long> accountIds, bool onlyCandidates, CancellationToken ct)
    {
        var result = new List<Transaction>();
        foreach (var chunk in accountIds.Chunk(Chunk))
        {
            var part = chunk;
            var q = db.Transactions.AsNoTracking().Where(t => t.AccountId.HasValue && part.Contains(t.AccountId.Value) && (t.Splitted ?? 0) != SplitState.Parent);
            if (onlyCandidates) q = q.Where(t => t.CategoryId == null && t.CategorySetBy != TransactionCategorySource.Manual);
            result.AddRange(await q.Select(t => new Transaction
            {
                Id = t.Id,
                SourceId = t.SourceId,
                AccountId = t.AccountId,
                Amount = t.Amount,
                Name = t.Name,
                Purpose = t.Purpose,
                BookingText = t.BookingText,
                CategoryId = t.CategoryId,
                CategorySetBy = t.CategorySetBy,
            }).ToListAsync(ct));
        }
        return result;
    }

    private void Publish(int projectId) => _notifier.Publish(new DataChange(DataArea.Projects, ProjectId: projectId), _origin.Id);
}
