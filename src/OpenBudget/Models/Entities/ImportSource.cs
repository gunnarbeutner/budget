using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace OpenBudget.Models;

/// <summary>
/// Named imported data source. Each source carries its own set of imported rows; the
/// source's <see cref="Id"/> namespaces every imported rowid so multiple source-app
/// installations can coexist in one budgets.db without ID collisions.
/// </summary>
[Table("import_sources")]
public class ImportSource
{
    [Key]
    public int Id { get; set; }

    /// <summary>URL-safe slug used in the upload endpoint path and as the dashboard label.</summary>
    [Required]
    [MaxLength(100)]
    public string Name { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Last-write time of the most recent successful import for this source.</summary>
    public DateTime? LastUploadAt { get; set; }

    public long? LastUploadSizeBytes { get; set; }

    /// <summary>
    /// Owning user (AppUser.Id). Null only for legacy rows not yet claimed through the CLI; an
    /// unowned source is invisible to everyone. Accounts and transactions belong to the owner.
    /// </summary>
    [MaxLength(450)]
    public string? OwnerUserId { get; set; }

    /// <summary>
    /// Per-source upload token (bearer). Authorizes uploads to this source only. Stored in clear
    /// because the owner can view it on the Sources page. Null until generated; the legacy global
    /// token (see LegacyCompat) keeps working for every source in addition.
    /// </summary>
    [MaxLength(100)]
    public string? UploadToken { get; set; }

    public ICollection<Project> Projects { get; set; } = new List<Project>();
}
