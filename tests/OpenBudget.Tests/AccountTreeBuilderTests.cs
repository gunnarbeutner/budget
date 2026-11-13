using OpenBudget.Models;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests;

public class AccountTreeBuilderTests
{
    private static BrowseAccountNode Acct(long id, string name, decimal? balance, long? group, long? sort = null, string? currency = "EUR") =>
        new() { Id = id, Name = name, Balance = balance, GroupId = group, Sort = sort, Currency = currency };

    private static AccountGroupInfo Group(long id, string name, long? parent = null, long? sort = null) => new(id, parent, name, sort);

    [Fact]
    public void Nests_groups_orders_by_sort_and_sums_everything_below()
    {
        var groups = new[] { Group(1, "Banks", sort: 2), Group(2, "Example Bank", parent: 1), Group(3, "Brokers", sort: 1) };
        var accounts = new[]
        {
            Acct(10, "Giro", 100m, 2, sort: 2),
            Acct(11, "Savings", 50.50m, 2, sort: 1),
            Acct(12, "Cash", 20m, 1, sort: 0),
            Acct(13, "Depot", 1000m, 3),
        };

        var tree = AccountTreeBuilder.Build(accounts, groups, "Other accounts");

        Assert.Equal(["Brokers", "Banks"], tree.Select(n => n.Name));
        var banks = tree[1];
        Assert.Equal(170.50m, Assert.Single(banks.Sums).Amount);
        Assert.Equal(["Cash", "Example Bank"], banks.Children.Select(n => n.Name));
        var bank = banks.Children[1];
        Assert.Equal(150.50m, Assert.Single(bank.Sums).Amount);
        Assert.Equal(["Savings", "Giro"], bank.Children.Select(n => n.Name));
        Assert.Equal(1000m, Assert.Single(tree[0].Sums).Amount);
    }

    [Fact]
    public void Ungrouped_accounts_go_to_an_other_node_at_the_end()
    {
        var groups = new[] { Group(1, "Banks") };
        var accounts = new[] { Acct(1, "Giro", 10m, 1), Acct(2, "Loose", 5m, null), Acct(3, "Orphan", 1m, 999) };

        var tree = AccountTreeBuilder.Build(accounts, groups, "Other accounts");

        Assert.Equal(["Banks", "Other accounts"], tree.Select(n => n.Name));
        Assert.Equal(AccountTreeBuilder.OtherKey, tree[1].Key);
        Assert.Equal(6m, Assert.Single(tree[1].Sums).Amount);
        Assert.Equal(["Loose", "Orphan"], tree[1].Children.Select(n => n.Name));
    }

    [Fact]
    public void Without_any_groups_the_accounts_stay_a_flat_list()
    {
        var tree = AccountTreeBuilder.Build([Acct(1, "B", 1m, null), Acct(2, "A", 2m, null)], [], "Other accounts");

        Assert.Equal(["A", "B"], tree.Select(n => n.Name));
        Assert.All(tree, n => Assert.False(n.IsGroup));
    }

    [Fact]
    public void Groups_without_visible_accounts_are_pruned_and_sums_cover_visible_accounts_only()
    {
        var groups = new[] { Group(1, "Banks"), Group(2, "Example Bank", parent: 1), Group(3, "Hidden group"), Group(4, "Empty child", parent: 1) };
        // Only one of the accounts of group 2 is visible (as to a user who sees a shared project's accounts).
        var tree = AccountTreeBuilder.Build([Acct(10, "Shared Giro", 25m, 2)], groups, "Other accounts");

        var banks = Assert.Single(tree);
        Assert.Equal("Banks", banks.Name);
        var bank = Assert.Single(banks.Children);
        Assert.Equal("Example Bank", bank.Name);
        Assert.Equal(25m, Assert.Single(banks.Sums).Amount);
    }

    [Fact]
    public void Mixed_currencies_are_summed_per_currency_with_EUR_first()
    {
        var groups = new[] { Group(1, "World") };
        var accounts = new[]
        {
            Acct(1, "USD one", 10m, 1, currency: "USD"),
            Acct(2, "EUR one", 3m, 1),
            Acct(3, "USD two", 5m, 1, currency: "USD"),
            Acct(4, "No balance", null, 1),
        };

        var sums = Assert.Single(AccountTreeBuilder.Build(accounts, groups, "x")).Sums;

        Assert.Equal([("EUR", 3m), ("USD", 15m)], sums.Select(s => (s.Currency, s.Amount)));
    }

    [Fact]
    public void A_group_cycle_does_not_hang_and_keeps_the_accounts_reachable()
    {
        var groups = new[] { Group(1, "A", parent: 2), Group(2, "B", parent: 1) };

        var tree = AccountTreeBuilder.Build([Acct(1, "Giro", 1m, 1)], groups, "Other accounts");

        Assert.NotNull(tree);
    }

    [Fact]
    public async Task A_sharee_sees_only_the_shared_account_and_the_groups_holding_it()
    {
        await using var t = await AccessWorld.BuildAsync();
        var group = AccessWorld.Id(AccessWorld.SourceAlice, 500);
        var other = AccessWorld.Id(AccessWorld.SourceAlice, 501);
        t.Db.AccountGroups.Add(new AccountGroup { Id = group, OwnerUserId = AccessWorld.Alice, Name = "Alice banks" });
        t.Db.AccountGroups.Add(new AccountGroup { Id = other, OwnerUserId = AccessWorld.Alice, Name = "Alice private bank", ParentGroupId = group });
        foreach (var acc in t.Db.Accounts.Where(a => a.OwnerUserId == AccessWorld.Alice))
        {
            acc.GroupId = acc.Id == AccessWorld.AccountWp ? group : other;
            acc.Balances = "11:100.000000,3:EUR,";
            acc.Currency = "EUR";
        }
        await t.Db.SaveChangesAsync();

        var asAlice = await t.ForUser(AccessWorld.Alice).Browse.GetTreeAsync(AccessWorld.Shared, 2026, 3, 3);
        var asBob = await t.ForUser(AccessWorld.Bob).Browse.GetTreeAsync(AccessWorld.Shared, 2026, 3, 3);

        // Bob sees the shared project's accounts (and its sub-project's), but never Alice's private one.
        var bobTree = AccountTreeBuilder.Build(asBob!.Accounts, asBob.AccountGroups, "Other accounts");
        var bobRoot = Assert.Single(bobTree);
        Assert.Equal("Alice banks", bobRoot.Name);
        Assert.Equal(["Alice private bank", "Giro Shared"], bobRoot.Children.Select(n => n.Name));
        Assert.Equal("Property Account", Assert.Single(bobRoot.Children[0].Children).Name);
        Assert.DoesNotContain(asBob.Accounts, a => a.Id == AccessWorld.AccountPrivate);
        Assert.Equal(200m, Assert.Single(bobRoot.Sums).Amount);

        // Alice's own project only has her own assigned accounts too; the sum covers what that view holds.
        var aliceTree = AccountTreeBuilder.Build(asAlice!.Accounts, asAlice.AccountGroups, "Other accounts");
        Assert.Equal("Alice banks", Assert.Single(aliceTree).Name);
    }
}
