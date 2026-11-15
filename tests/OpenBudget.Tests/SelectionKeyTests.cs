using OpenBudget.Services;
using OpenBudget.Shared;
using Xunit;

namespace OpenBudget.Tests;

public class SelectionKeyTests
{
    [Theory]
    [InlineData(SelectionKind.Category, 12, "cat-12")]
    [InlineData(SelectionKind.Group, 3, "grp-3")]
    [InlineData(SelectionKind.Account, 549755813895, "acct-549755813895")]
    public void Round_trips(SelectionKind kind, long id, string text)
    {
        var key = new SelectionKey(kind, id);
        Assert.Equal(text, key.ToString());
        Assert.True(SelectionKey.TryParse(text, out var parsed));
        Assert.Equal(key, parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("cat-")]
    [InlineData("cat-x")]
    [InlineData("g5")]
    [InlineData("uncategorized")]
    public void Rejects_other_values(string? text) => Assert.False(SelectionKey.TryParse(text, out _));

    [Fact]
    public void Maps_tree_node_types()
    {
        Assert.Equal(SelectionKey.Group(4), SelectionKey.Of(CategoryItemType.Group, 4));
        Assert.Equal(CategoryItemType.Category, SelectionKey.Of(CategoryItemType.Category, 4).ItemType);
    }

    [Fact]
    public void Institution_keys_round_trip()
    {
        var text = InstitutionKey.Format("Example Bank: Depot");
        Assert.Equal("bank:Example Bank: Depot", text);
        Assert.True(InstitutionKey.TryParse(text, out var institution));
        Assert.Equal("Example Bank: Depot", institution);
        Assert.False(InstitutionKey.TryParse("42", out _));
        Assert.False(InstitutionKey.TryParse(null, out _));
    }
}
