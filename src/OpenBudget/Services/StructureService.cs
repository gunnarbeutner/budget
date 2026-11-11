using Microsoft.EntityFrameworkCore;
using OpenBudget.Connectors.Sync;
using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>Why the structure of a source cannot be changed by the current user (null = it can).</summary>
public enum StructureBlock
{
    /// <summary>Only the source's owner edits accounts, categories and their groups (an editor of a shared project cannot).</summary>
    NotOwner,
    /// <summary>MoneyMoney still owns the structure: every upload rewrites it until the owner takes it over (Sources).</summary>
    MoneyMoney,
}

/// <summary>What deleting an account takes with it, for the confirmation dialog.</summary>
public sealed record AccountDeleteInfo(long AccountId, string Name, int Bookings, int Statements);

/// <summary>A group in "Move to group…" (indented by <paramref name="Depth"/>).</summary>
public sealed record StructureGroupOption(long Id, string Name, int Depth);

/// <summary>Edit permissions for a project's sidebar: the category tree's source and each account's source.</summary>
public sealed record StructureEditInfo(int ProjectSourceId, IReadOnlyDictionary<long, int> AccountSources, IReadOnlyDictionary<int, StructureBlock?> Blocks)
{
    public StructureBlock? CategoryBlock => Blocks.GetValueOrDefault(ProjectSourceId, StructureBlock.NotOwner);

    public int? SourceOfAccount(long accountId) => AccountSources.TryGetValue(accountId, out var s) ? s : null;

    public StructureBlock? AccountBlock(long accountId) =>
        SourceOfAccount(accountId) is { } s ? Blocks.GetValueOrDefault(s, StructureBlock.NotOwner) : StructureBlock.NotOwner;
}

/// <summary>The kinds of rows in the two sidebar trees that can be moved.</summary>
public enum StructureItemKind
{
    Category,
    CategoryGroup,
    Account,
    AccountGroup,
}

/// <summary>
/// Structure editing from the sidebar: rename, create, move/reorder and delete categories, category groups, accounts and
/// account groups. Only the source's owner may, and only after the structure was taken over from MoneyMoney
/// (<see cref="ImportSource.StructureTakenOverAt"/>); before that each upload rewrites it. Simple edits go through the
/// user-scoped context (whose SaveChanges guard requires the source owner as well); the cascades of a delete (bookings,
/// statements, project rows of every project of the source, other users' bank-login links) run on the system context
/// after the same checks, so nothing is left dangling behind the read filters.
/// <para>
/// Order: siblings (sub-groups and items under one parent) share one explicit sort sequence, as the takeover left it;
/// every move renumbers the siblings 0..n.
/// </para>
/// </summary>
public sealed class StructureService
{
    private readonly IDbContextFactory<BudgetDbContext> _dbFactory;
    private readonly SystemDbContextFactory _system;
    private readonly ChangeNotificationService _notifier;
    private readonly ChangeOrigin _origin;
    private readonly BankStatementStore? _statementStore;

    /// <summary>First rowid OpenBudget uses for groups and categories it creates (inside the connector range, past the reserved "New categories" group).</summary>
    public const long OwnRowBase = BankTransactionStore.ConnectorRowBase + 1000;

    public StructureService(
        IDbContextFactory<BudgetDbContext> dbFactory, SystemDbContextFactory system, ChangeNotificationService notifier,
        ChangeOrigin origin, BankStatementStore? statementStore = null)
    {
        _dbFactory = dbFactory;
        _system = system;
        _notifier = notifier;
        _origin = origin;
        _statementStore = statementStore;
    }

    // ---- Permission ----

    /// <summary>Why the current user cannot edit the structure of <paramref name="sourceId"/>, or null when they can.</summary>
    public async Task<StructureBlock?> GetBlockAsync(int sourceId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await BlockAsync(db, sourceId, ct);
    }

    // A source MoneyMoney never uploaded to (e.g. a bank connector's) has an OpenBudget-owned structure from the start.
    private static async Task<StructureBlock?> BlockAsync(BudgetDbContext db, int sourceId, CancellationToken ct)
    {
        if (db.Scope is { } scope && !scope.OwnsSource(sourceId)) return StructureBlock.NotOwner;
        var ours = await db.ImportSources.AnyAsync(s => s.Id == sourceId && (s.StructureTakenOverAt != null || s.LastUploadAt == null), ct);
        return ours ? null : StructureBlock.MoneyMoney;
    }

