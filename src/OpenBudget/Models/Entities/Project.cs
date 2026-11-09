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

    /// <summary>
    /// import source this project draws categories and accounts from. Set at creation
    /// and immutable — re-binding would invalidate every category/account assignment.
    /// </summary>
    public int SourceId { get; set; } = 1;

    public ImportSource? Source { get; set; }

    /// <summary>
    /// Optional parent project. When set, the Cashflow page rolls this project's tracked
    /// flows into the parent's chart and per-category breakdown — but each project keeps
    /// its own budgets and budget editor scope. Parent and child must share the same
    /// SourceId; cycles and self-parenting are rejected at the service layer. Currently
    /// only single-level hierarchies are supported (a parent cannot itself have a parent).
    /// </summary>
    public int? ParentProjectId { get; set; }
    public Project? Parent { get; set; }
    public ICollection<Project> Children { get; set; } = new List<Project>();

    // Navigation properties
    public ICollection<ProjectCategoryAssignment> CategoryAssignments { get; set; } = new List<ProjectCategoryAssignment>();
    public ICollection<ProjectTrackingItem> TrackingItems { get; set; } = new List<ProjectTrackingItem>();
    public ICollection<CategoryTypeAssignment> CategoryTypeAssignments { get; set; } = new List<CategoryTypeAssignment>();
    public ICollection<ProjectAccountAssignment> AccountAssignments { get; set; } = new List<ProjectAccountAssignment>();
}
