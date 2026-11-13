using Microsoft.EntityFrameworkCore;

namespace OpenBudget.Services;

/// <summary>
/// The account groups each account lies in (its own group and every ancestor), for <c>account_group=N</c> terms and
/// category "Restrict to" checks. Loaded once per run.
/// </summary>
public sealed class AccountGroupIndex
{
    private static readonly IReadOnlySet<long> None = new HashSet<long>();
    private readonly Dictionary<long, IReadOnlySet<long>> _byAccount;

    public AccountGroupIndex(IReadOnlyDictionary<long, long?> accountGroups, IReadOnlyDictionary<long, long?> groupParents)
    {
        _byAccount = [];
        foreach (var (accountId, groupId) in accountGroups)
        {
            var set = new HashSet<long>();
            var g = groupId;
            while (g is { } id && set.Add(id)) g = groupParents.GetValueOrDefault(id);
            _byAccount[accountId] = set;
        }
    }

    public static readonly AccountGroupIndex Empty = new(new Dictionary<long, long?>(), new Dictionary<long, long?>());

    /// <summary>The groups (own group and its ancestors) of an account; empty when unknown.</summary>
    public IReadOnlySet<long> GroupsOf(long? accountId) =>
        accountId is { } id && _byAccount.TryGetValue(id, out var set) ? set : None;

    /// <summary>Loads the groups of the given accounts (all of their owners' group trees).</summary>
    public static async Task<AccountGroupIndex> LoadAsync(BudgetDbContext db, IReadOnlyCollection<long> accountIds, CancellationToken ct)
    {
        if (accountIds.Count == 0) return Empty;
        var accounts = new Dictionary<long, long?>();
        var owners = new HashSet<string>(StringComparer.Ordinal);
        foreach (var chunk in accountIds.Distinct().Chunk(500))
        {
            foreach (var a in await db.Accounts.AsNoTracking().IgnoreQueryFilters().Where(a => chunk.Contains(a.Id))
                         .Select(a => new { a.Id, a.GroupId, a.OwnerUserId }).ToListAsync(ct))
            {
                accounts[a.Id] = a.GroupId;
                owners.Add(a.OwnerUserId);
            }
        }
        var ownerList = owners.ToList();
        var groups = await db.AccountGroups.AsNoTracking().IgnoreQueryFilters().Where(g => ownerList.Contains(g.OwnerUserId))
            .Select(g => new { g.Id, g.ParentGroupId }).ToListAsync(ct);
        return new AccountGroupIndex(accounts, groups.ToDictionary(g => g.Id, g => g.ParentGroupId));
    }
}

/// <summary>The "Restrict to" settings of categories: which bookings a category's rules and suggestions may touch.</summary>
public sealed class CategoryRestrictions
{
    private readonly Dictionary<long, (long? Account, long? Group)> _restricted;

    public CategoryRestrictions(IReadOnlyDictionary<long, (long? Account, long? Group)> restricted, AccountGroupIndex groups)
    {
        _restricted = restricted.Where(kv => kv.Value.Account != null || kv.Value.Group != null).ToDictionary(kv => kv.Key, kv => kv.Value);
        Groups = groups;
    }

    public static readonly CategoryRestrictions None = new(new Dictionary<long, (long?, long?)>(), AccountGroupIndex.Empty);

    public AccountGroupIndex Groups { get; }

    /// <summary>Whether category <paramref name="categoryId"/> may be set on a booking of <paramref name="accountId"/>.</summary>
    public bool Allows(long categoryId, long? accountId) =>
        !_restricted.TryGetValue(categoryId, out var r) || Allows(r.Account, r.Group, accountId, Groups);

    public static bool Allows(long? restrictAccountId, long? restrictGroupId, long? accountId, AccountGroupIndex groups) =>
        restrictAccountId is { } a ? accountId == a
        : restrictGroupId is not { } g || groups.GroupsOf(accountId).Contains(g);

    /// <summary>Loads the restrictions of the given categories and the group membership of the given accounts.</summary>
    public static async Task<CategoryRestrictions> LoadAsync(BudgetDbContext db, IEnumerable<long> categoryIds, IReadOnlyCollection<long> accountIds, CancellationToken ct)
    {
        var ids = categoryIds.Distinct().ToList();
        var restricted = new Dictionary<long, (long?, long?)>();
        foreach (var chunk in ids.Chunk(500))
        {
            foreach (var c in await db.Categories.AsNoTracking().IgnoreQueryFilters()
                         .Where(c => chunk.Contains(c.Id) && (c.RestrictAccountId != null || c.RestrictAccountGroupId != null))
                         .Select(c => new { c.Id, c.RestrictAccountId, c.RestrictAccountGroupId }).ToListAsync(ct))
                restricted[c.Id] = (c.RestrictAccountId, c.RestrictAccountGroupId);
        }
        var groups = await AccountGroupIndex.LoadAsync(db, accountIds, ct);
        return new CategoryRestrictions(restricted, groups);
    }
}