    /// <summary>
    /// What the sidebar of a project needs to build its menus: the block of the project's source (the category tree) and,
    /// per account, its source and that source's block (accounts of a project may live in several sources).
    /// </summary>
    public async Task<StructureEditInfo> GetEditInfoAsync(int projectSourceId, IReadOnlyCollection<long> accountIds, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var sources = await db.Accounts.AsNoTracking().Where(a => accountIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.SourceId, ct);
        // Account groups too (ids are namespaced by source, so a group and an account sharing an id share the source).
        var groupSources = sources.Values.Distinct().ToList();
        foreach (var g in await db.AccountGroups.AsNoTracking().Where(g => groupSources.Contains(g.SourceId)).Select(g => new { g.Id, g.SourceId }).ToListAsync(ct))
            sources.TryAdd(g.Id, g.SourceId);
        var blocks = new Dictionary<int, StructureBlock?> { [projectSourceId] = await BlockAsync(db, projectSourceId, ct) };
        foreach (var s in sources.Values.Distinct().Where(s => !blocks.ContainsKey(s)))
            blocks[s] = await BlockAsync(db, s, ct);
        return new StructureEditInfo(projectSourceId, sources, blocks);
    }

    /// <summary>The groups of a tree in a source, depth first in display order, for "Move to group…".</summary>
    public async Task<List<StructureGroupOption>> ListGroupsAsync(bool categories, int sourceId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var result = new List<StructureGroupOption>();
        async Task WalkAsync(long? parent, int depth)
        {
            var siblings = categories ? await CategorySiblingsAsync(db, sourceId, parent, ct) : await AccountSiblingsAsync(db, sourceId, parent, ct);
            foreach (var g in siblings.Where(s => s.Kind is StructureItemKind.CategoryGroup or StructureItemKind.AccountGroup))
            {
                result.Add(new StructureGroupOption(g.Id, g.Name, depth));
                await WalkAsync(g.Id, depth + 1);
            }
        }
        await WalkAsync(categories ? await CategoryRootAsync(db, sourceId, ct) : null, 0);
        return result;
    }

    private static async Task RequireEditableAsync(BudgetDbContext db, int sourceId, CancellationToken ct)
    {
        var block = await BlockAsync(db, sourceId, ct);
        if (block == StructureBlock.NotOwner) throw new AccessDeniedException("Only the source's owner can change its structure.");
        if (block == StructureBlock.MoneyMoney) throw new InvalidOperationException("MoneyMoney still owns this structure; take it over first.");
    }

