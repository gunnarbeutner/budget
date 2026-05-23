using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Globalization;
using System.Text.RegularExpressions;

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

    /// <summary>
    /// MoneyMoney account type code (1=giro, 2=savings, 4=credit, 6=cash/other, 7=depot, ...).
    /// </summary>
    [Column("type")]
    public long? Type { get; set; }

    /// <summary>
    /// 1 if this account aggregates portfolio positions (e.g. brokerage), else 0.
    /// </summary>
    [Column("portfolio")]
    public long? Portfolio { get; set; }

    /// <summary>
    /// Packed balance blob ("11:5203.710000,3:EUR,"). Use <see cref="CurrentBalance"/> to read it.
    /// </summary>
    [Column("balances")]
    public string? Balances { get; set; }

    [Column("sort")]
    public long? Sort { get; set; }

    // Navigation properties
    public ICollection<Transaction> Transactions { get; set; } = new List<Transaction>();

    /// <summary>
    /// Parses the first numeric amount out of MoneyMoney's "balances" string ("11:5203.710000,3:EUR,").
    /// Returns null when the column is empty or unparseable.
    /// </summary>
    [NotMapped]
    public decimal? CurrentBalance
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Balances)) return null;
            // Match "<tag>:<number>" pairs and pick the first one with a fractional part —
            // the currency entry shows up as "3:EUR" with no number, which the regex skips.
            var match = Regex.Match(Balances, @"\d+:(-?\d+\.\d+)");
            if (!match.Success) return null;
            return decimal.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;
        }
    }

    /// <summary>
    /// Short human label derived from <see cref="Type"/>. Falls back to the raw code.
    /// </summary>
    [NotMapped]
    public string KindLabel => Type switch
    {
        0 => "Other",
        1 => "Giro",
        2 => "Savings",
        3 => "Credit card",
        4 => "Loan",
        5 => "Manual",
        6 => "Cash",
        7 => "Portfolio",
        8 => "Group",
        null => "—",
        _ => $"Type {Type}"
    };
}
