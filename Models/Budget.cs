using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace OpenBudget.Models;

/// <summary>
/// Yearly budget plan for a project with monthly granularity
/// </summary>
[Table("budgets")]
public class Budget
{
    [Key]
    public int Id { get; set; }

    [Required]
    public int ProjectId { get; set; }

    [Required]
    public int Year { get; set; }

    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? LastModifiedAt { get; set; }

    // Navigation properties
    public Project Project { get; set; } = null!;
    public ICollection<MonthlyBudgetEntry> MonthlyEntries { get; set; } = new List<MonthlyBudgetEntry>();
}
