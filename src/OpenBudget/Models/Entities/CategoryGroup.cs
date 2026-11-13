using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace OpenBudget.Models;

/// <summary>
/// imported category group imported into our DB. <see cref="Id"/> and
/// <see cref="ParentGroupId"/> are namespaced via <c>RowIds.Namespace</c>.
/// </summary>
[Table("category_groups")]
public class CategoryGroup
{
    [Key]
    public long Id { get; set; }

    /// <summary>The owning user (AppUser.Id).</summary>
    [Required]
    [MaxLength(450)]
    public string OwnerUserId { get; set; } = string.Empty;

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
