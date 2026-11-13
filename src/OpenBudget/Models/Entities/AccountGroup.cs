using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace OpenBudget.Models;

/// <summary>
/// imported account group imported into our DB. Mirrors how <see cref="CategoryGroup"/>
/// stores its tree: <see cref="Id"/> and <see cref="ParentGroupId"/> are namespaced via
/// <c>RowIds.Namespace</c> in its owner's id space.
/// </summary>
[Table("account_groups")]
public class AccountGroup
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

    /// <summary>The Bootstrap icon its owner picked; null = the folder. Set from MoneyMoney's icon by the import; later uploads never change it.</summary>
    [Column("icon")]
    public string? Icon { get; set; }

    /// <summary>The colour (hex) its owner picked for the icon; null = the default text colour. Set from MoneyMoney's icon by the import; later uploads never change it.</summary>
    [Column("color")]
    public string? Color { get; set; }

    // Navigation properties
    public AccountGroup? ParentGroup { get; set; }
    public ICollection<AccountGroup> SubGroups { get; set; } = new List<AccountGroup>();
    public ICollection<Account> Accounts { get; set; } = new List<Account>();
}
