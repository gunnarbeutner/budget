using Microsoft.AspNetCore.Identity;

namespace OpenBudget.Models;

/// <summary>
/// An OpenBudget login. Users are created only through the CLI (<c>users add</c>); there is no
/// public sign-up. <see cref="IsAdmin"/> only grants user management (via the CLI) — there is no
/// admin override for viewing other users' data.
/// </summary>
public class AppUser : IdentityUser
{
    public bool IsAdmin { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Longest display name kept (longer input is cut).</summary>
    public const int DisplayNameMaxLength = 60;

    /// <summary>
    /// The name other users see (project sharing, the top bar); null shows the user name instead.
    /// Not unique and never used to look a user up.
    /// </summary>
    [System.ComponentModel.DataAnnotations.MaxLength(DisplayNameMaxLength)]
    public string? DisplayName { get; set; }

    /// <summary>Trims the input, cuts it to <see cref="DisplayNameMaxLength"/>; blank means none (null).</summary>
    public static string? NormalizeDisplayName(string? input)
    {
        var t = input?.Trim();
        if (string.IsNullOrEmpty(t)) return null;
        return t.Length > DisplayNameMaxLength ? t[..DisplayNameMaxLength].TrimEnd() : t;
    }

    /// <summary>The display name when set, else the user name.</summary>
    public static string ShownName(string? displayName, string? userName) =>
        string.IsNullOrWhiteSpace(displayName) ? userName ?? "" : displayName;
}
