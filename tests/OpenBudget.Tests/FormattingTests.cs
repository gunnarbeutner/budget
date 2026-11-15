using System.Globalization;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests;

/// <summary>Money is shown in German format app-wide ("−1.234,56 €"), whatever the request culture.</summary>
public class FormattingTests
{
    [Theory]
    [InlineData(0.0, "0,00 €")]
    [InlineData(12.34, "12,34 €")]
    [InlineData(-12.34, "−12,34 €")]
    [InlineData(1000.5, "1.000,50 €")]
    public void FormatMoneyDe_ignores_the_current_culture(decimal value, string expected)
    {
        var prior = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        try
        {
            Assert.Equal(expected, Formatting.FormatMoneyDe(value));
        }
        finally
        {
            CultureInfo.CurrentCulture = prior;
        }
    }

    [Theory]
    [InlineData(50.0, "USD", "50,00 USD")]
    [InlineData(-50.0, "USD", "−50,00 USD")]
    [InlineData(50.0, "EUR", "50,00 €")]
    public void FormatMoneyDe_puts_other_currency_codes_after_the_amount(decimal value, string currency, string expected) =>
        Assert.Equal(expected, Formatting.FormatMoneyDe(value, currency));

    [Fact]
    public void Dates_use_the_app_format_whatever_the_current_culture()
    {
        var prior = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        try
        {
            Assert.Equal("05.11.2025", new DateOnly(2025, 11, 5).ToAppDate());
            Assert.Equal("20.08.2026", new DateTime(2026, 8, 20, 23, 59, 0).ToAppDate());
            Assert.Equal("08.2026", new DateOnly(2026, 8, 20).ToAppMonth());
            Assert.Equal("08.2026", new DateTime(2026, 8, 20).ToAppMonth());
        }
        finally
        {
            CultureInfo.CurrentCulture = prior;
        }
    }
}
