using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace OpenBudget.Models;

/// <summary>
/// Assigns a imported category to a project. Budget amounts live on the Budget /
/// MonthlyBudgetEntry models — this record only expresses membership.
/// </summary>
[Table("project_category_assignments")]
public class ProjectCategoryAssignment
{
    [Key]
    public int Id { get; set; }

    public int ProjectId { get; set; }

    public long CategoryId { get; set; }

    // Navigation properties
    public Project Project { get; set; } = null!;

    // Note: Category is in a different database (the source app),
    // so we can't have a navigation property here.
    // Use CategoryId to look up the category from BudgetDbContext.
}
