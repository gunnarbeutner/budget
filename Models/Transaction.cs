using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace OpenBudget.Models;

/// <summary>
/// MoneyMoney transaction imported into our DB. <see cref="Id"/> encodes the source via
/// <c>SourceScope.Namespace(SourceId, rowid)</c>; <see cref="SourceId"/> is duplicated as a
/// real column for indexed filtering.
/// </summary>
[Table("mm_transactions")]
public class Transaction
{
    [Key]
    public long Id { get; set; }

    public int SourceId { get; set; }

    [Column("local_account_key")]
    public long? AccountId { get; set; }

    [Column("amount")]
    public double Amount { get; set; }

    [Column("currency")]
    public string? Currency { get; set; }

    [Column("timestamp")]
    public long Timestamp { get; set; }

    [Column("name")]
    public string? Name { get; set; }

    [Column("comment")]
    public string? Comment { get; set; }

    [Column("category_key")]
    public long? CategoryId { get; set; }

    [Column("checkmark")]
    public long? Checkmark { get; set; }

    /// <summary>
    /// MoneyMoney split marker. 0 = normal row, 1 = split parent (a placeholder; its real
    /// category lives on the children), 2 = split child. Parents must be excluded from any
    /// rollup or "uncategorized" count or they double-count against their children.
    /// </summary>
    [Column("splitted")]
    public long? Splitted { get; set; }

    [NotMapped]
    public DateTime Date => DateTimeOffset.FromUnixTimeSeconds(Timestamp).DateTime;

    // Navigation properties
    public Category? Category { get; set; }
    public Account? Account { get; set; }
}
