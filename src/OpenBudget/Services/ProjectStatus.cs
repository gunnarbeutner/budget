using Microsoft.Extensions.Localization;
using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>
/// Shared "how is this project doing?" derivations used by the project switcher, the project list,
/// the sidebar's budget badge and the Transactions summary strip, so they can never drift on thresholds or wording.
///
/// The alert builder follows the same convention as <see cref="Formatting.FormatRelativeAge"/>:
/// the caller passes its own <see cref="IStringLocalizer"/>, and the refresh-error / uncategorized
/// keys (Freshness_RefreshErrors_One/_Other,
/// Uncategorized_One/_Other, plus the RelativeAge_* set) must exist on that page's resx.
/// </summary>
public static class ProjectStatus
{
    /// <summary>Overall spend as a percentage of budget (0 when there's no budget to divide by).</summary>
    public static decimal OverallPercent(ProjectSpendingSummary s)
        => s.TotalBudget > 0 ? s.TotalSpent / s.TotalBudget * 100 : 0;

    /// <summary>
    /// The success / warning / danger token driving status dots, progress bars and amount colours.
    /// Budgeted projects grade on spend-vs-budget; un-budgeted ones grade on whether net is positive.
    /// </summary>
    public static string StatusClass(ProjectSpendingSummary s)
    {
        if (s.HasBudget)
        {
            var pct = OverallPercent(s);
            return pct > 100 ? "danger" : pct > 80 ? "warning" : "success";
        }
        return s.MonthNet >= 0 ? "success" : "danger";
    }

    /// <summary>
    /// Colour of a spent-of-budget bar: red once over the budget, amber from 85 % on, green below. Nothing budgeted
    /// counts as on track unless something was spent against it.
    /// </summary>
    public static string BarClass(decimal spent, decimal budget)
    {
        if (budget <= 0) return spent > 0 ? "danger" : "success";
        if (spent > budget) return "danger";
        return spent / budget * 100 >= 85 ? "warning" : "success";
    }

    /// <summary>Width of a spent-of-budget bar in percent (0–100), formatted for CSS.</summary>
    public static string BarWidth(decimal spent, decimal budget)
    {
        var pct = budget > 0 ? Math.Clamp(spent / budget * 100, 0, 100) : (spent > 0 ? 100 : 0);
        return pct.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "%";
    }

    /// <summary>
    /// Per-project diagnostic. Returns null when there's nothing to warn about; otherwise a
    /// (severity, message-lines) tuple. Severity drives the badge / icon colour.
    /// </summary>
    public static (string Severity, List<string> Lines)? BuildAlert(
        ProjectSpendingSummary summary,
        IReadOnlyDictionary<string, DataFreshness> freshnessByOwner,
        IReadOnlyDictionary<string, UncategorizedSummary>? uncategorized,
        IStringLocalizer L)
    {
        var lines = new List<string>();
        string severity = "secondary";

        // Accounts of the project owner whose refresh failed.
        if (freshnessByOwner.TryGetValue(summary.OwnerUserId, out var f))
        {
            var errorAccounts = f.AccountsWithErrors.ToList();
            if (errorAccounts.Count > 0)
            {
                var errorKey = errorAccounts.Count == 1 ? "Freshness_RefreshErrors_One" : "Freshness_RefreshErrors_Other";
                lines.Add($"{L[errorKey, errorAccounts.Count]} {string.Join(", ", errorAccounts.Select(a => a.Name ?? $"#{a.AccountId}"))}.");
                if (severity == "secondary") severity = "warning";
            }
        }

        // Uncategorized transactions of this project's owner: roll into numbers that may undercount.
        if (uncategorized != null && uncategorized.TryGetValue(summary.OwnerUserId, out var u) && u.Count > 0)
        {
            var key = u.Count == 1 ? "Uncategorized_One" : "Uncategorized_Other";
            lines.Add(L[key, u.Count, Formatting.FormatMoneyDe(u.AbsoluteAmount)].Value);
            var uncatSev = u.Count switch
            {
                >= 50 => "danger",
                >= 10 => "warning",
                _ => "secondary"
            };
            severity = MaxSeverity(severity, uncatSev);
        }

        return lines.Count > 0 ? (severity, lines) : null;
    }

    private static string MaxSeverity(string a, string b)
    {
        static int Rank(string s) => s switch { "danger" => 2, "warning" => 1, _ => 0 };
        return Rank(a) >= Rank(b) ? a : b;
    }
}
