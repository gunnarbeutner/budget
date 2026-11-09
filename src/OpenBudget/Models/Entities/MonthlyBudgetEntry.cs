using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using OpenBudget.Services;

namespace OpenBudget.Models;

/// <summary>
/// Monthly budget breakdown for a specific category within a yearly budget
/// </summary>
[Table("monthly_budget_entries")]
public class MonthlyBudgetEntry
{
    [Key]
    public int Id { get; set; }

    [Required]
    public int BudgetId { get; set; }

    [Required]
    public long CategoryId { get; set; }

    /// <summary>
    /// Whether CategoryId refers to a CategoryGroup or a leaf Category. The two source-app
    /// tables share a numeric ID space, so this discriminator is required to identify the row.
    /// </summary>
    public CategoryItemType ItemType { get; set; } = CategoryItemType.Group;

    // 12 monthly amounts for the year
    [Column(TypeName = "decimal(18,2)")]
    public decimal Month1 { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Month2 { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Month3 { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Month4 { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Month5 { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Month6 { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Month7 { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Month8 { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Month9 { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Month10 { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Month11 { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Month12 { get; set; }

    // Navigation properties
    public Budget Budget { get; set; } = null!;

    // Helper method to get amount for a specific month
    public decimal GetMonthAmount(int month)
    {
        return month switch
        {
            1 => Month1,
            2 => Month2,
            3 => Month3,
            4 => Month4,
            5 => Month5,
            6 => Month6,
            7 => Month7,
            8 => Month8,
            9 => Month9,
            10 => Month10,
            11 => Month11,
            12 => Month12,
            _ => throw new ArgumentOutOfRangeException(nameof(month), "Month must be between 1 and 12")
        };
    }

    // Helper method to set amount for a specific month
    public void SetMonthAmount(int month, decimal amount)
    {
        switch (month)
        {
            case 1: Month1 = amount; break;
            case 2: Month2 = amount; break;
            case 3: Month3 = amount; break;
            case 4: Month4 = amount; break;
            case 5: Month5 = amount; break;
            case 6: Month6 = amount; break;
            case 7: Month7 = amount; break;
            case 8: Month8 = amount; break;
            case 9: Month9 = amount; break;
            case 10: Month10 = amount; break;
            case 11: Month11 = amount; break;
            case 12: Month12 = amount; break;
            default: throw new ArgumentOutOfRangeException(nameof(month), "Month must be between 1 and 12");
        }
    }

    // Helper method to get total year amount
    public decimal GetYearTotal()
    {
        return Month1 + Month2 + Month3 + Month4 + Month5 + Month6 +
               Month7 + Month8 + Month9 + Month10 + Month11 + Month12;
    }
}
