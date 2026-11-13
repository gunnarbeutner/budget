using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace OpenBudget.Models;

/// <summary>
/// A user's connection to their Microsoft account (OneDrive / OneDrive for Business). Strictly personal: only the
/// owner can see or change it (query filter and write guard in <see cref="Services.BudgetDbContext"/>). The refresh
/// token is stored AES-GCM encrypted (see <see cref="Connectors.SecretsProtector"/>); access tokens are never stored.
/// </summary>
[Table("cloud_connections")]
public class CloudConnection
{
    [Key]
    public int Id { get; set; }

    [Required]
    [MaxLength(450)]
    public string OwnerUserId { get; set; } = string.Empty;

    /// <summary>The provider, currently always <see cref="CloudProviders.OneDrive"/>.</summary>
    [Required]
    [MaxLength(50)]
    public string Provider { get; set; } = CloudProviders.OneDrive;

    /// <summary>The signed-in Microsoft account (user principal name or display name), shown on the settings page.</summary>
    [MaxLength(300)]
    public string? AccountName { get; set; }

    /// <summary>Encrypted refresh token; rotated tokens returned by the token endpoint replace it.</summary>
    public string? EncryptedRefreshToken { get; set; }

    public DateTime ConnectedAt { get; set; } = DateTime.UtcNow;

    /// <summary>True when Microsoft rejected the refresh token (consent revoked or expired): the user must reconnect.</summary>
    public bool NeedsReconnect { get; set; }

    [MaxLength(1000)]
    public string? LastError { get; set; }

    public List<CloudFileLink> Files { get; set; } = [];
}

public static class CloudProviders
{
    public const string OneDrive = "onedrive";
}

/// <summary>
/// A file in the user's cloud storage that OpenBudget reads server-side, identified by drive and item id (both
/// stable across renames and moves). The file is a PortfolioPerformance file whose data it updates for its owner.
/// </summary>
[Table("cloud_file_links")]
public class CloudFileLink
{
    [Key]
    public int Id { get; set; }

    public int ConnectionId { get; set; }

    public CloudConnection Connection { get; set; } = null!;

    [Required]
    [MaxLength(450)]
    public string OwnerUserId { get; set; } = string.Empty;

    [Required]
    [MaxLength(200)]
    public string DriveId { get; set; } = string.Empty;

    [Required]
    [MaxLength(200)]
    public string ItemId { get; set; } = string.Empty;

    /// <summary>Path shown to the user when the file was picked (informational; the ids are authoritative).</summary>
    [MaxLength(1000)]
    public string DisplayPath { get; set; } = string.Empty;

    /// <summary>Content tag of the last processed version; an unchanged tag skips the download.</summary>
    [MaxLength(200)]
    public string? LastCTag { get; set; }

    [MaxLength(200)]
    public string? LastETag { get; set; }

    public DateTime? RemoteModifiedAt { get; set; }

    public DateTime? LastCheckedAt { get; set; }

    public DateTime? LastImportedAt { get; set; }

    [MaxLength(1000)]
    public string? LastError { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
