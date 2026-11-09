using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace OpenBudget.Models;

/// <summary>
/// imported category imported into our DB. <see cref="Id"/> is namespaced via
/// <c>SourceScope.Namespace(SourceId, rowid)</c>; <see cref="GroupId"/> uses the same
/// namespacing so it resolves into <see cref="CategoryGroup"/> within the same source.
/// </summary>
[Table("categories")]
public class Category
{
    [Key]
    public long Id { get; set; }

    public int SourceId { get; set; }

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

    /// <summary>
    /// "Show in charts" flag; once the source's structure is taken over (see <see cref="ImportSource.StructureTakenOverAt"/>)
    /// OpenBudget owns it. Defaults to true.
    /// </summary>
    [Column("show_in_charts")]
    public bool ShowInCharts { get; set; } = true;

    // Navigation properties
    public ICollection<Transaction> Transactions { get; set; } = new List<Transaction>();

    // Reference to the category group this category belongs to
    public CategoryGroup? Group { get; set; }
}
