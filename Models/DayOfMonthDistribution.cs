namespace OpenBudget.Models;

public enum DistributionPatternSource
{
    TrailingYear,
    AllTime,
    Day1Fallback
}

/// <summary>
/// Normalized 31-element day-of-month spending histogram for one tracked item.
/// <see cref="Weights"/>[i] is the share of monthly volume on day (i+1), summing to ~1.
/// </summary>
public class DayOfMonthDistribution
{
    public decimal[] Weights { get; set; } = new decimal[31];
    public DistributionPatternSource Source { get; set; }
    public int SampleSize { get; set; }

    public static DayOfMonthDistribution Day1Fallback()
    {
        var w = new decimal[31];
        w[0] = 1m;
        return new DayOfMonthDistribution { Weights = w, Source = DistributionPatternSource.Day1Fallback };
    }

    public static DayOfMonthDistribution FromSample(
        IReadOnlyCollection<(decimal AbsAmount, int DayOfMonth)> sample,
        int sampleSize,
        DistributionPatternSource source)
    {
        var weights = new decimal[31];
        decimal total = 0m;
        foreach (var (amt, day) in sample)
        {
            if (day < 1 || day > 31) continue;
            weights[day - 1] += amt;
            total += amt;
        }

        if (total > 0m)
        {
            for (int i = 0; i < 31; i++) weights[i] /= total;
        }
        else
        {
            // Zero-amount sample (rare — refunds netted to zero). Fall back to count-based by
            // re-walking; if even that is empty, distribute uniformly.
            int countTotal = sample.Count(s => s.DayOfMonth >= 1 && s.DayOfMonth <= 31);
            if (countTotal > 0)
            {
                foreach (var (_, day) in sample)
                {
                    if (day < 1 || day > 31) continue;
                    weights[day - 1] += 1m / countTotal;
                }
            }
            else
            {
                for (int i = 0; i < 31; i++) weights[i] = 1m / 31m;
            }
        }

        return new DayOfMonthDistribution
        {
            Weights = weights,
            Source = source,
            SampleSize = sampleSize
        };
    }
}
