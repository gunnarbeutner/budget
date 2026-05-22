using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace OpenBudget.Models;

/// <summary>
/// MoneyMoney category group - defines the category hierarchy
/// </summary>
[Table("category_groups")]
public class CategoryGroup
{
    [Key]
    [Column("rowid")]
    public long Id { get; set; }

    [Column("name")]
    public string? Name { get; set; }

    [Column("group_key")]
    public long? ParentGroupId { get; set; }

    [Column("sort")]
    public long? Sort { get; set; }

    [Column("color")]
    public string? Color { get; set; }

    // Navigation properties
    public CategoryGroup? ParentGroup { get; set; }
    public ICollection<CategoryGroup> SubGroups { get; set; } = new List<CategoryGroup>();
    public ICollection<Category> Categories { get; set; } = new List<Category>();
}
