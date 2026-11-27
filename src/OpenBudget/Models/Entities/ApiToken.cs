using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace OpenBudget.Models;

/// <summary>
/// A personal, read-only API token for the <c>/api/mcp/v1/</c> endpoints (see docs/mcp-api.md). Belongs to one user
/// and reads exactly what that user sees. Only the SHA-256 hash of the token is stored; the token itself is shown
/// once when it is created. Revoked or expired tokens are kept (they show in the list) but never authenticate.
/// </summary>
[Table("api_tokens")]
public class ApiToken
{
    /// <summary>Longest name kept.</summary>
    public const int NameMaxLength = 100;

    [Key]
    public int Id { get; set; }

    [Required]
    [MaxLength(450)]
    public string OwnerUserId { get; set; } = string.Empty;

    [Required]
    [MaxLength(NameMaxLength)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Lower-case hex SHA-256 of the token.</summary>
    [Required]
    [MaxLength(64)]
    public string TokenHash { get; set; } = string.Empty;

    /// <summary>The token's first characters, to tell tokens apart in the list.</summary>
    [Required]
    [MaxLength(16)]
    public string Prefix { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? LastUsedAt { get; set; }

    public DateTime? ExpiresAt { get; set; }

    public DateTime? RevokedAt { get; set; }

    public bool IsActive(DateTime nowUtc) => RevokedAt == null && (ExpiresAt == null || ExpiresAt > nowUtc);
}
