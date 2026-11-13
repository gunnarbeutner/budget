using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace OpenBudget.Models;

/// <summary>
/// imported category imported into our DB. <see cref="Id"/> is namespaced via
/// <c>RowIds.Namespace(OwnerUserId, rowid)</c>; <see cref="GroupId"/> uses the same
/// namespacing so it resolves into <see cref="CategoryGroup"/> within the same source.
/// </summary>
[Table("categories")]
public class Category
{
    [Key]
    public long Id { get; set; }

    /// <summary>The owning user (AppUser.Id).</summary>
    [Required]
    [MaxLength(450)]
    public string OwnerUserId { get; set; } = string.Empty;

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
    /// the source's display order within the parent group. Null values sort last.
    /// </summary>
    [Column("sort")]
    public long? Sort { get; set; }

    // Navigation properties
    public ICollection<Transaction> Transactions { get; set; } = new List<Transaction>();

    // Reference to the category group this category belongs to
    public CategoryGroup? Group { get; set; }
}
