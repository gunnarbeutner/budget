using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests;

/// <summary>Text totals keep one sum per currency and never add different currencies.</summary>
public class CurrencySumsTests
{
    [Fact]
    public void Group_keeps_one_total_per_currency_and_treats_a_missing_currency_as_euro()
    {
        var sums = CurrencySums.Group([(null, 10m), ("USD", 20m), ("EUR", 5m), ("usd", -2m)]);
        Assert.Equal([new CurrencyAmount("EUR", 15m), new CurrencyAmount("USD", 18m)], sums);
    }

    [Fact]
    public void Format_puts_the_base_currency_first_and_the_others_alphabetically()
    {
        var sums = CurrencySums.Group([("USD", 190.94m), ("GBP", 3m), ("EUR", -175.5m)]);
        Assert.Equal("−175,50 € + 3,00 GBP + 190,94 USD", CurrencySums.Format(sums));
        Assert.Equal("190,94 USD − 175,50 € + 3,00 GBP", CurrencySums.Format(sums, "USD"));
    }

    [Fact]
    public void Format_of_a_single_currency_is_the_plain_amount()
    {
        Assert.Equal("40,00 €", CurrencySums.Format(CurrencySums.Group([("EUR", 20m), (null, 20m)])));
        Assert.Equal("20,00 USD", CurrencySums.Format(CurrencySums.Group([("USD", 20m)])));
    }

    [Fact]
    public void Format_of_nothing_is_zero_in_the_base_currency()
    {
        Assert.Equal("0,00 €", CurrencySums.Format([]));
        Assert.Equal("0,00 GBP", CurrencySums.Format([], "GBP"));
    }

    [Fact]
    public void AllNegative_needs_every_total_below_zero()
    {
        Assert.True(CurrencySums.AllNegative([new("EUR", -1m), new("USD", -2m)]));
        Assert.False(CurrencySums.AllNegative([new("EUR", -1m), new("USD", 2m)]));
        Assert.False(CurrencySums.AllNegative([]));
    }
}
