using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests;

public class MoneyTextTests
{
    [Theory]
    // PayPal CSV
    [InlineData("-1,234.50", "-1234.50")]
    [InlineData("-1.234,56", "-1234.56")]
    [InlineData("12,5", "12.5")]
    [InlineData("0.99", "0.99")]
    [InlineData("1.000", "1000")]
    [InlineData("1.234.567,89", "1234567.89")]
    [InlineData("(12,50)", "-12.50")]
    // Amazon order pages
    [InlineData("-€10.60", "-10.60")]
    [InlineData("−10,60 €", "-10.60")]
    [InlineData("+€5.00", "5.00")]
    [InlineData("EUR 23,99", "23.99")]
    // ING web banking
    [InlineData("1.234,56 €", "1234.56")]
    [InlineData("-12,50", "-12.50")]
    [InlineData("- 3.400,00", "-3400.00")]
    [InlineData("0,30-", "-0.30")]
    public void Parse_reads_german_and_english_amounts(string text, string expected) =>
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), MoneyText.Parse(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("€")]
    public void Parse_returns_null_without_digits(string? text) => Assert.Null(MoneyText.Parse(text));
}
