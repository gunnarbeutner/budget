using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace OpenBudget.Models;

/// <summary>
/// MoneyMoney category (read-only from MoneyMoney database)
/// </summary>
[Table("categories")]
public class Category
{
    [Key]
    [Column("rowid")]
    public long Id { get; set; }

    [Column("name")]
    public string? Name { get; set; }

    [Column("type")]
    public long? Type { get; set; }

    [Column("color")]
    public string? Color { get; set; }

    [Column("rules")]
    public string? Rules { get; set; }

    [Column("group_key")]
    public long? GroupId { get; set; }

    /// <summary>
    /// MoneyMoney's display order within the parent group. Null values sort last.
    /// </summary>
    [Column("sort")]
    public long? Sort { get; set; }

    // Navigation properties
    public ICollection<Transaction> Transactions { get; set; } = new List<Transaction>();

    // Reference to the category group this category belongs to
    public CategoryGroup? Group { get; set; }
}
