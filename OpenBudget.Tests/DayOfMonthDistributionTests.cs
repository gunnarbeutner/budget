using OpenBudget.Models;
using Xunit;

namespace OpenBudget.Tests;

public class DayOfMonthDistributionTests
{
    [Fact]
    public void Day1Fallback_puts_full_weight_on_day_1()
    {
        var dist = DayOfMonthDistribution.Day1Fallback();
        Assert.Equal(1m, dist.Weights[0]);
        for (int i = 1; i < 31; i++) Assert.Equal(0m, dist.Weights[i]);
        Assert.Equal(DistributionPatternSource.Day1Fallback, dist.Source);
    }

    [Fact]
    public void FromSample_normalises_by_amount_share_and_sums_to_one()
    {
        // 100 on day 1, 300 on day 15. Day 1 should land at 0.25, day 15 at 0.75.
        var sample = new List<(decimal AbsAmount, int DayOfMonth)>
        {
            (100m, 1),
            (300m, 15),
        };

        var dist = DayOfMonthDistribution.FromSample(sample, sample.Count, DistributionPatternSource.TrailingYear);

        Assert.Equal(0.25m, dist.Weights[0]);
        Assert.Equal(0.75m, dist.Weights[14]);
        Assert.Equal(1m, dist.Weights.Sum());
        Assert.Equal(DistributionPatternSource.TrailingYear, dist.Source);
        Assert.Equal(2, dist.SampleSize);
    }

    [Fact]
    public void FromSample_falls_back_to_count_based_when_amounts_net_to_zero()
    {
        // Refunds netted to zero — total amount is 0 but we still want to keep the timing
        // signal. The fallback uses count-share (each entry contributes 1/N to its day).
        var sample = new List<(decimal AbsAmount, int DayOfMonth)>
        {
            (0m, 1),
            (0m, 1),
            (0m, 15),
            (0m, 30),
        };

        var dist = DayOfMonthDistribution.FromSample(sample, sample.Count, DistributionPatternSource.AllTime);

        Assert.Equal(0.5m, dist.Weights[0]);   // 2 of 4 entries on day 1
        Assert.Equal(0.25m, dist.Weights[14]); // 1 of 4 on day 15
        Assert.Equal(0.25m, dist.Weights[29]); // 1 of 4 on day 30
    }

    [Fact]
    public void FromSample_uniform_when_sample_is_completely_empty()
    {
        var dist = DayOfMonthDistribution.FromSample(
            Array.Empty<(decimal, int)>(),
            sampleSize: 0,
            DistributionPatternSource.AllTime);

        for (int i = 0; i < 31; i++)
            Assert.Equal(1m / 31m, dist.Weights[i]);
    }

    [Fact]
    public void FromSample_ignores_out_of_range_day_of_month()
    {
        // Day 0 and day 32 are not valid; they should be dropped from the share calculation.
        var sample = new List<(decimal AbsAmount, int DayOfMonth)>
        {
            (100m, 0),  // skipped
            (100m, 15), // counted
            (100m, 32), // skipped
        };

        var dist = DayOfMonthDistribution.FromSample(sample, sample.Count, DistributionPatternSource.AllTime);

        Assert.Equal(1m, dist.Weights[14]);
        Assert.Equal(0m, dist.Weights[0]);
        Assert.Equal(1m, dist.Weights.Sum());
    }
}
