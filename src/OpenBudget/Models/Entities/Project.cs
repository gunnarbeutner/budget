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
    /// Owning user (AppUser.Id). Null only for legacy rows not yet claimed through the CLI; an
    /// unowned project is invisible to everyone. Deliberately no database FK (see the migration).
    /// </summary>
    [MaxLength(450)]
    public string OwnerUserId { get; set; } = string.Empty;

    /// <summary>
    /// The owner's default project for new accounts: accounts that newly appear in one of the owner's
    /// data (MoneyMoney import, connector-created) are assigned to it. At most one per owner, only
    /// ever set on a project that is not shared (not itself and not through its parent) — sharing it
    /// clears the flag, so a new account can never become visible to a sharee automatically.
    /// </summary>
    public bool IsDefaultForNewAccounts { get; set; }

    /// <summary>
    /// Set when sharing (or re-parenting under a shared project) cleared <see cref="IsDefaultForNewAccounts"/>;
    /// the settings page shows a one-time notice until the owner dismisses it.
    /// </summary>
    public bool DefaultClearedNotice { get; set; }

    public ICollection<ProjectShare> Shares { get; set; } = new List<ProjectShare>();

    /// <summary>
    /// Optional parent project. When set, the Cashflow page rolls this project's tracked
    /// flows into the parent's chart and per-category breakdown — but each project keeps
    /// its own budgets and budget editor scope. Parent and child must share the same
    /// owner; cycles and self-parenting are rejected at the service layer. Currently
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
