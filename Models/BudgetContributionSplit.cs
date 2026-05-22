using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using OpenBudget.Services;

namespace OpenBudget.Models;

/// <summary>
/// Per-budget contribution split for one income source — the percentage of the year's total
/// expenses that this source is expected to cover. Sums of percentages should be 100 but
/// the storage doesn't enforce that.
/// </summary>
[Table("budget_contribution_splits")]
public class BudgetContributionSplit
{
    [Key]
    public int Id { get; set; }

    [Required]
    public int BudgetId { get; set; }

    [Required]
    public long CategoryId { get; set; }

    /// <summary>Group vs Category discriminator; CategoryId and ItemType together identify the income source.</summary>
    public CategoryItemType ItemType { get; set; }

    [Column(TypeName = "decimal(5,2)")]
    public decimal Percent { get; set; }

    public Budget Budget { get; set; } = null!;
}
