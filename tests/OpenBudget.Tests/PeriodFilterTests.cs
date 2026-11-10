using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests;

/// <summary>The transaction list's period filter: the choices on offer and the date ranges they stand for.</summary>
public sealed class PeriodFilterTests
{
    private static readonly DateOnly Today = new(2026, 11, 10);

    [Fact]
    public void Choices_offer_rolling_windows_recent_months_quarters_years_and_custom()
    {
        var keys = PeriodFilter.Choices(Today).Select(c => c.Key).ToList();

        Assert.Equal(["all", "30d", "90d", "12m"], keys.Take(4));
        Assert.Equal(["m:2026-11", "m:2026-10", "m:2026-09", "m:2026-08", "m:2026-07", "m:2026-06"], keys.Skip(4).Take(6));
        Assert.Equal(["q:2026-4", "q:2026-3", "q:2026-2", "q:2026-1"], keys.Skip(10).Take(4));
        Assert.Equal(["y:2026", "y:2025", "y:2024", "custom"], keys.Skip(14));
    }

    [Fact]
    public void Quarters_and_months_roll_back_across_the_year_boundary()
    {
        var early = PeriodFilter.Choices(new DateOnly(2026, 2, 3)).Select(c => c.Key).ToList();
        Assert.Contains("m:2025-12", early);
        Assert.Contains("q:2025-4", early);
        Assert.Contains("q:2025-2", early);
        Assert.DoesNotContain("q:2025-1", early);               // only the last four quarters
    }

    [Theory]
    [InlineData("30d", "2026-10-12", "2026-11-11")]
    [InlineData("90d", "2026-08-13", "2026-11-11")]
    [InlineData("12m", "2025-11-11", "2026-11-11")]
    [InlineData("m:2026-02", "2026-02-01", "2026-03-01")]
    [InlineData("m:2026-12", "2026-12-01", "2027-01-01")]
    [InlineData("q:2026-3", "2026-07-01", "2026-10-01")]
    [InlineData("q:2026-4", "2026-10-01", "2027-01-01")]
    [InlineData("y:2025", "2025-01-01", "2026-01-01")]
    public void Keys_resolve_to_inclusive_start_and_exclusive_end(string key, string from, string toExclusive)
    {
        var (f, t) = PeriodFilter.Resolve(key, Today);
        Assert.Equal(DateOnly.Parse(from, System.Globalization.CultureInfo.InvariantCulture), f);
        Assert.Equal(DateOnly.Parse(toExclusive, System.Globalization.CultureInfo.InvariantCulture), t);
    }

    [Fact]
    public void Custom_includes_the_last_day_and_may_be_open_ended()
    {
        Assert.Equal((new DateOnly(2026, 3, 11), new DateOnly(2026, 3, 13)), PeriodFilter.Resolve("custom", Today, new DateOnly(2026, 3, 11), new DateOnly(2026, 3, 12)));
        Assert.Equal((new DateOnly(2026, 3, 11), null), PeriodFilter.Resolve("custom", Today, new DateOnly(2026, 3, 11), null));
    }

    [Theory]
    [InlineData("all")]
    [InlineData("")]
    [InlineData("m:2026-13")]
    [InlineData("q:2026-5")]
    [InlineData("y:abc")]
    [InlineData("nonsense")]
    public void Unknown_or_malformed_keys_mean_no_restriction(string key)
    {
        Assert.Equal((null, null), PeriodFilter.Resolve(key, Today));
    }
}