    private static string CleanName(string name)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0) throw new ArgumentException("A name is required.", nameof(name));
        return n.Length > 200 ? n[..200] : n;
    }

    private void Changed(int sourceId, bool projects = false)
    {
        _notifier.Publish(new DataChange(DataArea.ImportedData, SourceId: sourceId), _origin.Id);
        if (projects) _notifier.Publish(new DataChange(DataArea.Projects), _origin.Id);
    }

    // ---- Rename ----

    public async Task RenameAsync(StructureItemKind kind, long id, string name, CancellationToken ct = default)
    {
        var clean = CleanName(name);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var (sourceId, _) = await LocateAsync(db, kind, id, ct);
        await RequireEditableAsync(db, sourceId, ct);
        await (kind switch
        {
            StructureItemKind.Category => db.Categories.Where(x => x.Id == id).ForEachAsync(x => x.Name = clean, ct),
            StructureItemKind.CategoryGroup => db.CategoryGroups.Where(x => x.Id == id).ForEachAsync(x => x.Name = clean, ct),
            StructureItemKind.Account => db.Accounts.Where(x => x.Id == id).ForEachAsync(x => x.Name = clean, ct),
            StructureItemKind.AccountGroup => db.AccountGroups.Where(x => x.Id == id).ForEachAsync(x => x.Name = clean, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        });
        await db.SaveChangesAsync(ct);
        Changed(sourceId);
    }

    // ---- Icon and colour ----

    /// <summary>
    /// Sets the icon and colour of an account or account group (null = its default icon / the default colour). Only the
    /// source's owner may; unlike the other structure edits this also works while MoneyMoney owns the structure, since
    /// uploads keep both.
    /// </summary>
    public async Task SetAppearanceAsync(StructureItemKind kind, long id, string? icon, string? color, CancellationToken ct = default)
    {
        if (icon != null && !AccountIcons.IsValid(icon)) throw new ArgumentException("Not an offered icon.", nameof(icon));
        if (color != null && !CategoryColors.IsValid(color)) throw new ArgumentException("Not a color.", nameof(color));
        var css = color == null ? null : CategoryColors.ToCss(color);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var (sourceId, _) = await LocateAsync(db, kind, id, ct);
        if (await BlockAsync(db, sourceId, ct) == StructureBlock.NotOwner)
            throw new AccessDeniedException("Only the source's owner can change its structure.");
        await (kind switch
        {
            StructureItemKind.Account => db.Accounts.Where(x => x.Id == id).ForEachAsync(x => (x.Icon, x.Color) = (icon, css), ct),
            StructureItemKind.AccountGroup => db.AccountGroups.Where(x => x.Id == id).ForEachAsync(x => (x.Icon, x.Color) = (icon, css), ct),
            StructureItemKind.Category or StructureItemKind.CategoryGroup => throw new ArgumentOutOfRangeException(nameof(kind)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        });
        await db.SaveChangesAsync(ct);
        Changed(sourceId);
    }

    private static KeyNotFoundException NotFound() => new("Not found.");

    // ---- Create ----

    /// <summary>A new category in <paramref name="groupId"/>, placed right after <paramref name="afterId"/> (a sibling) or at the end.</summary>
    public async Task<long> CreateCategoryAsync(long groupId, string name, long? afterId = null, CancellationToken ct = default)
    {
        var clean = CleanName(name);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var group = await db.CategoryGroups.FirstOrDefaultAsync(g => g.Id == groupId, ct) ?? throw NotFound();
        await RequireEditableAsync(db, group.SourceId, ct);
        var id = await NextOwnIdAsync(db, group.SourceId, ct);
        // Expense by default (MoneyMoney's type 1); the project decides income vs. expense itself.
        var category = new Category { Id = id, SourceId = group.SourceId, Name = clean, GroupId = groupId, Type = 1 };
        db.Categories.Add(category);
        var siblings = await CategorySiblingsAsync(db, group.SourceId, groupId, ct);
        var at = afterId is { } after ? siblings.FindIndex(s => s.Kind == StructureItemKind.Category && s.Id == after) + 1 : siblings.Count;
        if (at <= 0) at = siblings.Count;
        siblings.Insert(at, new Sibling(StructureItemKind.Category, id, null, clean, v => category.Sort = v));
        Renumber(siblings);
        await db.SaveChangesAsync(ct);
        Changed(group.SourceId);
        return id;
    }

    /// <summary>A new category in the same group as <paramref name="siblingId"/>, right below it.</summary>
    public async Task<long> CreateCategoryAfterAsync(long siblingId, string name, CancellationToken ct = default)
    {
        long? group;
        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
            group = await db.Categories.Where(c => c.Id == siblingId).Select(c => c.GroupId).FirstOrDefaultAsync(ct);
        return group is { } g ? await CreateCategoryAsync(g, name, siblingId, ct) : throw NotFound();
    }

    /// <summary>A new category group under <paramref name="parentGroupId"/> (a group of the same source), at the end.</summary>
    public async Task<long> CreateCategoryGroupAsync(long parentGroupId, string name, CancellationToken ct = default)
    {
        var clean = CleanName(name);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var parent = await db.CategoryGroups.FirstOrDefaultAsync(g => g.Id == parentGroupId, ct) ?? throw NotFound();
        await RequireEditableAsync(db, parent.SourceId, ct);
        var id = await NextOwnIdAsync(db, parent.SourceId, ct);
        var group = new CategoryGroup { Id = id, SourceId = parent.SourceId, Name = clean, ParentGroupId = parentGroupId };
        db.CategoryGroups.Add(group);
        var siblings = await CategorySiblingsAsync(db, parent.SourceId, parentGroupId, ct);
        siblings.Add(new Sibling(StructureItemKind.CategoryGroup, id, null, clean, v => group.Sort = v));
        Renumber(siblings);
        await db.SaveChangesAsync(ct);
        Changed(parent.SourceId);
        return id;
    }

    /// <summary>A new account group next to (right after) <paramref name="siblingGroupId"/>.</summary>
    public async Task<long> CreateAccountGroupAsync(long siblingGroupId, string name, CancellationToken ct = default)
    {
        var clean = CleanName(name);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var sibling = await db.AccountGroups.FirstOrDefaultAsync(g => g.Id == siblingGroupId, ct) ?? throw NotFound();
        await RequireEditableAsync(db, sibling.SourceId, ct);
        var maxId = await db.AccountGroups.IgnoreQueryFilters().Where(g => g.SourceId == sibling.SourceId).MaxAsync(g => (long?)g.Id, ct);
        var floor = SourceScope.Namespace(sibling.SourceId, OwnRowBase);
        var id = maxId is { } m && m >= floor ? m + 1 : floor;
        var group = new AccountGroup { Id = id, SourceId = sibling.SourceId, Name = clean, ParentGroupId = sibling.ParentGroupId };
        db.AccountGroups.Add(group);
        var siblings = await AccountSiblingsAsync(db, sibling.SourceId, sibling.ParentGroupId, ct);
        var at = siblings.FindIndex(s => s.Kind == StructureItemKind.AccountGroup && s.Id == siblingGroupId) + 1;
        siblings.Insert(at <= 0 ? siblings.Count : at, new Sibling(StructureItemKind.AccountGroup, id, null, clean, v => group.Sort = v));
        Renumber(siblings);
        await db.SaveChangesAsync(ct);
        Changed(sibling.SourceId);
        return id;
    }

    // Categories and category groups share one id range per source (the trees mix them under a parent).
    private static async Task<long> NextOwnIdAsync(BudgetDbContext db, int sourceId, CancellationToken ct)
    {
        var floor = SourceScope.Namespace(sourceId, OwnRowBase);
        var maxCategory = await db.Categories.IgnoreQueryFilters().Where(c => c.SourceId == sourceId && c.Id >= floor).MaxAsync(c => (long?)c.Id, ct);
        var maxGroup = await db.CategoryGroups.IgnoreQueryFilters().Where(g => g.SourceId == sourceId && g.Id >= floor).MaxAsync(g => (long?)g.Id, ct);
        var max = Math.Max(maxCategory ?? floor - 1, maxGroup ?? floor - 1);
        return max + 1;
    }

    // ---- Move ----

    /// <summary>
    /// Moves an item under <paramref name="parentGroupId"/> (a group of the same tree; null = the top level of the accounts
    /// tree, or the source's root group for categories) at position <paramref name="index"/> among its new siblings.
    /// </summary>
    public async Task MoveAsync(StructureItemKind kind, long id, long? parentGroupId, int index, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var sourceId = await MoveCoreAsync(db, kind, id, parentGroupId, index, ct);
        await db.SaveChangesAsync(ct);
        Changed(sourceId);
    }

    /// <summary>
    /// Drag and drop: moves an item right before <paramref name="targetId"/> (an item of the same tree, under the target's
    /// parent), or with <paramref name="into"/> to the end of the target group.
    /// </summary>
    public async Task MoveToAsync(StructureItemKind kind, long id, StructureItemKind targetKind, long targetId, bool into, CancellationToken ct = default)
    {
        if (IsCategoryTree(kind) != IsCategoryTree(targetKind) || (kind == targetKind && id == targetId)) return;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        int sourceId;
        if (into)
        {
            sourceId = await MoveCoreAsync(db, kind, id, targetId, int.MaxValue, ct);
        }
        else
        {
            var (targetSource, parent) = await LocateAsync(db, targetKind, targetId, ct);
            var siblings = await SiblingsAsync(db, targetKind, targetSource, parent, ct);
            siblings.RemoveAll(s => s.Kind == kind && s.Id == id);
            var index = siblings.FindIndex(s => s.Kind == targetKind && s.Id == targetId);
            sourceId = await MoveCoreAsync(db, kind, id, parent, index, ct);
        }
        await db.SaveChangesAsync(ct);
        Changed(sourceId);
    }

    /// <summary>Moves an item one place up (<paramref name="delta"/> = -1) or down (+1) among its siblings; false at the edge.</summary>
    public async Task<bool> MoveStepAsync(StructureItemKind kind, long id, int delta, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var (sourceId, parent) = await LocateAsync(db, kind, id, ct);
        await RequireEditableAsync(db, sourceId, ct);
        var siblings = await SiblingsAsync(db, kind, sourceId, parent, ct);
        var at = siblings.FindIndex(s => s.Kind == kind && s.Id == id);
        var to = at + Math.Sign(delta);
        if (at < 0 || to < 0 || to >= siblings.Count) return false;
        (siblings[at], siblings[to]) = (siblings[to], siblings[at]);
        Renumber(siblings);
        await db.SaveChangesAsync(ct);
        Changed(sourceId);
        return true;
    }

    /// <summary>The position of an item among its siblings and their count (for enabling move up / down).</summary>
    public async Task<(int Index, int Count)> PositionAsync(StructureItemKind kind, long id, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var (sourceId, parent) = await LocateAsync(db, kind, id, ct);
        var siblings = await SiblingsAsync(db, kind, sourceId, parent, ct);
        return (siblings.FindIndex(s => s.Kind == kind && s.Id == id), siblings.Count);
    }

    private static bool IsCategoryTree(StructureItemKind kind) => kind is StructureItemKind.Category or StructureItemKind.CategoryGroup;

    private static async Task<int> MoveCoreAsync(BudgetDbContext db, StructureItemKind kind, long id, long? parentGroupId, int index, CancellationToken ct)
    {
        var (sourceId, oldParent) = await LocateAsync(db, kind, id, ct);
        await RequireEditableAsync(db, sourceId, ct);
        if (IsCategoryTree(kind))
        {
            parentGroupId ??= await CategoryRootAsync(db, sourceId, ct);
            if (parentGroupId is { } cp)
            {
                var target = await db.CategoryGroups.FirstOrDefaultAsync(g => g.Id == cp, ct);
                if (target == null || target.SourceId != sourceId) throw new ArgumentException("Unknown group.", nameof(parentGroupId));
                if (kind == StructureItemKind.CategoryGroup && await IsSelfOrBelowAsync(db.CategoryGroups.Where(g => g.SourceId == sourceId).Select(g => new { g.Id, g.ParentGroupId }).ToDictionaryAsync(g => g.Id, g => g.ParentGroupId, ct), id, cp))
                    throw new ArgumentException("A group cannot move into itself.", nameof(parentGroupId));
            }
        }
        else if (parentGroupId is { } pg)
        {
            var target = await db.AccountGroups.FirstOrDefaultAsync(g => g.Id == pg, ct);
            if (target == null || target.SourceId != sourceId) throw new ArgumentException("Unknown group.", nameof(parentGroupId));
            if (kind == StructureItemKind.AccountGroup && await IsSelfOrBelowAsync(db.AccountGroups.Where(g => g.SourceId == sourceId).Select(g => new { g.Id, g.ParentGroupId }).ToDictionaryAsync(g => g.Id, g => g.ParentGroupId, ct), id, pg))
                throw new ArgumentException("A group cannot move into itself.", nameof(parentGroupId));
        }

        // Close the gap in the old place, then insert into the new one.
        var old = await SiblingsAsync(db, kind, sourceId, oldParent, ct);
        var moving = old.Find(s => s.Kind == kind && s.Id == id)!;
        old.Remove(moving);
        Renumber(old);
        var siblings = await SiblingsAsync(db, kind, sourceId, parentGroupId, ct);
        siblings.RemoveAll(s => s.Kind == kind && s.Id == id);
        siblings.Insert(Math.Clamp(index, 0, siblings.Count), moving);
        Renumber(siblings);
        await SetParentAsync(db, kind, id, parentGroupId, ct);
        return sourceId;
    }

    private static async Task<bool> IsSelfOrBelowAsync(Task<Dictionary<long, long?>> parentsTask, long groupId, long candidate)
    {
        var parents = await parentsTask;
        for (long? at = candidate; at is { } a; at = parents.GetValueOrDefault(a))
        {
            if (a == groupId) return true;
            if (!parents.ContainsKey(a)) break;
        }
        return false;
    }

    private static async Task SetParentAsync(BudgetDbContext db, StructureItemKind kind, long id, long? parent, CancellationToken ct)
    {
        await (kind switch
        {
            StructureItemKind.Category => db.Categories.Where(x => x.Id == id).ForEachAsync(x => x.GroupId = parent, ct),
            StructureItemKind.CategoryGroup => db.CategoryGroups.Where(x => x.Id == id).ForEachAsync(x => x.ParentGroupId = parent, ct),
            StructureItemKind.Account => db.Accounts.Where(x => x.Id == id).ForEachAsync(x => x.GroupId = parent, ct),
            StructureItemKind.AccountGroup => db.AccountGroups.Where(x => x.Id == id).ForEachAsync(x => x.ParentGroupId = parent, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        });
    }

    private static async Task<(int SourceId, long? Parent)> LocateAsync(BudgetDbContext db, StructureItemKind kind, long id, CancellationToken ct)
    {
        var found = kind switch
        {
            StructureItemKind.Category => await db.Categories.Where(x => x.Id == id).Select(x => new { x.SourceId, Parent = x.GroupId }).FirstOrDefaultAsync(ct),
            StructureItemKind.CategoryGroup => await db.CategoryGroups.Where(x => x.Id == id).Select(x => new { x.SourceId, Parent = x.ParentGroupId }).FirstOrDefaultAsync(ct),
            StructureItemKind.Account => await db.Accounts.Where(x => x.Id == id).Select(x => new { x.SourceId, Parent = x.GroupId }).FirstOrDefaultAsync(ct),
            StructureItemKind.AccountGroup => await db.AccountGroups.Where(x => x.Id == id).Select(x => new { x.SourceId, Parent = x.ParentGroupId }).FirstOrDefaultAsync(ct),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        } ?? throw NotFound();
        if (IsCategoryTree(kind))
            return (found.SourceId, found.Parent ?? await CategoryRootAsync(db, found.SourceId, ct));
        // An account whose group is unknown sits at the top level ("Other accounts").
        if (found.Parent is { } p && !await db.AccountGroups.AnyAsync(g => g.Id == p, ct)) return (found.SourceId, null);
        return (found.SourceId, found.Parent);
    }

    // MoneyMoney's hidden root group (rowid 1) parents the top level of the category tree.
    private static async Task<long?> CategoryRootAsync(BudgetDbContext db, int sourceId, CancellationToken ct)
    {
        var root = SourceScope.SentinelRoot(sourceId);
        return await db.CategoryGroups.AnyAsync(g => g.Id == root, ct) ? root : null;
    }

    private sealed record Sibling(StructureItemKind Kind, long Id, long? Sort, string Name, Action<long> SetSort);

    private static void Renumber(List<Sibling> siblings)
    {
        for (var i = 0; i < siblings.Count; i++) siblings[i].SetSort(i);
    }

    private static Task<List<Sibling>> SiblingsAsync(BudgetDbContext db, StructureItemKind kind, int sourceId, long? parent, CancellationToken ct) =>
        IsCategoryTree(kind) ? CategorySiblingsAsync(db, sourceId, parent, ct) : AccountSiblingsAsync(db, sourceId, parent, ct);

    // Display order of the sidebar: sort (unset last), groups before items at an equal sort, then name.
    private static List<Sibling> Ordered(IEnumerable<Sibling> items) =>
        items.OrderBy(s => s.Sort ?? long.MaxValue)
            .ThenBy(s => s.Kind is StructureItemKind.Category or StructureItemKind.Account ? 1 : 0)
            .ThenBy(s => s.Name, StringComparer.Ordinal)
            .ThenBy(s => s.Id)
            .ToList();

    private static async Task<List<Sibling>> CategorySiblingsAsync(BudgetDbContext db, int sourceId, long? parent, CancellationToken ct)
    {
        var sentinels = SourceScope.SentinelIds(sourceId).ToList();
        var groups = await db.CategoryGroups.Where(g => g.SourceId == sourceId && g.ParentGroupId == parent && !sentinels.Contains(g.Id)).ToListAsync(ct);
        var categories = await db.Categories.Where(c => c.SourceId == sourceId && c.GroupId == parent && !sentinels.Contains(c.Id)).ToListAsync(ct);
        return Ordered(groups.Select(g => new Sibling(StructureItemKind.CategoryGroup, g.Id, g.Sort, g.Name ?? "", v => g.Sort = v))
            .Concat(categories.Select(c => new Sibling(StructureItemKind.Category, c.Id, c.Sort, c.Name ?? "", v => c.Sort = v))));
    }

    private static async Task<List<Sibling>> AccountSiblingsAsync(BudgetDbContext db, int sourceId, long? parent, CancellationToken ct)
    {
        var allGroups = await db.AccountGroups.Where(g => g.SourceId == sourceId).ToListAsync(ct);
        var known = allGroups.Select(g => g.Id).ToHashSet();
        long? Effective(long? p) => p is { } x && known.Contains(x) ? x : null;
        var groups = allGroups.Where(g => g.Id != parent && Effective(g.ParentGroupId) == parent);
        var accounts = (await db.Accounts.Where(a => a.SourceId == sourceId).ToListAsync(ct)).Where(a => Effective(a.GroupId) == parent);
        return Ordered(groups.Select(g => new Sibling(StructureItemKind.AccountGroup, g.Id, g.Sort, g.Name ?? "", v => g.Sort = v))
            .Concat(accounts.Select(a => new Sibling(StructureItemKind.Account, a.Id, a.Sort, a.Name ?? "", v => a.Sort = v))));
    }

    // ---- Delete ----

    /// <summary>
    /// Deletes a category: its bookings become uncategorized, rules that assign it are deleted, and so are the budget
    /// amounts, tracking and income/expense settings of every project for it. MoneyMoney does not bring it back.
    /// </summary>
    public async Task DeleteCategoryAsync(long id, CancellationToken ct = default)
    {
        int sourceId;
        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
        {
            var category = await db.Categories.FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw NotFound();
            sourceId = category.SourceId;
            await RequireEditableAsync(db, sourceId, ct);
        }

        await using var sys = await _system.CreateDbContextAsync(ct);
        await using var tx = await sys.Database.BeginTransactionAsync(ct);
        await sys.Transactions.Where(t => t.SourceId == sourceId && t.CategoryId == id)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.CategoryId, (long?)null).SetProperty(t => t.RuleId, (int?)null), ct);
        var projects = sys.Projects.Where(p => p.SourceId == sourceId).Select(p => p.Id);
        var rules = sys.CategoryRules.Where(r => r.CategoryId == id && projects.Contains(r.ProjectId));
        var ruleIds = await rules.Select(r => r.Id).ToListAsync(ct);
        if (ruleIds.Count > 0)
            await sys.Transactions.Where(t => t.SourceId == sourceId && t.RuleId != null && ruleIds.Contains(t.RuleId.Value))
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.RuleId, (int?)null), ct);
        await rules.ExecuteDeleteAsync(ct);
        await DeleteProjectRowsAsync(sys, sourceId, id, CategoryItemType.Category, ct);
        await sys.Categories.Where(c => c.Id == id).ExecuteDeleteAsync(ct);
        await TombstoneAsync(sys, sourceId, RemovedImportItem.CategoryKind, id, ct);
        await tx.CommitAsync(ct);
        Changed(sourceId, projects: true);
    }

    private static async Task DeleteProjectRowsAsync(BudgetDbContext sys, int sourceId, long id, CategoryItemType type, CancellationToken ct)
    {
        var projects = sys.Projects.Where(p => p.SourceId == sourceId).Select(p => p.Id);
        await sys.MonthlyBudgetEntries.Where(e => e.CategoryId == id && e.ItemType == type && projects.Contains(e.Budget.ProjectId)).ExecuteDeleteAsync(ct);
        await sys.BudgetContributionSplits.Where(e => e.CategoryId == id && e.ItemType == type && projects.Contains(e.Budget.ProjectId)).ExecuteDeleteAsync(ct);
        await sys.CategoryTypeAssignments.Where(e => e.CategoryId == id && e.ItemType == type && projects.Contains(e.ProjectId)).ExecuteDeleteAsync(ct);
        if (type == CategoryItemType.Category)
        {
            await sys.ProjectTrackingItems.Where(e => e.CategoryId == id && projects.Contains(e.ProjectId)).ExecuteDeleteAsync(ct);
        }
        else
        {
            await sys.ProjectTrackingItems.Where(e => e.CategoryGroupId == id && e.CategoryId == null && projects.Contains(e.ProjectId)).ExecuteDeleteAsync(ct);
            await sys.ProjectCategoryAssignments.Where(e => e.CategoryId == id && projects.Contains(e.ProjectId)).ExecuteDeleteAsync(ct);
        }
    }

    private static async Task TombstoneAsync(BudgetDbContext sys, int sourceId, string kind, long id, CancellationToken ct)
    {
        if (await sys.RemovedImportItems.AnyAsync(r => r.SourceId == sourceId && r.Kind == kind && r.ItemId == id, ct)) return;
        sys.RemovedImportItems.Add(new RemovedImportItem { SourceId = sourceId, Kind = kind, ItemId = id });
        await sys.SaveChangesAsync(ct);
    }

    /// <summary>Why a category group cannot be deleted (it still has categories or sub-groups), or null.</summary>
    public async Task<bool> IsCategoryGroupEmptyAsync(long id, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return !await db.Categories.AnyAsync(c => c.GroupId == id, ct) && !await db.CategoryGroups.AnyAsync(g => g.ParentGroupId == id, ct);
    }

    /// <summary>Deletes an empty category group (refused while it has categories or sub-groups).</summary>
    public async Task DeleteCategoryGroupAsync(long id, CancellationToken ct = default)
    {
        int sourceId;
        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
        {
            var group = await db.CategoryGroups.FirstOrDefaultAsync(g => g.Id == id, ct) ?? throw NotFound();
            sourceId = group.SourceId;
            await RequireEditableAsync(db, sourceId, ct);
            if (SourceScope.SentinelIds(sourceId).Contains(id)) throw new InvalidOperationException("The root group cannot be deleted.");
        }
        await using var sys = await _system.CreateDbContextAsync(ct);
        if (await sys.Categories.AnyAsync(c => c.GroupId == id, ct) || await sys.CategoryGroups.AnyAsync(g => g.ParentGroupId == id, ct))
            throw new InvalidOperationException("Only an empty group can be deleted.");
        await using var tx = await sys.Database.BeginTransactionAsync(ct);
        await DeleteProjectRowsAsync(sys, sourceId, id, CategoryItemType.Group, ct);
        await sys.CategoryGroups.Where(g => g.Id == id).ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);
        Changed(sourceId, projects: true);
    }

    /// <summary>Deletes an account group; its accounts and sub-groups move up one level, to where the group was.</summary>
    public async Task DeleteAccountGroupAsync(long id, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var group = await db.AccountGroups.FirstOrDefaultAsync(g => g.Id == id, ct) ?? throw NotFound();
        await RequireEditableAsync(db, group.SourceId, ct);
        var (_, parent) = await LocateAsync(db, StructureItemKind.AccountGroup, id, ct);
        var children = await AccountSiblingsAsync(db, group.SourceId, id, ct);
        var siblings = await AccountSiblingsAsync(db, group.SourceId, parent, ct);
        var at = siblings.FindIndex(s => s.Kind == StructureItemKind.AccountGroup && s.Id == id);
        siblings.RemoveAt(at);
        siblings.InsertRange(at, children);
        Renumber(siblings);
        foreach (var child in children) await SetParentAsync(db, child.Kind, child.Id, parent, ct);
        db.AccountGroups.Remove(group);
        await db.SaveChangesAsync(ct);
        Changed(group.SourceId);
    }

    /// <summary>The account's name and how many bookings and statements deleting it removes; null when not visible.</summary>
    public async Task<AccountDeleteInfo?> GetAccountDeleteInfoAsync(long id, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var account = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);
        if (account == null) return null;
        await using var sys = await _system.CreateDbContextAsync(ct);
        var bookings = await sys.Transactions.CountAsync(t => t.SourceId == account.SourceId && t.AccountId == id && t.Splitted != SplitState.Child, ct);
        var statements = await sys.BankStatements.CountAsync(s => s.AccountId == id, ct);
        return new AccountDeleteInfo(id, account.Name ?? "", bookings, statements);
    }

    /// <summary>
    /// Deletes an account with all its bookings, statements (files too), positions and project assignments.
    /// <paramref name="confirmName"/> must repeat the account's name exactly. MoneyMoney does not bring it back.
    /// </summary>
    public async Task DeleteAccountAsync(long id, string confirmName, CancellationToken ct = default)
    {
        int sourceId;
        await using (var db = await _dbFactory.CreateDbContextAsync(ct))
        {
            var account = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct) ?? throw NotFound();
            sourceId = account.SourceId;
            await RequireEditableAsync(db, sourceId, ct);
            if (!string.Equals(account.Name ?? "", confirmName, StringComparison.Ordinal))
                throw new ArgumentException("The confirmation does not match the account's name.", nameof(confirmName));
        }

        List<string> files;
        await using (var sys = await _system.CreateDbContextAsync(ct))
        {
            await using var tx = await sys.Database.BeginTransactionAsync(ct);
            var bookings = sys.Transactions.Where(t => t.SourceId == sourceId && t.AccountId == id);
            await bookings.ExecuteDeleteAsync(ct);
            await sys.Holdings.Where(h => h.AccountId == id).ExecuteDeleteAsync(ct);
            await sys.PortfolioChartPoints.Where(p => p.AccountId == id).ExecuteDeleteAsync(ct);
            await sys.PpPortfolioLinks.Where(p => p.AccountId == id).ExecuteDeleteAsync(ct);
            await sys.ProjectAccountAssignments.Where(p => p.AccountId == id).ExecuteDeleteAsync(ct);
            // Rules restricted to this account (account_id=N) would never match again.
            var accountRules = (await sys.CategoryRules.Where(r => r.Expression.Contains("account_id")).Select(r => new { r.Id, r.Expression }).ToListAsync(ct))
                .Where(r => RuleExpression.ReferencesAccount(r.Expression, id)).Select(r => r.Id).ToList();
            if (accountRules.Count > 0) await sys.CategoryRules.Where(r => accountRules.Contains(r.Id)).ExecuteDeleteAsync(ct);
            await sys.BankLoginAccounts.Where(l => l.LinkedAccountId == id)
                .ExecuteUpdateAsync(s => s.SetProperty(l => l.LinkedAccountId, (long?)null).SetProperty(l => l.StatementsEnabled, false), ct);
            var statements = sys.BankStatements.Where(s => s.AccountId == id);
            files = await statements.Select(s => s.FilePath).ToListAsync(ct);
            await sys.StatementReads.Where(r => statements.Select(s => s.Id).Contains(r.StatementId)).ExecuteDeleteAsync(ct);
            await statements.ExecuteDeleteAsync(ct);
            await sys.Accounts.Where(a => a.Id == id).ExecuteDeleteAsync(ct);
            await TombstoneAsync(sys, sourceId, RemovedImportItem.AccountKind, id, ct);
            await tx.CommitAsync(ct);
        }
        if (_statementStore != null)
        {
            foreach (var f in files) _statementStore.DeleteFile(f);
        }
        Changed(sourceId, projects: true);
    }
}
