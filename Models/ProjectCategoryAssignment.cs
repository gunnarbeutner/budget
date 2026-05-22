using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace OpenBudget.Models;

/// <summary>
/// Assigns a MoneyMoney category to a project with spending limit
/// </summary>
[Table("project_category_assignments")]
public class ProjectCategoryAssignment
{
    [Key]
    public int Id { get; set; }

    public int ProjectId { get; set; }

    public long CategoryId { get; set; }

    /// <summary>
    /// Monthly spending limit for this category in this project
    /// </summary>
    public decimal MonthlyLimit { get; set; }

    // Navigation properties
    public Project Project { get; set; } = null!;

    // Note: Category is in a different database (MoneyMoney),
    // so we can't have a navigation property here.
    // Use CategoryId to look up the category from MoneyMoneyDbContext.
}
