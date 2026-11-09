using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace OpenBudget.Models;

/// <summary>
/// What a user may do with a project. Ordered: each role includes the capabilities of the lower
/// ones. <see cref="Viewer"/> and <see cref="Editor"/> are the grantable share roles (stored in
/// <see cref="ProjectShare.Role"/>); <see cref="Owner"/> is derived from ownership and never stored
/// in a share row.
/// </summary>
public enum ProjectRole
{
    None = 0,
    Viewer = 1,
    Editor = 2,
    Owner = 3
}

/// <summary>
/// Shares a project with another user as viewer or editor. Sub-projects inherit their parent's
/// shares (resolved in <c>AccessScope</c>, not stored per child). Only the project's owner can
/// create, change or revoke shares.
/// </summary>
[Table("project_shares")]
public class ProjectShare
{
    [Key]
    public int Id { get; set; }

    public int ProjectId { get; set; }
    public Project Project { get; set; } = null!;

    [Required]
    public string UserId { get; set; } = string.Empty;
    public AppUser? User { get; set; }

    /// <summary>Viewer or Editor (never Owner).</summary>
    public ProjectRole Role { get; set; } = ProjectRole.Viewer;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
