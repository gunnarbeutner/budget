using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using OpenBudget.Services;

namespace OpenBudget.Models;

/// <summary>
/// Assigns income/expense type to a category for a specific project
/// Stored in our DB to persist across MoneyMoney DB reimports
/// </summary>
[Table("category_type_assignments")]
public class CategoryTypeAssignment
{
    [Key]
    public int Id { get; set; }

    public int ProjectId { get; set; }

    public long CategoryId { get; set; }

    /// <summary>
    /// Type of item: Group (CategoryGroup) or Category (leaf category)
    /// </summary>
    public CategoryItemType ItemType { get; set; }

    /// <summary>
    /// True = Income, False = Expense
    /// </summary>
    public bool IsIncome { get; set; }

    // Navigation properties
    public Project Project { get; set; } = null!;

    // Note: Category is in a different database (MoneyMoney),
    // so we can't have a navigation property here.
    // Use CategoryId to look up the category from MoneyMoneyDbContext.
}
