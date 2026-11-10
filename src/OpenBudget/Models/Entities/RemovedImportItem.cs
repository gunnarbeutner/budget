using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace OpenBudget.Models;

/// <summary>
/// An account or category the owner deleted in OpenBudget after the structure takeover. MoneyMoney may keep reporting
/// it in its uploads; the import skips it (and an account's bookings) instead of bringing it back.
/// </summary>
[Table("removed_import_items")]
public class RemovedImportItem
{
    public const string AccountKind = "account";
    public const string CategoryKind = "category";

    [Key]
    public int Id { get; set; }

    public int SourceId { get; set; }

    /// <summary><see cref="AccountKind"/> or <see cref="CategoryKind"/>.</summary>
    [MaxLength(20)]
    public string Kind { get; set; } = AccountKind;

    /// <summary>The namespaced id of the deleted account / category.</summary>
    public long ItemId { get; set; }

    public DateTime RemovedAt { get; set; } = DateTime.UtcNow;
}
