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
}
