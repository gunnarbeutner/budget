using System.Globalization;
using Microsoft.Extensions.Localization;

namespace OpenBudget.Services;

/// <summary>
/// Display helpers shared across pages. Pure: no DB access, no Blazor lifecycle.
/// </summary>
public static class Formatting
{
    /// <summary>
    /// Render a money amount with a currency symbol (€ for EUR, otherwise the code as a
    /// prefix). Negative amounts get the minus sign in front of the symbol.
    /// </summary>
    public static string FormatMoney(decimal value, string currency = "EUR")
    {
        var symbol = currency == "EUR" ? "€" : (currency + " ");
        return value < 0
            ? $"-{symbol}{Math.Abs(value):N2}"
            : $"{symbol}{value:N2}";
    }

    /// <summary>
    /// Human-readable relative age — "just now", "5 minutes ago", "3 days ago", "2 months ago".
    /// Caller passes its IStringLocalizer; the keys (RelativeAge_Future, _JustNow,
    /// _MinutesAgo, _HoursAgo, _DaysAgo, _MonthsAgo) must exist on the page's resx.
    /// </summary>
    public static string FormatRelativeAge(TimeSpan age, IStringLocalizer L)
    {
        if (age.TotalSeconds < 0) return L["RelativeAge_Future"];
        if (age.TotalMinutes < 1) return L["RelativeAge_JustNow"];
        if (age.TotalHours < 1) return L["RelativeAge_MinutesAgo", (int)age.TotalMinutes];
        if (age.TotalDays < 1) return L["RelativeAge_HoursAgo", (int)age.TotalHours];
        if (age.TotalDays < 30) return L["RelativeAge_DaysAgo", (int)age.TotalDays];
        return L["RelativeAge_MonthsAgo", (int)(age.TotalDays / 30)];
    }
}

/// <summary>
/// Fixed rgba palette for Chart.js datasets. Templates contain a "{0}" placeholder for the
/// alpha component so callers can vary opacity (e.g. 0.8 for actual bars, 0.3 for budget
/// overlays, 1 for borders) without resorting to string.Replace tricks.
/// </summary>
public static class ChartPalette
{
    private static readonly string[] _templates = new[]
    {
        "rgba(255, 99, 132, {0})",
        "rgba(54, 162, 235, {0})",
        "rgba(255, 206, 86, {0})",
        "rgba(75, 192, 192, {0})",
        "rgba(153, 102, 255, {0})",
        "rgba(255, 159, 64, {0})",
        "rgba(199, 199, 199, {0})",
        "rgba(83, 102, 255, {0})",
        "rgba(255, 99, 255, {0})",
        "rgba(99, 255, 132, {0})"
    };

    /// <summary>Returns <paramref name="count"/> color templates, cycling through the palette.</summary>
    public static string[] Templates(int count)
    {
        var result = new string[count];
        for (int i = 0; i < count; i++) result[i] = _templates[i % _templates.Length];
        return result;
    }

    /// <summary>Apply the given alpha to a template returned by <see cref="Templates"/>.</summary>
    public static string WithAlpha(string template, double alpha)
        => string.Format(CultureInfo.InvariantCulture, template, alpha);
}
