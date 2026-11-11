namespace OpenBudget.Services;

/// <summary>
/// The Bootstrap Icons an account or account group can pick. Accounts without a chosen icon show the icon of their
/// type (<see cref="Models.Account.IconForType"/>); account groups show <see cref="GroupDefault"/>.
/// </summary>
public static class AccountIcons
{
    public const string GroupDefault = "bi-folder";

    public static readonly IReadOnlyList<string> All =
    [
        "bi-bank", "bi-bank2", "bi-piggy-bank", "bi-wallet2", "bi-wallet", "bi-cash", "bi-cash-stack", "bi-cash-coin",
        "bi-coin", "bi-credit-card", "bi-credit-card-2-front", "bi-safe", "bi-safe2", "bi-graph-up-arrow", "bi-bar-chart-line",
        "bi-pie-chart", "bi-briefcase", "bi-building", "bi-house", "bi-house-heart", "bi-car-front", "bi-airplane",
        "bi-cart", "bi-gift", "bi-heart", "bi-people", "bi-person", "bi-mortarboard", "bi-shield-check", "bi-globe",
        "bi-star", "bi-folder",
    ];

    /// <summary>Whether a value is one of the offered icons.</summary>
    public static bool IsValid(string? icon) => icon != null && All.Contains(icon, StringComparer.Ordinal);

    /// <summary>The icon to show for an account: its chosen one, else the one of its type (depots: the chart icon).</summary>
    public static string ForAccount(string? icon, long? type, bool isPortfolio) =>
        IsValid(icon) ? icon! : isPortfolio ? "bi-graph-up-arrow" : Models.Account.IconForType(type);

    /// <summary>The icon to show for an account group: its chosen one, else the folder.</summary>
    public static string ForGroup(string? icon) => IsValid(icon) ? icon! : GroupDefault;

    /// <summary>An inline style tinting an icon in a stored colour, readable in both themes; null without one.</summary>
    public static string? TintStyle(string? color) => CategoryColors.TintStyle(color);
}
