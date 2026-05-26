using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests;

public class MoneyMathTests
{
    [Theory]
    [InlineData(0.0, 0L)]
    [InlineData(1.0, 100L)]
    [InlineData(12.34, 1234L)]
    [InlineData(-12.34, -1234L)]
    [InlineData(0.01, 1L)]
    public void EurosToCents_round_trips_typical_amounts(double euros, long expectedCents)
    {
        Assert.Equal(expectedCents, MoneyMath.EurosToCents(euros));
    }

    [Theory]
    // Banker's rounding rounds half-even — both 0.005 and 0.015 land on the even neighbor.
    [InlineData(0.005, 0L)]
    [InlineData(0.015, 2L)]
    [InlineData(0.025, 2L)]
    [InlineData(0.035, 4L)]
    public void EurosToCents_uses_bankers_rounding_on_half_cent_boundaries(double euros, long expectedCents)
    {
        // Pinning this: anyone "fixing" to MidpointRounding.AwayFromZero would silently shift
        // half a cent per transaction. Banker's matches the C# default and the EF Core
        // cents column we settled on.
        Assert.Equal(expectedCents, MoneyMath.EurosToCents(euros));
    }
}
