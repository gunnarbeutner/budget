using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests;

public class ProjectStatusTests
{
    [Theory]
    [InlineData(0, 100, "success")]
    [InlineData(84.99, 100, "success")]
    [InlineData(85, 100, "warning")]
    [InlineData(100, 100, "warning")]
    [InlineData(100.01, 100, "danger")]
    [InlineData(0, 0, "success")]
    [InlineData(5, 0, "danger")]
    public void Bar_class_is_green_below_85_percent_amber_up_to_the_budget_and_red_over_it(double spent, double budget, string expected) =>
        Assert.Equal(expected, ProjectStatus.BarClass((decimal)spent, (decimal)budget));

    [Theory]
    [InlineData(42, 100, "42%")]
    [InlineData(148.2, 100, "100%")]
    [InlineData(1, 3, "33.3%")]
    [InlineData(0, 0, "0%")]
    [InlineData(5, 0, "100%")]
    public void Bar_width_is_clamped_and_invariant(double spent, double budget, string expected) =>
        Assert.Equal(expected, ProjectStatus.BarWidth((decimal)spent, (decimal)budget));
}
