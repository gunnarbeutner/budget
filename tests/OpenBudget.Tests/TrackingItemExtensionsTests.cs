using OpenBudget.Models;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests;

public class TrackingItemExtensionsTests
{
    [Fact]
    public void Group_id_folds_to_Group_key()
    {
        var items = new[]
        {
            new ProjectTrackingItem { CategoryGroupId = 7, CategoryId = null },
        };

        var keys = items.ToTrackedKeys().ToList();

        Assert.Single(keys);
        Assert.Equal((7L, CategoryItemType.Group), keys[0]);
    }

    [Fact]
    public void Category_id_folds_to_Category_key()
    {
        var items = new[]
        {
            new ProjectTrackingItem { CategoryGroupId = null, CategoryId = 42 },
        };

        var keys = items.ToTrackedKeys().ToList();

        Assert.Single(keys);
        Assert.Equal((42L, CategoryItemType.Category), keys[0]);
    }

    [Fact]
    public void Rows_with_neither_id_are_skipped()
    {
        // A malformed row (both nulls) shouldn't yield a (0, Group) ghost key.
        var items = new[]
        {
            new ProjectTrackingItem { CategoryGroupId = null, CategoryId = null },
            new ProjectTrackingItem { CategoryGroupId = 1, CategoryId = null },
        };

        var keys = items.ToTrackedKeys().ToList();

        Assert.Single(keys);
        Assert.Equal((1L, CategoryItemType.Group), keys[0]);
    }
}
