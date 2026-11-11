using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace OpenBudget.Models;

/// <summary>
/// A user's own position of a project in their project list. Every user orders the projects they can see (owned
/// and shared) independently; projects without a row for the user come after the ordered ones, by
/// <see cref="Project.SortOrder"/> and name. Rows of projects the user can no longer see are ignored.
/// </summary>
[Table("user_project_order")]
public class UserProjectOrder
{
    [Key]
    public int Id { get; set; }

    [Required]
    [MaxLength(450)]
    public string UserId { get; set; } = string.Empty;

    public int ProjectId { get; set; }

    public int SortOrder { get; set; }
}
