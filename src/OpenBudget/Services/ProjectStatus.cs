using Microsoft.Extensions.Localization;
using OpenBudget.Models;

namespace OpenBudget.Services;

/// <summary>
/// Shared "how is this project doing?" derivations used by both the dashboard portfolio
/// list and the project Overview tab, so the two can never drift on thresholds or wording.
///
/// The alert builder follows the same convention as <see cref="Formatting.FormatRelativeAge"/>:
/// the caller passes its own <see cref="IStringLocalizer"/>, and the freshness / uncategorized
/// keys (Freshness_Uploaded, Freshness_Unknown, Freshness_RefreshErrors_One/_Other,
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
    /// Per-project diagnostic. Returns null when there's nothing to warn about; otherwise a
    /// (severity, message-lines) tuple. Severity drives the badge / icon colour.
    /// </summary>
    public static (string Severity, List<string> Lines)? BuildAlert(
        ProjectSpendingSummary summary,
        IReadOnlyDictionary<int, DataFreshness> freshnessBySourceId,
        IReadOnlyDictionary<int, UncategorizedSummary>? uncategorized,
        IStringLocalizer L,
        DateTime nowUtc)
    {
        var lines = new List<string>();
        string severity = "secondary";

        // Freshness: stale upload or per-account refresh errors for the project's source.
        if (freshnessBySourceId.TryGetValue(summary.SourceId, out var f))
        {
            var age = f.DbUploadedUtc.HasValue ? nowUtc - f.DbUploadedUtc.Value : (TimeSpan?)null;
            var freshSev = age switch
            {
                null => "warning",
                { TotalDays: > 7 } => "danger",
                { TotalHours: > 36 } => "warning",
                _ => null
            };
            if (freshSev != null)
            {
                string ageLabel = age.HasValue ? Formatting.FormatRelativeAge(age.Value, L) : L["Freshness_Unknown"];
                var uploadedExtra = f.DbUploadedUtc.HasValue
                    ? $" ({f.DbUploadedUtc.Value.ToLocalTime():yyyy-MM-dd HH:mm})"
                    : "";
                lines.Add($"{L["Freshness_Uploaded"]} {ageLabel}{uploadedExtra}.");
                severity = freshSev;
            }

            var errorAccounts = f.AccountsWithErrors.ToList();
            if (errorAccounts.Any())
            {
                var errorKey = errorAccounts.Count == 1 ? "Freshness_RefreshErrors_One" : "Freshness_RefreshErrors_Other";
                lines.Add($"{L[errorKey, errorAccounts.Count]} {string.Join(", ", errorAccounts.Select(a => a.Name ?? $"#{a.AccountId}"))}.");
                if (severity == "secondary") severity = "warning";
            }
        }

        // Uncategorized transactions in this project's source: roll into numbers that may undercount.
        if (uncategorized != null && uncategorized.TryGetValue(summary.SourceId, out var u) && u.Count > 0)
        {
            var key = u.Count == 1 ? "Uncategorized_One" : "Uncategorized_Other";
            lines.Add(L[key, u.Count, $"€{u.AbsoluteAmount:N2}"].Value);
            var uncatSev = u.Count switch
            {
                >= 50 => "danger",
                >= 10 => "warning",
                _ => "secondary"
            };
            severity = MaxSeverity(severity, uncatSev);
        }

        return lines.Any() ? (severity, lines) : null;
    }

    private static string MaxSeverity(string a, string b)
    {
        static int Rank(string s) => s switch { "danger" => 2, "warning" => 1, _ => 0 };
        return Rank(a) >= Rank(b) ? a : b;
    }
}
