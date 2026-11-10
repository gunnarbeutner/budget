using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>An account group as the rail needs it (imported from MoneyMoney's account groups).</summary>
/// <param name="KeepEmpty">Show the group even without visible accounts (it is empty in its source: just created, waiting to be filled).</param>
public sealed record AccountGroupInfo(long Id, long? ParentId, string Name, long? Sort, bool KeepEmpty = false);

/// <summary>A sum in one currency.</summary>
public sealed record CurrencyAmount(string Currency, decimal Amount);

/// <summary>
/// A node of the accounts tree: a group (with the sums of everything below it) or an account leaf.
/// </summary>
public sealed class AccountTreeNode
{
    /// <summary>Stable key for expand state: "g:&lt;id&gt;" for groups, <see cref="AccountTreeBuilder.OtherKey"/> for the ungrouped bucket.</summary>
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public BrowseAccountNode? Account { get; init; }
    public List<AccountTreeNode> Children { get; } = [];
    /// <summary>One entry per currency, EUR first. Empty for accounts without a balance.</summary>
    public List<CurrencyAmount> Sums { get; init; } = [];
    public bool IsGroup => Account == null;
}

/// <summary>
/// Builds the MoneyMoney-style accounts tree. Pure: no DB access, so the caller decides which accounts
/// and groups are visible (the access filters already did that) and this only arranges them.
/// <list type="bullet">
/// <item>Only groups that (transitively) contain a visible account appear; sums cover visible accounts only.</item>
/// <item>Siblings (sub-groups and accounts) are ordered by the imported sort value (nulls last), then name;
/// at equal sort a group precedes an account.</item>
/// <item>Accounts without a (known) group go to an "Other accounts" node at the end - or stay a flat
/// list when no groups exist at all.</item>
/// <item>A group sums per currency (balances are never converted): a group mixing EUR and USD shows both amounts.</item>
/// </list>
/// </summary>
public static class AccountTreeBuilder
{
    public const string OtherKey = "other";

    /// <summary>The node with <paramref name="key"/> anywhere in <paramref name="nodes"/>, or null.</summary>
    public static AccountTreeNode? Find(IEnumerable<AccountTreeNode> nodes, string key)
    {
        foreach (var n in nodes)
        {
            if (n.Key == key) return n;
            if (Find(n.Children, key) is { } found) return found;
        }
        return null;
    }

    /// <summary>The accounts below a node (its own account for a leaf), depth first.</summary>
    public static IEnumerable<BrowseAccountNode> Leaves(AccountTreeNode node) =>
        node.Account is { } a ? [a] : node.Children.SelectMany(Leaves);

    public static string GroupKey(long id) => "g:" + id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static List<AccountTreeNode> Build(
        IReadOnlyList<BrowseAccountNode> accounts, IReadOnlyList<AccountGroupInfo> groups, string otherName)
    {
        var groupById = groups.GroupBy(g => g.Id).ToDictionary(g => g.Key, g => g.First());
        var accountsByGroup = accounts
            .Where(a => a.GroupId is { } g && groupById.ContainsKey(g))
            .GroupBy(a => a.GroupId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());
        var ungrouped = accounts.Where(a => a.GroupId is not { } g || !groupById.ContainsKey(g)).ToList();

        var childGroups = groups
            .Where(g => g.ParentId is { } p && p != g.Id && groupById.ContainsKey(p))
            .GroupBy(g => g.ParentId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());
        var roots = groups.Where(g => g.ParentId is not { } p || p == g.Id || !groupById.ContainsKey(p)).ToList();

        var onPath = new HashSet<long>();
        var result = new List<(long? Sort, string Name, AccountTreeNode Node)>();
        foreach (var r in roots)
        {
            if (BuildGroup(r) is { } node) result.Add((r.Sort, r.Name, node));
        }
        var nodes = Order(result);

        if (ungrouped.Count > 0)
        {
            var leaves = ungrouped.OrderBy(a => a.Sort ?? long.MaxValue).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(Leaf).ToList();
            if (nodes.Count == 0)
            {
                nodes.AddRange(leaves);
            }
            else
            {
                var other = new AccountTreeNode { Key = OtherKey, Name = otherName, Sums = SumOf(ungrouped) };
                other.Children.AddRange(leaves);
                nodes.Add(other);
            }
        }
        return nodes;

        AccountTreeNode? BuildGroup(AccountGroupInfo g)
        {
            // Cycle guard: a malformed import must not recurse forever.
            if (!onPath.Add(g.Id)) return null;
            try
            {
                var items = new List<(long? Sort, string Name, AccountTreeNode Node)>();
                var below = new List<BrowseAccountNode>();
                if (childGroups.TryGetValue(g.Id, out var subs))
                {
                    foreach (var s in subs)
                    {
                        if (BuildGroup(s) is { } sub)
                        {
                            items.Add((s.Sort, s.Name, sub));
                            below.AddRange(Flatten(sub));
                        }
                    }
                }
                if (accountsByGroup.TryGetValue(g.Id, out var own))
                {
                    foreach (var a in own) { items.Add((a.Sort, a.Name, Leaf(a))); below.Add(a); }
                }
                if (items.Count == 0 && !g.KeepEmpty) return null;
                var node = new AccountTreeNode { Key = GroupKey(g.Id), Name = g.Name, Sums = SumOf(below) };
                node.Children.AddRange(Order(items));
                return node;
            }
            finally { onPath.Remove(g.Id); }
        }
    }

    private static AccountTreeNode Leaf(BrowseAccountNode a) => new()
    {
        Key = "a:" + a.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
        Name = a.Name,
        Account = a,
        Sums = SumOf([a]),
    };

    private static List<AccountTreeNode> Order(List<(long? Sort, string Name, AccountTreeNode Node)> items) =>
        items.OrderBy(i => i.Sort ?? long.MaxValue)
            .ThenBy(i => i.Node.IsGroup ? 0 : 1)
            .ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(i => i.Node).ToList();

    private static IEnumerable<BrowseAccountNode> Flatten(AccountTreeNode n) =>
        n.Account != null ? [n.Account] : n.Children.SelectMany(Flatten);

    private static List<CurrencyAmount> SumOf(IEnumerable<BrowseAccountNode> accounts) =>
        accounts.Where(a => a.Balance.HasValue)
            .GroupBy(a => string.IsNullOrWhiteSpace(a.Currency) ? "EUR" : a.Currency!, StringComparer.Ordinal)
            .Select(g => new CurrencyAmount(g.Key, g.Sum(a => a.Balance!.Value)))
            .OrderBy(c => c.Currency == "EUR" ? 0 : 1).ThenBy(c => c.Currency, StringComparer.Ordinal)
            .ToList();
}
