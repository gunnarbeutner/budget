using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace OpenBudget.Models;

/// <summary>
/// User-defined project (named grouping of categories)
/// </summary>
[Table("projects")]
public class Project
{
    [Key]
    public int Id { get; set; }

    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Display order on the dashboard. Lower values come first. Reorder buttons swap with
    /// the neighboring project. New projects get the largest existing value + 1.
    /// </summary>
    public int SortOrder { get; set; }

    // Navigation properties
    public ICollection<ProjectCategoryAssignment> CategoryAssignments { get; set; } = new List<ProjectCategoryAssignment>();
    public ICollection<ProjectTrackingItem> TrackingItems { get; set; } = new List<ProjectTrackingItem>();
    public ICollection<CategoryTypeAssignment> CategoryTypeAssignments { get; set; } = new List<CategoryTypeAssignment>();
    public ICollection<ProjectAccountAssignment> AccountAssignments { get; set; } = new List<ProjectAccountAssignment>();
}
