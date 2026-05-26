using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests;

public class TimeRangeTests
{
    [Fact]
    public void Month_is_half_open_at_next_month_start()
    {
        var jan = TimeRange.Month(2026, 1);
        var feb = TimeRange.Month(2026, 2);
        Assert.Equal(jan.EndTsExclusive, feb.StartTs);
    }

    [Fact]
    public void Year_spans_jan1_to_next_jan1()
    {
        var y2026 = TimeRange.Year(2026);
        var jan = TimeRange.Month(2026, 1);
        var y2027 = TimeRange.Year(2027);
        Assert.Equal(jan.StartTs, y2026.StartTs);
        Assert.Equal(y2027.StartTs, y2026.EndTsExclusive);
    }

    [Fact]
    public void Between_round_trips_via_DateTime()
    {
        var start = new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2026, 4, 15, 0, 0, 0, DateTimeKind.Utc);
        var range = TimeRange.Between(start, end);
        Assert.Equal(((DateTimeOffset)start).ToUnixTimeSeconds(), range.StartTs);
        Assert.Equal(((DateTimeOffset)end).ToUnixTimeSeconds(), range.EndTsExclusive);
    }
}
