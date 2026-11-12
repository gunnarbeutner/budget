using Microsoft.AspNetCore.WebUtilities;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests;

/// <summary>The project views' period: reading it from a URL (including the former filter's "per" keys) and its days.</summary>
public sealed class ViewPeriodTests
{
    private static readonly DateOnly Today = new(2026, 11, 10);
    private static readonly ViewPeriod Seed = new(2026, 11, "month");

    private static ViewPeriod Read(string query, ViewPeriod? seed = null) =>
        ViewPeriod.Read(QueryHelpers.ParseQuery(query), seed ?? Seed, Today);

    private static DateOnly D(string s) => DateOnly.Parse(s, System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void Month_ytd_and_year_come_from_year_month_and_period()
    {
        Assert.Equal(new ViewPeriod(2025, 3, "ytd"), Read("year=2025&month=3&period=ytd"));
        Assert.Equal(new ViewPeriod(2025, 11, "month"), Read("year=2025"));
        Assert.Equal(Seed, Read(""));
    }

    [Fact]
    public void A_range_needs_both_days_and_is_anchored_on_its_last_month()
    {
        var p = Read("period=range&from=2026-03-11&to=2026-04-02");
        Assert.True(p.IsRange);
        Assert.Equal((D("2026-03-11"), D("2026-04-02")), p.Days);
        Assert.Equal((2026, 4), (p.Year, p.Month));
        Assert.Equal((3, 4), p.Months);
        Assert.False(Read("period=range&from=2026-03-11").IsRange);
    }

    [Fact]
    public void A_remembered_range_survives_a_url_without_a_period()
    {
        var range = ViewPeriod.WithRange(D("2026-03-01"), D("2026-03-05"));
        Assert.Equal(range, Read("sel=cat-1", range));
        Assert.Equal(new ViewPeriod(2025, 3, "month"), Read("year=2025", range));
    }

    [Theory]
    [InlineData("per=y:2025", 2025, "year", null, null)]
    [InlineData("per=m:2026-02", 2026, "month", null, null)]
    [InlineData("per=q:2026-3", 2026, "range", "2026-07-01", "2026-09-30")]
    [InlineData("per=30d", 2026, "range", "2026-10-12", "2026-11-10")]
    [InlineData("per=90d", 2026, "range", "2026-08-13", "2026-11-10")]
    [InlineData("per=12m", 2026, "range", "2025-11-11", "2026-11-10")]
    [InlineData("per=custom&from=2026-03-11&to=2026-03-12", 2026, "range", "2026-03-11", "2026-03-12")]
    public void Former_filter_keys_map_onto_the_period(string query, int year, string mode, string? from, string? to)
    {
        var p = Read(query);
        Assert.Equal(year, p.Year);
        Assert.Equal(mode, p.Mode);
        if (from != null) Assert.Equal((D(from), D(to!)), p.Days);
    }

    [Theory]
    [InlineData("per=all")]
    [InlineData("per=")]
    [InlineData("per=m:2026-13")]
    [InlineData("per=q:2026-5")]
    [InlineData("per=y:abc")]
    [InlineData("per=custom")]
    [InlineData("per=nonsense")]
    public void Unknown_or_malformed_keys_leave_the_period_as_it_was(string query)
    {
        Assert.Equal(Seed, Read(query));
    }

    [Fact]
    public void The_query_carries_the_range_and_drops_the_former_key()
    {
        var q = ViewPeriod.WithRange(D("2026-03-12"), D("2026-03-11")).ToQuery();
        Assert.Equal("2026-03-11", q["from"]);
        Assert.Equal("range", q["period"]);
        Assert.True(q.ContainsKey("per") && q["per"] == null);
        Assert.Equal("year=2026&month=11", Seed.ToQueryString());
    }
}
