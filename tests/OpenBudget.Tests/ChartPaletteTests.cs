using System.Globalization;
using OpenBudget.Services;
using Xunit;

namespace OpenBudget.Tests;

public class ChartPaletteTests
{
    [Fact]
    public void Templates_carry_a_placeholder_per_entry()
    {
        var templates = ChartPalette.Templates(5);
        Assert.Equal(5, templates.Length);
        Assert.All(templates, t => Assert.Contains("{0}", t));
    }

    [Fact]
    public void Templates_cycle_through_palette()
    {
        var palette = ChartPalette.Templates(20);
        // The palette is 10 entries; index 10 must match index 0, index 11 must match 1, etc.
        for (int i = 0; i < 10; i++)
            Assert.Equal(palette[i], palette[i + 10]);
    }

    [Fact]
    public void WithAlpha_uses_invariant_culture()
    {
        // A culture that formats decimals with a comma (e.g. de-DE) would break the rgba
        // string if WithAlpha used the current culture. Force one and assert the dot stays.
        var prior = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            var rgba = ChartPalette.WithAlpha("rgba(0, 0, 0, {0})", 0.5);
            Assert.Equal("rgba(0, 0, 0, 0.5)", rgba);
        }
        finally
        {
            CultureInfo.CurrentCulture = prior;
        }
    }
}
