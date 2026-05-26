using System.Globalization;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests;

public class FormattingTests
{
    [Theory]
    [InlineData(0.0, "€0.00")]
    [InlineData(12.34, "€12.34")]
    [InlineData(-12.34, "-€12.34")]
    [InlineData(1000.5, "€1,000.50")]
    public void FormatMoney_uses_euro_symbol_by_default(decimal value, string expected)
    {
        var prior = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            Assert.Equal(expected, Formatting.FormatMoney(value));
        }
        finally
        {
            CultureInfo.CurrentCulture = prior;
        }
    }

    [Theory]
    [InlineData(50.0, "USD", "USD 50.00")]
    [InlineData(-50.0, "USD", "-USD 50.00")]
    [InlineData(50.0, "EUR", "€50.00")]
    public void FormatMoney_uses_currency_prefix_for_non_EUR(decimal value, string currency, string expected)
    {
        var prior = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            Assert.Equal(expected, Formatting.FormatMoney(value, currency));
        }
        finally
        {
            CultureInfo.CurrentCulture = prior;
        }
    }
}
