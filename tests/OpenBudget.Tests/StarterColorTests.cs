using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests;

/// <summary>Every starter group (and with it its categories) gets its own valid colour.</summary>
public sealed class StarterColorTests
{
    [Fact]
    public void Twelve_starter_groups_get_twelve_distinct_valid_colours()
    {
        var colors = Enumerable.Range(0, 12).Select(OnboardingService.StarterColor).ToList();
        Assert.All(colors, c => Assert.True(CategoryColors.IsValid(c), c));
        Assert.Equal(12, colors.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
