using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace OpenBudget.Models;

/// <summary>
/// MoneyMoney account (read-only from MoneyMoney database)
/// </summary>
[Table("accounts")]
public class Account
{
    [Key]
    [Column("rowid")]
    public long Id { get; set; }

    [Column("name")]
    public string? Name { get; set; }

    [Column("iban")]
    public string? Iban { get; set; }

    [Column("currency")]
    public string? Currency { get; set; }

    // Navigation properties
    public ICollection<Transaction> Transactions { get; set; } = new List<Transaction>();
}
