using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace OpenBudget.Models;

/// <summary>
/// Represents a category group or individual category that a project tracks for budgeting/analysis.
/// This defines the granularity level - whether we track at group level or drill down to individual categories.
/// </summary>
[Table("project_tracking_items")]
public class ProjectTrackingItem
{
    [Key]
    public int Id { get; set; }

    [Required]
    public int ProjectId { get; set; }

    /// <summary>
    /// The category group ID being tracked (null if tracking an individual category instead)
    /// </summary>
    public long? CategoryGroupId { get; set; }

    /// <summary>
    /// The individual category ID being tracked (null if tracking a category group instead)
    /// </summary>
    public long? CategoryId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation property
    public Project Project { get; set; } = null!;
}
