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

    public ICollection<Project> Projects { get; set; } = new List<Project>();
}
