using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Globalization;
using System.Text.RegularExpressions;

namespace OpenBudget.Models;

/// <summary>
/// MoneyMoney account imported into our DB. <see cref="Id"/> is namespaced via
/// <c>SourceScope.Namespace(SourceId, rowid)</c> to avoid collisions across sources.
/// </summary>
[Table("mm_accounts")]
public class Account
{
    [Key]
    public long Id { get; set; }

    public int SourceId { get; set; }

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

    /// <summary>
    /// MoneyMoney <c>accounts.group_key</c>, mapped to <see cref="AccountGroup.Id"/> via
    /// the same namespacing scheme. Null for orphans; MoneyMoney itself anchors every
    /// account under a group in practice.
    /// </summary>
    [Column("group_key")]
    public long? GroupId { get; set; }

    /// <summary>
    /// Unix-seconds timestamp of MoneyMoney's last successful refresh of this account. Null for
    /// manually-maintained accounts (cash, real estate) that never auto-refresh.
    /// </summary>
    [Column("refresh_tsp")]
    public long? RefreshTimestamp { get; set; }

    /// <summary>
    /// Last refresh error message from MoneyMoney (e.g. login failure, MFA timeout). Null when
    /// the most recent refresh succeeded.
    /// </summary>
    [Column("error")]
    public string? Error { get; set; }

    // Navigation properties
    public ICollection<Transaction> Transactions { get; set; } = new List<Transaction>();
    public AccountGroup? Group { get; set; }

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
            var match = Regex.Match(Balances, @"\d+:(?<amount>-?\d+\.\d+)", RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(1));
            if (!match.Success) return null;
            return decimal.TryParse(match.Groups["amount"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                ? value
                : null;
        }
    }

    /// <summary>
    /// <see cref="RefreshTimestamp"/> as UTC <see cref="DateTime"/>, or null when not refreshed.
    /// </summary>
    [NotMapped]
    public DateTime? LastRefreshUtc => RefreshTimestamp.HasValue && RefreshTimestamp.Value > 0
        ? DateTimeOffset.FromUnixTimeSeconds(RefreshTimestamp.Value).UtcDateTime
        : null;

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

    /// <summary>
    /// Bootstrap-Icons class name for the account type. Picked to roughly match how
    /// MoneyMoney itself signals account kinds in its sidebar.
    /// </summary>
    [NotMapped]
    public string KindIcon => Type switch
    {
        1 => "bi-bank",
        2 => "bi-piggy-bank",
        3 => "bi-credit-card-2-front",
        4 => "bi-cash-stack",
        5 => "bi-pencil-square",
        6 => "bi-cash",
        7 => "bi-graph-up-arrow",
        8 => "bi-folder",
        _ => "bi-question-circle"
    };
}
