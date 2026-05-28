using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace OpenBudget.Models;

/// <summary>
/// MoneyMoney account group imported into our DB. Mirrors how <see cref="CategoryGroup"/>
/// stores its tree: <see cref="Id"/> and <see cref="ParentGroupId"/> are namespaced via
/// <c>SourceScope.Namespace</c> so multiple sources can coexist.
/// </summary>
[Table("mm_account_groups")]
public class AccountGroup
{
    [Key]
    public long Id { get; set; }

    public int SourceId { get; set; }

    [Column("name")]
    public string? Name { get; set; }

    [Column("group_key")]
    public long? ParentGroupId { get; set; }

    [Column("sort")]
    public long? Sort { get; set; }

    // Navigation properties
    public AccountGroup? ParentGroup { get; set; }
    public ICollection<AccountGroup> SubGroups { get; set; } = new List<AccountGroup>();
    public ICollection<Account> Accounts { get; set; } = new List<Account>();
}
