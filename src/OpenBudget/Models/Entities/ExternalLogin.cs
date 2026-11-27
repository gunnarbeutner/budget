using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace OpenBudget.Models;

/// <summary>
/// A Microsoft (Entra ID) account linked to an OpenBudget user: the account's tenant id and object id. Signing in with
/// Microsoft and the MCP API's Entra access tokens find the user only through such a link; there is no matching by
/// e-mail and no account is ever created from a Microsoft sign-in. Linked by the user (Account page) or seeded by the
/// operator (<c>Auth:Microsoft:Links</c>).
/// </summary>
[Table("external_logins")]
public class ExternalLogin
{
    public const string MicrosoftProvider = "microsoft";

    [Key]
    public int Id { get; set; }

    [Required]
    [MaxLength(450)]
    public string OwnerUserId { get; set; } = string.Empty;

    [Required]
    [MaxLength(32)]
    public string Provider { get; set; } = MicrosoftProvider;

    /// <summary>The Entra tenant id (tid), lower-case GUID.</summary>
    [Required]
    [MaxLength(64)]
    public string TenantId { get; set; } = string.Empty;

    /// <summary>The Entra object id (oid) of the account, lower-case GUID.</summary>
    [Required]
    [MaxLength(64)]
    public string ObjectId { get; set; } = string.Empty;

    /// <summary>The account's name when it was linked (display only).</summary>
    [MaxLength(256)]
    public string? DisplayName { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
