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

    /// <summary>
    /// Human-friendly label, defaulting to the creation date (YYYY-MM-DD) — editable. Lets the
    /// user keep multiple budgets for the same year (e.g. baseline vs revised) distinguishable.
    /// </summary>
    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    public string? Notes { get; set; }

    /// <summary>
    /// Exactly one budget per (project, year) should have IsActive=true; that's the one the
    /// dashboard rolls up. The service enforces the uniqueness when a budget is made active.
    /// </summary>
    public bool IsActive { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? LastModifiedAt { get; set; }

    // Navigation properties
    public Project Project { get; set; } = null!;
    public ICollection<MonthlyBudgetEntry> MonthlyEntries { get; set; } = new List<MonthlyBudgetEntry>();
}
