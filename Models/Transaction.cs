using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace OpenBudget.Models;

/// <summary>
/// MoneyMoney transaction (read-only from MoneyMoney database)
/// </summary>
[Table("transactions")]
public class Transaction
{
    [Key]
    [Column("rowid")]
    public long Id { get; set; }

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

    [NotMapped]
    public DateTime Date => DateTimeOffset.FromUnixTimeSeconds(Timestamp).DateTime;

    // Navigation properties
    public Category? Category { get; set; }
    public Account? Account { get; set; }
}
