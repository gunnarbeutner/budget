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

    // Navigation properties
    public ICollection<ProjectCategoryAssignment> CategoryAssignments { get; set; } = new List<ProjectCategoryAssignment>();
    public ICollection<ProjectTrackingItem> TrackingItems { get; set; } = new List<ProjectTrackingItem>();
}
