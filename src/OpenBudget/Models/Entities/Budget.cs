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

    /// <summary>
    /// Optimistic-concurrency token, bumped on every editor save (see
    /// <c>BudgetEntryService.SaveBudgetAsync</c>). The editor passes back the value it loaded; if
    /// another session saved in the meantime the row's version no longer matches and the save is
    /// rejected with a conflict instead of silently overwriting the other edit. Managed manually
    /// (incremented in code) because SQLite has no native rowversion.
    /// </summary>
    public int Version { get; set; }

    // Navigation properties
    public Project Project { get; set; } = null!;
    public ICollection<MonthlyBudgetEntry> MonthlyEntries { get; set; } = new List<MonthlyBudgetEntry>();
}
