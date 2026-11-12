using System.Globalization;

namespace OpenBudget.Services;

/// <summary>Background and text colors of a category pill, for light and dark theme.</summary>
public sealed record CategoryPillColors(string Background, string Text, string DarkBackground, string DarkText)
{
    /// <summary>Inline CSS custom properties consumed by <c>.cat-pill</c> in site.css.</summary>
    public string Style => $"--cat-bg:{Background};--cat-fg:{Text};--cat-bg-d:{DarkBackground};--cat-fg-d:{DarkText};";
}

/// <summary>
/// Pastel category pills. A category's own <c>Color</c> (hex, imported from MoneyMoney or picked in OpenBudget) is turned
/// into a pastel background with a dark text of the same hue; without one a stable entry of a fixed palette is chosen
/// from the category id. Every pair keeps a text contrast of at least 4.5:1, in both themes.
/// </summary>
public static class CategoryColors
{
    /// <summary>
    /// The fixed palette of categories, accounts and account groups: (text color = the stored key, pastel background) for
    /// light mode. Ten hues spread around the colour wheel (red, orange, yellow, green, teal, cyan, blue, indigo, purple,
    /// pink) plus a neutral grey.
    /// </summary>
    public static readonly IReadOnlyList<(string Key, string Background)> Palette =
    [
        ("#b91c1c", "#fee2e2"),
        ("#a3480a", "#ffedd5"),
        ("#856404", "#fdf3c4"),
        ("#2a7330", "#e3f4e4"),
        ("#0f766e", "#d9f4f0"),
        ("#0a6a8a", "#dcf1f8"),
        ("#1d4ed8", "#e2eafd"),
        ("#4f3fc4", "#e9e7fc"),
        ("#7e22ce", "#f3e6fd"),
        ("#be185d", "#fde6f1"),
        ("#4b5563", "#eceef1"),
    ];

    /// <summary>
    /// The palette key closest to a stored colour (for a picker's selection state: colours stored before the palette
    /// changed, or imported from MoneyMoney, mark their nearest swatch); null when it does not parse.
    /// </summary>
    public static string? NearestSwatch(string? color)
    {
        if (!TryParse(color, out var rgb)) return null;
        return Palette.MinBy(p =>
        {
            var k = Parse(p.Key);
            return (k.R - rgb.R) * (k.R - rgb.R) + (k.G - rgb.G) * (k.G - rgb.G) + (k.B - rgb.B) * (k.B - rgb.B);
        }).Key;
    }

    /// <summary>
    /// Inline style tinting an icon in a stored colour: the readable text colour of its pill for light mode, and (as
    /// <c>--ob-tint-d</c>, applied by site.css) the one for dark mode; null without a colour.
    /// </summary>
    public static string? TintStyle(string? color)
    {
        if (!IsValid(color)) return null;
        var p = For(0, color);
        return $"color:{p.Text};--ob-tint-d:{p.DarkText};";
    }

    private const double MinContrast = 4.5;
    private static readonly (double R, double G, double B) White = (255, 255, 255);
    private static readonly (double R, double G, double B) Black = (0, 0, 0);
    private static readonly (double R, double G, double B) DarkSurface = (0x21, 0x25, 0x29);

    /// <summary>The palette colour of a category without its own colour (the one <see cref="For"/> uses for its pills).</summary>
    public static string FallbackColor(long categoryId) => Palette[(int)(Math.Abs(categoryId % Palette.Count))].Key;

    /// <summary>The pill colors for a category: its own color when it parses, otherwise the palette entry for its id.</summary>
    public static CategoryPillColors For(long categoryId, string? color)
    {
        if (TryParse(color, out var rgb))
        {
            var hex = ToHex(rgb);
            foreach (var (key, bg) in Palette)
            {
                if (string.Equals(key, hex, StringComparison.OrdinalIgnoreCase))
                    return Build(rgb, Parse(bg));
            }
            return Build(rgb, Mix(rgb, White, 0.85));
        }
        var entry = Palette[(int)(Math.Abs(categoryId % Palette.Count))];
        return Build(Parse(entry.Key), Parse(entry.Background));
    }

    /// <summary>Whether a string is a usable "#rgb" / "#rrggbb" color.</summary>
    public static bool IsValid(string? color) => TryParse(color, out _);

    /// <summary>WCAG contrast ratio of two hex colors.</summary>
    public static double Contrast(string a, string b) => Contrast(Parse(a), Parse(b));

    private static CategoryPillColors Build((double R, double G, double B) baseColor, (double R, double G, double B) lightBg)
    {
        var fg = baseColor;
        for (var i = 0; i < 30 && Contrast(fg, lightBg) < MinContrast; i++) fg = Mix(fg, Black, 0.15);

        var darkBg = Mix(baseColor, DarkSurface, 0.72);
        var darkFg = Mix(baseColor, White, 0.55);
        for (var i = 0; i < 30 && Contrast(darkFg, darkBg) < MinContrast; i++) darkFg = Mix(darkFg, White, 0.2);

        return new CategoryPillColors(ToHex(lightBg), ToHex(fg), ToHex(darkBg), ToHex(darkFg));
    }

    private static bool TryParse(string? color, out (double R, double G, double B) rgb)
    {
        rgb = default;
        if (string.IsNullOrWhiteSpace(color)) return false;
        if (color.Contains(',', StringComparison.Ordinal)) return TryParseComponents(color, out rgb);
        var s = color.Trim().TrimStart('#');
        if (s.Length == 3) s = string.Concat(s.Select(c => new string(c, 2)));
        if (s.Length != 6 || !int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return false;
        rgb = ((v >> 16) & 0xff, (v >> 8) & 0xff, v & 0xff);
        return true;
    }

    // MoneyMoney stores colors as "r,g,b[,a]": 0-255 integers, or 0-1 fractions.
    private static bool TryParseComponents(string color, out (double R, double G, double B) rgb)
    {
        rgb = default;
        var parts = color.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 3 or > 4) return false;
        var v = new double[3];
        for (var i = 0; i < 3; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v[i]) || v[i] < 0 || v[i] > 255) return false;
        }
        var fractions = v.All(x => x <= 1) && parts.Take(3).Any(p => p.Contains('.', StringComparison.Ordinal));
        var scale = fractions ? 255.0 : 1.0;
        rgb = (v[0] * scale, v[1] * scale, v[2] * scale);
        return true;
    }

    /// <summary>A stored color as CSS hex, or null when it does not parse.</summary>
    public static string? ToCss(string? color) => TryParse(color, out var rgb) ? ToHex(rgb) : null;

    private static (double R, double G, double B) Parse(string hex) =>
        TryParse(hex, out var rgb) ? rgb : throw new ArgumentException("Not a hex color.", nameof(hex));

    private static (double R, double G, double B) Mix((double R, double G, double B) a, (double R, double G, double B) b, double towardB) =>
        (a.R + (b.R - a.R) * towardB, a.G + (b.G - a.G) * towardB, a.B + (b.B - a.B) * towardB);

    private static string ToHex((double R, double G, double B) c) =>
        string.Create(CultureInfo.InvariantCulture, $"#{(int)Math.Round(c.R):x2}{(int)Math.Round(c.G):x2}{(int)Math.Round(c.B):x2}");

    private static double Luminance((double R, double G, double B) c)
    {
        static double Channel(double v)
        {
            v = Math.Round(v) / 255.0;
            return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    private static double Contrast((double R, double G, double B) a, (double R, double G, double B) b)
    {
        var la = Luminance(a);
        var lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }
}
